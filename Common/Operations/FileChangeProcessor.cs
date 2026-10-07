namespace Tunetastic.Common.Operations;

/// <summary>
/// Applies individual file-system changes in the music libraries (created, modified, deleted and renamed files)
/// to the song database and to the scan metadata used by incremental scans.
/// </summary>
public static class FileChangeProcessor
{
	public static async Task ProcessFileChange(string path, FileChangeType changeType, string? newPath = null)
	{
		var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
		var ignoreTrackDuration = double.Parse(localSettings.Values[nameof(LocalSave.IgnoreTracksBelowDuration)]?.ToString() ?? "0");
		var ignoreDuplicates = bool.Parse(localSettings.Values[nameof(LocalSave.IgnoreDuplicateEnabled)]?.ToString() ?? "false");

		switch (changeType)
		{
			case FileChangeType.Created:
				await HandleCreated(path, ignoreTrackDuration, ignoreDuplicates);
				break;

			case FileChangeType.Modified:
				await HandleModified(path, ignoreTrackDuration, ignoreDuplicates);
				break;

			case FileChangeType.Deleted:
				await DatabaseHelper.Instance.DeleteSongFromDB(path);
				await DatabaseHelper.Instance.DeleteFileScanMeta(new List<string> { path });
				break;

			case FileChangeType.Renamed:
				if (string.IsNullOrWhiteSpace(newPath))
					return;

				var existingMeta = await DatabaseHelper.Instance.GetFileScanMeta(path);
				if (existingMeta == null)
				{
					await HandleCreated(newPath, ignoreTrackDuration, ignoreDuplicates);
					return;
				}

				await DatabaseHelper.Instance.RenameSongPath(path, newPath);

				var updatedMeta = LibraryScanner.BuildFileScanMeta(newPath);
				await DatabaseHelper.Instance.UpdateFileScanMeta(new List<FileScanMeta> { updatedMeta });
				break;
		}
	}

	private static async Task HandleCreated(string path, double ignoreTrackDuration, bool ignoreDuplicates)
	{
		var (song, succeeded, _, _, _) = await LibraryScanner.ExtractSongMetadata(path, ignoreTrackDuration);
		if (!succeeded) return;
		if (song.Duration <= ignoreTrackDuration) return;

		if (ignoreDuplicates && await DatabaseHelper.Instance.SongMetadataExists(song.Title, song.Artists, song.Album))
			return;

		await DatabaseHelper.Instance.InsertMultipleSongs(new List<Song> { song });
		await DatabaseHelper.Instance.UpdateFileScanMeta(new List<FileScanMeta> { LibraryScanner.BuildFileScanMeta(path) });
	}

	private static async Task HandleModified(string path, double ignoreTrackDuration, bool ignoreDuplicates)
	{
		var (song, succeeded, _, _, _) = await LibraryScanner.ExtractSongMetadata(path, ignoreTrackDuration);
		if (!succeeded) return;

		if (song.Duration <= ignoreTrackDuration)
		{
			await DatabaseHelper.Instance.DeleteSongFromDB(path);
			await DatabaseHelper.Instance.DeleteFileScanMeta(new List<string> { path });
			return;
		}

		if (ignoreDuplicates && await DatabaseHelper.Instance.SongMetadataExists(song.Title, song.Artists, song.Album, excludePath: path))
			return;

		var existingSong = await DatabaseHelper.Instance.GetSongByPath(path);
		song.PlayCount = existingSong?.PlayCount ?? 0;
		song.DateLastPlayed = existingSong?.DateLastPlayed;

		await DatabaseHelper.Instance.InsertMultipleSongs(new List<Song> { song });
		await DatabaseHelper.Instance.UpdateFileScanMeta(new List<FileScanMeta> { LibraryScanner.BuildFileScanMeta(path) });
	}
}
