using System.Text.Json;

namespace Tunetastic.Common.Operations;

/// <summary>
/// Unit tests and validation checks for CliHandler logic.
/// </summary>
public static class CliHandlerTests
{
	public static async Task RunTestsAsync()
	{
		TestIsCliCommand();
		await TestHelpUsageOutputAsync();
		TestJsonIpcSerialization();
		TestFileResolver();
	}

	private static void TestIsCliCommand()
	{
		Assert(CliHandler.IsCliCommand(new[] { "playlist", "add", "Test", "file.mp3" }));
		Assert(CliHandler.IsCliCommand(new[] { "--help" }));
		Assert(CliHandler.IsCliCommand(new[] { "-h" }));
		Assert(CliHandler.IsCliCommand(new[] { "help" }));
		Assert(CliHandler.IsCliCommand(new[] { "/?" }));

		Assert(!CliHandler.IsCliCommand(null));
		Assert(!CliHandler.IsCliCommand(Array.Empty<string>()));
		Assert(!CliHandler.IsCliCommand(new[] { "randomArg" }));
	}

	private static async Task TestHelpUsageOutputAsync()
	{
		using var writer = new StringWriter();
		await CliHandler.ExecuteCliAsync(new[] { "--help" }, writer);
		string output = writer.ToString();
		Assert(output.Contains("Tunetastic CLI - Add local music to playlist"));
		Assert(output.Contains("Usage:"));

		using var errorWriter = new StringWriter();
		await CliHandler.ExecuteCliAsync(new[] { "playlist", "invalid" }, errorWriter);
		string errorOutput = errorWriter.ToString();
		Assert(errorOutput.Contains("Error: Invalid command arguments"));
	}

	private static void TestJsonIpcSerialization()
	{
		string[] originalArgs = new[] { "playlist", "add", "My | Special \"Playlist\"", "C:\\Music\\song | 1.mp3" };
		string serialized = JsonSerializer.Serialize(originalArgs);
		string[]? deserialized = JsonSerializer.Deserialize<string[]>(serialized);

		Assert(deserialized != null);
		Assert(deserialized.Length == 4);
		Assert(deserialized[0] == "playlist");
		Assert(deserialized[1] == "add");
		Assert(deserialized[2] == "My | Special \"Playlist\"");
		Assert(deserialized[3] == "C:\\Music\\song | 1.mp3");
	}

	private static void TestFileResolver()
	{
		// Non-existent path test
		using var writer = new StringWriter();
		var result = CliHandler.ResolveFiles("non_existent_path_12345.mp3", null, writer);
		Assert(result.Count == 0);
		Assert(writer.ToString().Contains("Specified file or folder path does not exist"));

		// Existing temp file test
		string tempFile = Path.Combine(Path.GetTempPath(), "test_tunetastic_sample.mp3");
		File.WriteAllText(tempFile, "dummy");
		try
		{
			var fileResult = CliHandler.ResolveFiles(tempFile, null, null);
			Assert(fileResult.Count == 1);
			Assert(Path.GetFullPath(tempFile).Equals(fileResult[0], StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			if (File.Exists(tempFile)) File.Delete(tempFile);
		}
	}

	private static void Assert(bool condition, string? message = null)
	{
		if (!condition)
		{
			throw new InvalidOperationException($"CliHandlerTests Assertion Failed: {message ?? "Condition was false"}");
		}
	}
}
