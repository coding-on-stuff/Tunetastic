using System.Collections.Concurrent;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace Tunetastic.Common.Operations;

/// <summary>
/// Provides services for managing and updating metadata related to music libraries in the application.
/// </summary>
public class LibraryScanner
{
	private static Task? _scanTask;
	private static bool _isScanning = false;

	/// <summary>
	/// Indicates whether a music library scan is currently in progress.
	/// </summary>
	public static bool IsScanning => _isScanning;

	/// <summary>
	/// Represents the progress of the library scanning operation as a percentage.
	/// </summary>
	public static double ScanProgress { get; private set; } = 0;

	/// <summary>
	/// Performs an asynchronous metadata update operation on the music libraries stored in the system.
	/// </summary>
	public async Task UpdateMetaData()
	{
		if (IsScanning) return;

		_isScanning = true;
		string type = "";
		string message = "";
		List<string> failedFiles = new List<string>();

		_scanTask = Task.Run(async () =>
		{
			(type, message, failedFiles) = await ScanLibraries();
		});

		await _scanTask;

		foreach (var failed in failedFiles)
			GlobalNotification.Error($"Failed to read metadata for:\n{failed}");

		switch (type)
		{
			case "Success":
				GlobalNotification.Success(message);
				break;
			case "Warning":
				GlobalNotification.Warning(message);
				break;
			case "Error":
				GlobalNotification.Error(message);
				break;
			default:
				break;
		}

		_isScanning = false;
		MusicPlayer.Instance.ResetOrReloadPlayer();
		TaskbarHelper.SetProgressState(App.Hwnd, TaskbarStates.NoProgress);
	}

