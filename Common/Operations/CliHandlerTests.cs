using System.Text.Json;

namespace Tunetastic.Common.Operations;

/// <summary>
/// Unit tests and validation checks for CliHandler logic.
/// </summary>
public static class CliHandlerTests
{
	public static async Task RunTestsAsync(TextWriter? writer = null)
	{
		writer ??= Console.Out;
		writer.WriteLine("Running Tunetastic CLI Unit Tests...");

		TestIsCliCommand(writer);
		await TestHelpUsageOutputAsync(writer);
		TestJsonIpcSerialization(writer);
		TestFileResolver(writer);

		writer.WriteLine("All CLI Unit Tests Passed Successfully!");
	}

	private static void TestIsCliCommand(TextWriter writer)
	{
		Assert(CliHandler.IsCliCommand(new[] { "playlist", "add", "Test", "file.mp3" }));
		Assert(CliHandler.IsCliCommand(new[] { "--help" }));
		Assert(CliHandler.IsCliCommand(new[] { "-h" }));
		Assert(CliHandler.IsCliCommand(new[] { "help" }));
		Assert(CliHandler.IsCliCommand(new[] { "/?" }));
		Assert(CliHandler.IsCliCommand(new[] { "test" }));
		Assert(CliHandler.IsCliCommand(new[] { "--run-tests" }));

		Assert(!CliHandler.IsCliCommand(null));
		Assert(!CliHandler.IsCliCommand(Array.Empty<string>()));
		Assert(!CliHandler.IsCliCommand(new[] { "randomArg" }));

		writer.WriteLine("  [PASS] TestIsCliCommand");
	}

	private static async Task TestHelpUsageOutputAsync(TextWriter writer)
	{
		using var strWriter = new StringWriter();
		await CliHandler.ExecuteCliAsync(new[] { "--help" }, strWriter);
		string output = strWriter.ToString();
		Assert(output.Contains("Tunetastic CLI - Add local music to playlist"));
		Assert(output.Contains("Usage:"));

		using var errorWriter = new StringWriter();
		await CliHandler.ExecuteCliAsync(new[] { "playlist", "invalid" }, errorWriter);
		string errorOutput = errorWriter.ToString();
		Assert(errorOutput.Contains("Error: Invalid command arguments"));

		writer.WriteLine("  [PASS] TestHelpUsageOutputAsync");
	}

	private static void TestJsonIpcSerialization(TextWriter writer)
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

		writer.WriteLine("  [PASS] TestJsonIpcSerialization");
	}

	private static void TestFileResolver(TextWriter writer)
	{
		// Non-existent path test
		using var strWriter = new StringWriter();
		var result = CliHandler.ResolveFiles("non_existent_path_12345.mp3", null, strWriter);
		Assert(result.Count == 0);
		Assert(strWriter.ToString().Contains("Specified file or folder path does not exist"));

		// Existing non-audio file test (should be rejected)
		string tempTxtFile = Path.Combine(Path.GetTempPath(), "test_file.txt");
		File.WriteAllText(tempTxtFile, "text content");

		// Existing valid temp audio file test
		string tempMp3File = Path.Combine(Path.GetTempPath(), "test_tunetastic_sample.mp3");
		File.WriteAllText(tempMp3File, "dummy mp3");

		try
		{
			using var txtLogWriter = new StringWriter();
			var txtResult = CliHandler.ResolveFiles(tempTxtFile, null, txtLogWriter);
			Assert(txtResult.Count == 0, "Non-audio text file should be rejected by resolver");
			Assert(txtLogWriter.ToString().Contains("is not a supported audio format"));

			var mp3Result = CliHandler.ResolveFiles(tempMp3File, null, null);
			Assert(mp3Result.Count == 1, "Valid audio file should be accepted by resolver");
			Assert(Path.GetFullPath(tempMp3File).Equals(mp3Result[0], StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			if (File.Exists(tempTxtFile)) File.Delete(tempTxtFile);
			if (File.Exists(tempMp3File)) File.Delete(tempMp3File);
		}

		writer.WriteLine("  [PASS] TestFileResolver");
	}

	private static void Assert(bool condition, string? message = null)
	{
		if (!condition)
		{
			throw new InvalidOperationException($"CliHandlerTests Assertion Failed: {message ?? "Condition was false"}");
		}
	}
}
