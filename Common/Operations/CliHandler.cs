using System.Runtime.InteropServices;

namespace Tunetastic.Common.Operations;

/// <summary>
/// Handles command-line interface (CLI) execution for Tunetastic operations,
/// such as adding local music files to playlists with automated metadata extraction.
/// </summary>
public static class CliHandler
{
	public const int EXIT_SUCCESS = 0;
	public const int EXIT_PARTIAL_FAILURE = 1;
	public const int EXIT_INVALID_ARGS = 2;
	public const int EXIT_IPC_ERROR = 3;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(int dwProcessId);

	private const int ATTACH_PARENT_PROCESS = -1;

	/// <summary>
	/// Attaches the process to the parent console window if invoked from a terminal,
	/// ensuring Console.Out and Console.Error redirect to the parent command line.
	/// </summary>
	public static void AttachParentConsole()
	{
		try
		{
			if (AttachConsole(ATTACH_PARENT_PROCESS))
			{
				Stream stdout = Console.OpenStandardOutput();
				Stream stderr = Console.OpenStandardError();
				Console.SetOut(new StreamWriter(stdout, Console.OutputEncoding) { AutoFlush = true });
				Console.SetError(new StreamWriter(stderr, Console.OutputEncoding) { AutoFlush = true });
			}
		}
		catch
		{
			// Ignore if not running in console context or if attach fails
		}
	}

	/// <summary>
	/// Checks whether the command line arguments indicate a CLI invocation.
	/// </summary>
	public static bool IsCliCommand(string[]? args)
	{
		if (args == null || args.Length == 0) return false;

		string first = args[0].Trim().ToLowerInvariant();
		return first == "playlist" || first == "--help" || first == "-h" || first == "/?" || first == "help" || first == "test" || first == "--run-tests";
	}

	/// <summary>
	/// Executes CLI logic based on provided command line arguments.
	/// </summary>
	/// <returns>Exit code integer (0 = success, 1 = partial failure, 2 = invalid arguments or path not found, 3 = IPC communication error).</returns>
	public static async Task<int> ExecuteCliAsync(string[] args, TextWriter? writer = null)
	{
		writer ??= Console.Out;

		if (args == null || args.Length == 0 || IsHelpRequest(args))
		{
			PrintUsage(writer);
			Environment.ExitCode = EXIT_SUCCESS;
			return EXIT_SUCCESS;
		}

		if (args.Length > 0 && (string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "--run-tests", StringComparison.OrdinalIgnoreCase)))
		{
			await CliHandlerTests.RunTestsAsync(writer);
			Environment.ExitCode = EXIT_SUCCESS;
			return EXIT_SUCCESS;
		}

		// Syntax: tunetastic playlist add "<playlist_name>" "<file_or_folder_or_glob>"
		if (args.Length >= 4 &&
			string.Equals(args[0], "playlist", StringComparison.OrdinalIgnoreCase) &&
			string.Equals(args[1], "add", StringComparison.OrdinalIgnoreCase))
		{
			string playlistName = args[2].Trim();
			string targetPath = args[3].Trim();

			int exitCode = await AddToPlaylistAsync(playlistName, targetPath, writer);
			Environment.ExitCode = exitCode;
			return exitCode;
		}