	private async Task<(string, string, List<string>)> ScanLibraries()
	{
		TaskbarHelper.SetProgressState(App.Hwnd, TaskbarStates.Normal);
		ScanProgress = 0;
		TaskbarHelper.SetProgressValue(App.Hwnd, ScanProgress, 100);
		var audioFiles = new HashSet<string>();
		var foldersWithMusic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var libraries = new List<string>();

		foreach (LibraryModel library in await DatabaseHelper.Instance.GetAllLibraries())
		{
			libraries.Add(library.Path);
		}

		var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
		var ignoreTrackDuration = double.Parse(localSettings.Values[nameof(LocalSave.IgnoreTracksBelowDuration)]?.ToString() ?? "0");
		var ignoreDuplicates = bool.Parse(localSettings.Values[nameof(LocalSave.IgnoreDuplicateEnabled)]?.ToString() ?? "false");

		var extensions = await GetEnabledExtensions();

		var path = Path.Combine(Constants.ThumbnailsFolder);
		if (Directory.Exists(path)) Directory.Delete(path, true);

		ScanProgress = 0.5;

		if (libraries?.Count > 0)
		{
			var uniqueFolders = ComputeEffectiveRoots(libraries);

			var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

			foreach (var folder in uniqueFolders)
			{
				var files = Directory.EnumerateFiles(folder, "*.*", options)
									 .Where(file => extensions.Contains(Path.GetExtension(file).ToLower()));

				foreach (var file in files)
				{
					audioFiles.Add(file);
					foldersWithMusic.Add(Path.GetDirectoryName(file)!);
				}
			}

			var songsContainer = new ConcurrentBag<Song>();
			var scanMetaContainer = new ConcurrentBag<FileScanMeta>();
			var uniqueMetadata = new ConcurrentDictionary<(string Title, string Artist, string Album), byte>();

			ScanProgress = 1;
			TaskbarHelper.SetProgressValue(App.Hwnd, ScanProgress, 100);
			int processedFiles = 0;
			int totalFiles = audioFiles.Count;

			string probePath = uniqueFolders.Count > 0 ? uniqueFolders[0] : audioFiles.First();
			DiskKind diskKind = DiskSpeedDetector.GetDiskKind(probePath);
			int dop = DiskSpeedDetector.DopForKind(diskKind);

			var failedFiles = new ConcurrentBag<string>();
			await Parallel.ForEachAsync(
				audioFiles,
				new ParallelOptions { MaxDegreeOfParallelism = dop },
				async (filePath, ct) =>
				{
					var (song, succeeded, _, _, _) = await ExtractSongMetadata(filePath, ignoreTrackDuration);

					if (!succeeded)
					{
						failedFiles.Add(filePath);
					}

					if (song.Duration > ignoreTrackDuration && (!ignoreDuplicates || uniqueMetadata.TryAdd((song.Title, song.Artists, song.Album), 0)))
					{
						songsContainer.Add(song);
						scanMetaContainer.Add(BuildFileScanMeta(filePath));
					}

					int current = Interlocked.Increment(ref processedFiles);
					if (current % 10 == 0 || current == totalFiles)
					{
						ScanProgress = Math.Round(2 + ((double)(current * 97) / totalFiles), 2);
						TaskbarHelper.SetProgressValue(App.Hwnd, ScanProgress, 100);
					}
				}
			);

			try
			{
				await DatabaseHelper.Instance.UpdateSongsDatabase(songsContainer.ToList());
				await DatabaseHelper.Instance.WipeFileScanMeta();
				await DatabaseHelper.Instance.UpdateFileScanMeta(scanMetaContainer.ToList());
			}
			catch (Exception)
			{
				await DatabaseHelper.Instance.DeleteAllSongsFromDB();
				await RefreshAutoScanResultMessage(message: "No tracks could be added");
				TaskbarHelper.SetProgressState(App.Hwnd, TaskbarStates.Error);
				return ("Error", "No tracks could be added", failedFiles.ToList());
			}

			var librariesCount = libraries.Count;
			var songsCount = songsContainer.Count;
			var foldersCount = foldersWithMusic.Count;
			extensions = null!;
			uniqueFolders = null!;
			libraries = null!;
			foldersWithMusic = null!;

			await RefreshAutoScanResultMessage(foldersCount);
			ScanProgress = 100;
			TaskbarHelper.SetProgressValue(App.Hwnd, ScanProgress, 100);
			await Task.Delay(10);
			return ("Success", "Library scan completed.\nLibraries: " + librariesCount + "\nFolders: " + foldersCount + "\nSongs/Tracks: " + songsCount, failedFiles.ToList());
		}
		else
		{
			await DatabaseHelper.Instance.DeleteAllSongsFromDB();
			await DatabaseHelper.Instance.WipeFileScanMeta();
			await RefreshAutoScanResultMessage(0, "No libraries found");
			TaskbarHelper.SetProgressState(App.Hwnd, TaskbarStates.Error);
			return ("Warning", "No libraries found. Please add atleast one library.", new List<string>());
		}
	}

