using System.IO.Pipes;
using System.Text.Json;
using Tunetastic.Common.Operations;

namespace Tunetastic;

/// <summary>
/// Provides the entry point configuration for the application startup process.
/// </summary>
public static class Startup
{
	/// <summary>
	/// The main entry point for the Tunetastic application that enforces single instance behavior.
	/// </summary>
	/// <param name="args">Command-line arguments passed to the application.</param>
	[STAThread]
	static void Main(string[] args)
	{
		if (CliHandler.IsCliCommand(args))
		{
			CliHandler.AttachParentConsole();
		}

		bool createdNew;
		using var mutex = new Mutex(true, "Tunetastic.Mutex", out createdNew);

		if (!createdNew)
		{
			if (CliHandler.IsCliCommand(args))
			{
				try
				{
					using var client = new NamedPipeClientStream(".", "Tunetastic.InstancePing", PipeDirection.InOut);
					client.Connect(1500);
					using var writer = new StreamWriter(client) { AutoFlush = true };
					using var reader = new StreamReader(client);

					var jsonPayload = JsonSerializer.Serialize(args);
					writer.WriteLine("CLI:" + jsonPayload);
					string? line;
					while ((line = reader.ReadLine()) != null)
					{
						if (line == "END_CLI") break;
						Console.WriteLine(line);
					}
					Environment.ExitCode = CliHandler.EXIT_SUCCESS;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"Error: Failed to communicate with running Tunetastic instance: {ex.Message}");
					Environment.ExitCode = CliHandler.EXIT_IPC_ERROR;
				}
			}
			else
			{
				try
				{
					using var client = new NamedPipeClientStream(".", "Tunetastic.InstancePing", PipeDirection.InOut);
					client.Connect(500);
					using var writer = new StreamWriter(client) { AutoFlush = true };
					writer.WriteLine("PING");
				}
				catch
				{
					// ignore
				}
			}
			return;
		}

		if (CliHandler.IsCliCommand(args))
		{
			int exitCode = Task.Run(async () => await CliHandler.ExecuteCliAsync(args)).GetAwaiter().GetResult();
			Environment.ExitCode = exitCode;
			return;
		}

		Application.Start(p => new App());
	}
}