		writer.WriteLine($"Error: Invalid command arguments: '{string.Join(" ", args)}'.");
		writer.WriteLine();
		PrintUsage(writer);
		Environment.ExitCode = EXIT_INVALID_ARGS;
		return EXIT_INVALID_ARGS;
	}

	private static bool IsHelpRequest(string[] args)
	{
		return args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
							 a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
							 a.Equals("/?", StringComparison.OrdinalIgnoreCase) ||
							 a.Equals("help", StringComparison.OrdinalIgnoreCase));
	}

	private static void PrintUsage(TextWriter writer)
	{
		writer.WriteLine("Tunetastic CLI - Add local music to playlist");
		writer.WriteLine();
		writer.WriteLine("Usage:");
		writer.WriteLine("  tunetastic playlist add \"<playlist_name>\" \"<file_or_folder_or_glob>\"");
		writer.WriteLine("  tunetastic test");
		writer.WriteLine();
		writer.WriteLine("Examples:");
		writer.WriteLine("  tunetastic playlist add \"My Playlist\" \"C:\\Music\\song.mp3\"");
		writer.WriteLine("  tunetastic playlist add \"My Playlist\" \"C:\\Music\\*.mp3\"");
		writer.WriteLine("  tunetastic playlist add \"My Playlist\" \"C:\\Music\\**\\*.mp3\"");
		writer.WriteLine("  tunetastic playlist add \"My Playlist\" \"C:\\Music\\Album\"");
		writer.WriteLine();
		writer.WriteLine("Notes:");
		writer.WriteLine("  - Folder paths are scanned recursively for all supported audio formats.");
		writer.WriteLine("  - Single wildcard patterns (e.g. C:\\Music\\*.mp3) match files in that specific folder.");
		writer.WriteLine("  - Recursive wildcard patterns (e.g. C:\\Music\\**\\*.mp3) match files across subfolders.");
		writer.WriteLine("  - Direct file paths must be a supported audio format.");
		writer.WriteLine("  - Tracks already in the target playlist are skipped automatically.");
		writer.WriteLine();
		writer.WriteLine("Options:");
		writer.WriteLine("  --help, -h    Display this help message.");
		writer.WriteLine("  test          Execute internal CLI unit tests.");
	}

	public static readonly HashSet<string> DefaultAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".ogg", ".opus",
		".ape", ".wv", ".tta", ".mp2", ".m4b", ".m4r", ".mp4", ".bwf",
		".mid", ".midi", ".ac3", ".dts", ".mka", ".webm"
	};

	private static async Task<int> AddToPlaylistAsync(string playlistName, string targetPath, TextWriter writer)
	{
		var extensions = new HashSet<string>(DefaultAudioExtensions, StringComparer.OrdinalIgnoreCase);

		// Resolve files BEFORE initializing database
		List<string> files = ResolveFiles(targetPath, extensions, writer);

		if (files.Count == 0)
		{
			writer.WriteLine($"No audio files found matching path or pattern: '{targetPath}'.");
			return EXIT_INVALID_ARGS;
		}

		await DatabaseHelper.Instance.InitializeDatabase();

		try
		{
			var enabledExts = await LibraryScanner.GetEnabledExtensions();
			foreach (var ext in enabledExts)
			{
				if (!string.IsNullOrWhiteSpace(ext)) extensions.Add(ext);
			}
		}
		catch
		{
			// Fallback to default audio extensions
		}

		// Ensure target playlist exists or create it
		var existingPlaylists = await DatabaseHelper.Instance.GetAllPlaylistNames();
		string? targetPlaylist = existingPlaylists.FirstOrDefault(p => string.Equals(p, playlistName, StringComparison.OrdinalIgnoreCase));
		if (targetPlaylist == null)
		{
			await DatabaseHelper.Instance.CreatePlaylist(playlistName);
			targetPlaylist = playlistName;
			writer.WriteLine($"Playlist '{playlistName}' created.");
		}

		var existingSongsInPlaylist = await DatabaseHelper.Instance.GetSongsInPlaylist(targetPlaylist);
		var existingSongPaths = new HashSet<string>(existingSongsInPlaylist.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);

		writer.WriteLine($"Found {files.Count} file(s). Importing metadata and adding to playlist '{targetPlaylist}'...");
		writer.WriteLine();

		int addedCount = 0;
		int skippedCount = 0;
		int failedCount = 0;

		List<Song> songsToSave = new();
		List<string> songsToAddToPlaylist = new();

		foreach (var filePath in files)
		{
			string fileName = Path.GetFileName(filePath);
			writer.WriteLine($"File: {fileName}");

			try
			{
				var (song, succeeded, discNumber, composers, hasPictures) = await LibraryScanner.ExtractSongMetadata(filePath, 0);

				if (!succeeded)
				{
					writer.WriteLine("  [Failed] Could not parse metadata or audio tags from file.");
					failedCount++;
					writer.WriteLine();
					continue;
				}

				// Check metadata fields extracted
				writer.WriteLine($"  Title: {song.Title}");
				writer.WriteLine($"  Artist: {song.Artists}");
				writer.WriteLine($"  Album: {song.Album}");
				writer.WriteLine($"  Year: {song.Year}");
				writer.WriteLine($"  Genre: {song.Genre}");
				if (song.Track.HasValue && song.Track > 0)
				{
					writer.WriteLine($"  Track Number: {song.Track}");
				}

				if (discNumber > 0)
				{
					writer.WriteLine($"  [Limitation] Disc number ({discNumber}) present in file tag, but not supported by Tunetastic schema.");
				}
				if (composers != null && composers.Length > 0)
				{
					string composersStr = string.Join(", ", composers);
					writer.WriteLine($"  [Limitation] Composer tag ({composersStr}) present in file tag, but not supported by Tunetastic schema.");
				}

				// Check cover art
				bool hasCover = !string.IsNullOrEmpty(song.Cover) && File.Exists(song.Cover) && !song.Cover.EndsWith("AppIcon.png", StringComparison.OrdinalIgnoreCase);
				if (hasCover)
				{
					writer.WriteLine("  Cover Art: Embedded cover art successfully extracted.");
				}
				else if (hasPictures)
				{
					writer.WriteLine("  [Limitation] Embedded artwork present in file, but picture format could not be decoded.");
				}
				else
				{
					writer.WriteLine("  Cover Art: None embedded.");
				}

				if (existingSongPaths.Contains(filePath))
				{
					writer.WriteLine($"  Status: Skipped (already in playlist '{targetPlaylist}').");
					skippedCount++;
				}
				else
				{
					songsToSave.Add(song);
					songsToAddToPlaylist.Add(filePath);
					existingSongPaths.Add(filePath);
					writer.WriteLine("  Status: Added.");
					addedCount++;
				}
			}
			catch (Exception ex)
			{
				writer.WriteLine($"  [Failed] Error processing file '{fileName}': {ex.Message}");
				failedCount++;
			}

			writer.WriteLine();
		}

		if (songsToSave.Count > 0)
		{
			await DatabaseHelper.Instance.InsertMultipleSongs(songsToSave);
		}

		if (songsToAddToPlaylist.Count > 0)
		{
			await DatabaseHelper.Instance.AddSongsToPlaylist(targetPlaylist, songsToAddToPlaylist);
		}

		writer.WriteLine("========================================");
		writer.WriteLine($"Import Summary for Playlist '{targetPlaylist}':");
		writer.WriteLine($"  - Total files processed: {files.Count}");
		writer.WriteLine($"  - Successfully added: {addedCount}");
		writer.WriteLine($"  - Skipped (already in playlist): {skippedCount}");
		if (failedCount > 0)
		{
			writer.WriteLine($"  - Failed (unreadable/corrupt): {failedCount}");
		}
		writer.WriteLine("========================================");

		return failedCount > 0 ? EXIT_PARTIAL_FAILURE : EXIT_SUCCESS;
	}

	/// <summary>
	/// Resolves file paths from a target path, folder, or wildcard pattern.
	/// </summary>
	public static List<string> ResolveFiles(string targetPath, HashSet<string>? audioExtensions = null, TextWriter? writer = null)
	{
		audioExtensions ??= DefaultAudioExtensions;
		List<string> result = new();

		// Case 1: Direct file
		if (File.Exists(targetPath))
		{
			string ext = Path.GetExtension(targetPath);
			if (audioExtensions.Contains(ext))
			{
				result.Add(Path.GetFullPath(targetPath));
			}
			else
			{
				writer?.WriteLine($"File '{targetPath}' is not a supported audio format ({ext}).");
			}
			return result;
		}

		// Case 2: Direct directory
		if (Directory.Exists(targetPath))
		{
			var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
			var files = Directory.EnumerateFiles(targetPath, "*.*", options)
								 .Where(f => audioExtensions.Contains(Path.GetExtension(f)));
			result.AddRange(files.Select(Path.GetFullPath));
			return result;
		}

		// Case 3: Wildcards (* or ?)
		if (targetPath.Contains('*') || targetPath.Contains('?'))
		{
			try
			{
				int wildcardIdx = targetPath.IndexOfAny(new[] { '*', '?' });
				string pathBeforeWildcard = targetPath.Substring(0, wildcardIdx);
				string? dir = Path.GetDirectoryName(pathBeforeWildcard);

				if (string.IsNullOrEmpty(dir))
				{
					dir = Directory.GetCurrentDirectory();
				}

				string pattern = Path.GetFileName(targetPath);
				if (pattern.Contains("**"))
				{
					pattern = pattern.Replace("**", "*");
				}

				bool isRecursive = targetPath.Contains("**");
				var options = new EnumerationOptions { RecurseSubdirectories = isRecursive, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };

				if (Directory.Exists(dir))
				{
					var files = Directory.EnumerateFiles(dir, pattern, options)
										 .Where(f => audioExtensions.Contains(Path.GetExtension(f)));
					result.AddRange(files.Select(Path.GetFullPath));
					return result;
				}
				else
				{
					writer?.WriteLine($"Directory '{dir}' for wildcard search does not exist.");
				}
			}
			catch (Exception ex)
			{
				writer?.WriteLine($"Error resolving wildcard path '{targetPath}': {ex.Message}");
			}
		}
		else
		{
			writer?.WriteLine($"Specified file or folder path does not exist: '{targetPath}'.");
		}

		return result;
	}
}