	/// <summary>
	/// Extracts song metadata from an audio file using TagLib, returning song details along with extra tag info.
	/// </summary>
	internal static async Task<(Song song, bool succeeded, int discNumber, string[]? composers, bool hasPictures)> ExtractSongMetadata(string filePath, double ignoreTrackDuration)
	{
		int discNumber = 0;
		string[]? composers = null;
		bool hasPictures = false;

		try
		{
			using var audioModel = TagLib.File.Create(filePath);
			var fileInfo = new FileInfo(filePath);

			if (audioModel.Tag != null)
			{
				discNumber = (int)audioModel.Tag.Disc;
				composers = audioModel.Tag.Composers;
				hasPictures = audioModel.Tag.Pictures?.Length > 0;
			}

			var song = new Song
			{
				Title = audioModel.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath),
				Album = audioModel.Tag.Album ?? "Unknown Album",
				Artists = audioModel.Tag.Performers?.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "Unknown Artist",
				Duration = audioModel.Properties.Duration.TotalSeconds,
				Path = filePath,
				Year = audioModel.Tag.Year <= 0 ? "Unknown Year" : audioModel.Tag.Year.ToString(),
				Genre = audioModel.Tag.Genres?.FirstOrDefault(g => !string.IsNullOrEmpty(g)) ?? "Unknown Genre",
				Track = (int)audioModel.Tag.Track,
				Cover = ImageResizer.CreateThumbnailImage(ThumbnailFolder.AllSongView, audioModel.Tag.Pictures, 300),
				Lyrics = audioModel.Tag.Lyrics,
				DateAdded = fileInfo.LastWriteTime,
				Extension = fileInfo.Extension,
				AudioCodecDescription = audioModel.Properties.Description,
				AudioSampleRate = audioModel.Properties.AudioSampleRate != 0 ? audioModel.Properties.AudioSampleRate.ToString() + " Hz" : null,
				AudioBitrate = audioModel.Properties.AudioBitrate != 0 ? audioModel.Properties.AudioBitrate.ToString() + " kbps" : null,
				AudioChannels = audioModel.Properties.AudioChannels switch
				{
					1 => "Mono",
					2 => "Stereo",
					4 => "Quadraphonic",
					5 => "Surround 5.0",
					6 => "Surround 5.1",
					7 => "Surround 6.1",
					8 => "Surround 7.1",
					>= 9 => "Immersive",
					_ => null
				},
				FileSize = fileInfo.Length switch
				{
					>= 1L << 40 => $"{fileInfo.Length / Math.Pow(1024, 4):0.##} TB",
					>= 1L << 30 => $"{fileInfo.Length / Math.Pow(1024, 3):0.##} GB",
					>= 1L << 20 => $"{fileInfo.Length / Math.Pow(1024, 2):0.##} MB",
					>= 1L << 10 => $"{fileInfo.Length / 1024d:0.##} KB",
					_ => $"{fileInfo.Length} B"
				}
			};

			song.PlayerType = DeterminePlayerType(song.AudioCodecDescription, filePath);

			if (song.Duration <= 0)
			{
				if (!FlyleafLib.Engine.IsLoaded)
				{
					var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FFmpeg");
					Engine.Start(new EngineConfig
					{
						UIRefresh = false,
						FFmpegPath = ffmpegPath,
					});
				}

				FlyleafLib.Config config = new FlyleafLib.Config();
				config.Video.Enabled = false;
				config.Audio.Enabled = true;
				config.Player.AutoPlay = false;
				var tempPlayer = new Player(config);
				tempPlayer.Open(filePath);
				song.Duration = TimeSpan.FromTicks(tempPlayer.Duration).TotalSeconds;
				tempPlayer.Stop();
				tempPlayer.Dispose();
				song.PlayerType = "Flyleaf";
			}

			return (song, true, discNumber, composers, hasPictures);
		}
		catch (Exception)
		{
			double duration = 0;
			try
			{
				FlyleafLib.Config config = new FlyleafLib.Config();
				config.Video.Enabled = false;
				config.Audio.Enabled = true;
				config.Player.AutoPlay = false;
				var tempPlayer = new Player(config);
				tempPlayer.Open(filePath);
				duration = TimeSpan.FromTicks(tempPlayer.Duration).TotalSeconds;
				tempPlayer.Dispose();
			}
			catch (Exception)
			{
				duration = 0;
			}
			var fileInfo = new FileInfo(filePath);
			var song = new Song
			{
				Title = Path.GetFileNameWithoutExtension(filePath),
				Album = "Unknown Album",
				Artists = "Unknown Artist",
				Duration = duration,
				Path = filePath,
				Year = "Unknown Year",
				Genre = "Unknown Genre",
				Cover = ImageResizer.CreateThumbnailImage(ThumbnailFolder.AllSongView, null, 300),
				DateAdded = fileInfo.LastWriteTime,
				Extension = fileInfo.Extension
			};
			return (song, false, 0, null, false);
		}
	}

	internal static FileScanMeta BuildFileScanMeta(string filePath)
	{
		var fileInfo = new FileInfo(filePath);
		return new FileScanMeta
		{
			Path = filePath,
			LastModifiedUtc = fileInfo.LastWriteTimeUtc.Ticks,
			CreationTimeUtc = fileInfo.CreationTimeUtc.Ticks,
			FileSizeBytes = fileInfo.Length,
			LastScannedUtc = DateTime.UtcNow.Ticks
		};
	}

	internal static List<string> ComputeEffectiveRoots(List<string> libraries)
	{
		libraries = libraries.OrderBy(f => f.Length).ToList();

		var uniqueFolders = new List<string>();
		foreach (var folder in libraries)
		{
			if (!Directory.Exists(folder))
			{
				GlobalNotification.Error("Library folder not found: " + folder + "\n Folder might be removed/renamed from system.");
			}
			else
			{
				if (!uniqueFolders.Any(parent => IsSameOrNestedPath(folder, parent)))
					uniqueFolders.Add(folder);
			}
		}
		return uniqueFolders;
	}

	private static bool IsSameOrNestedPath(string path, string parent)
	{
		var trimmedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		return string.Equals(path, trimmedParent, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith(trimmedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	internal static async Task<List<string>> GetEnabledExtensions()
	{
		var formatList = await DatabaseHelper.Instance.GetAllMusicFormats();

		List<string> extensions = new();
		foreach (var format in formatList)
			if (format.Enabled) extensions.Add(format.Extension);

		if (extensions.Count == 0) extensions.Add(".mp3");
		return extensions;
	}

	internal static int CountFoldersFromPaths(IEnumerable<string> trackedPaths)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in trackedPaths)
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				folders.Add(directory);
		}
		return folders.Count;
	}

	internal static async Task RefreshAutoScanResultMessage(int? folderCount = null, string? message = null)
	{
		var librariesCount = (await DatabaseHelper.Instance.GetAllLibraries()).Count;
		var songsCount = await DatabaseHelper.Instance.GetSongsCount();
		var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

		localSettings.Values[nameof(LocalSave.ScanResult_LibraryCount)] = librariesCount;
		localSettings.Values[nameof(LocalSave.ScanResult_SongsCount)] = songsCount;

		if (folderCount.HasValue)
			localSettings.Values[nameof(LocalSave.ScanResult_FolderCount)] = folderCount.Value;

		localSettings.Values[nameof(LocalSave.ScanResult_Time)] = new DateFormatConverter().Convert(DateTime.Now, null, "dddd, dd MMMM yyyy 'at' hh:mm:ss tt", null).ToString();

		if (string.IsNullOrEmpty(message))
			localSettings.Values.Remove(nameof(LocalSave.ScanResult_Message));
		else
			localSettings.Values[nameof(LocalSave.ScanResult_Message)] = message;
	}

	internal static string DeterminePlayerType(string? codecDescription, string filePath)
	{
		var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

		switch (ext)
		{
			case ".mp3":
			case ".mp2":
			case ".wma":
			case ".asf":
			case ".mid":
			case ".midi":
			case ".kar":
			case ".rmi":
				return "Windows";

			case ".ogg":
			case ".oga":
			case ".ogx":
			case ".opus":
			case ".ape":
			case ".wv":
			case ".tta":
			case ".mka":
			case ".webm":
			case ".ac3":
			case ".dts":
			case ".ra":
			case ".rm":
			case ".rmvb":
			case ".flac":
				return "Flyleaf";
		}

		if (!string.IsNullOrWhiteSpace(codecDescription))
		{
			var codec = codecDescription.ToLowerInvariant();

			if (codec.Contains("apple lossless") ||
				codec.Contains("alac") ||
				codec.Contains("opus") ||
				codec.Contains("vorbis") ||
				codec.Contains("wavpack") ||
				codec.Contains("monkey") ||
				codec.Contains("g.711") ||
				codec.Contains("g711") ||
				codec.Contains("g.726") ||
				codec.Contains("g726") ||
				codec.Contains("rf64") ||
				codec.Contains("dolby") ||
				codec.Contains("xhe") ||
				codec.Contains("eld") ||
				codec.Contains("usac"))
			{
				return "Flyleaf";
			}

			if (codec.Contains("mpeg audio") ||
				codec.Contains("aac") ||
				codec.Contains("mpeg-4 audio") ||
				codec.Contains("pcm") ||
				codec.Contains("windows media audio") ||
				codec.Contains("wma") ||
				codec.Contains("he-aac") ||
				codec.Contains("he aac"))
			{
				return "Windows";
			}
		}

		switch (ext)
		{
			case ".m4a":
			case ".m4b":
			case ".m4r":
			case ".mp4":
			case ".aac":
			case ".wav":
			case ".bwf":
				return "Windows";
			default:
				return "Flyleaf";
		}
	}
}
