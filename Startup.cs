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
	/// <remarks>
	/// Uses a mutex to prevent multiple instances from running simultaneously.
	/// If an existing instance is detected, it attempts to communicate with it via a named pipe
	/// by sending a PING or CLI message before terminating the current instance.
	/// If no existing instance is found, it proceeds to execute CLI logic or start a new application instance.
	/// </remarks>
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
			try
			{
				using var client = new NamedPipeClientStream(".", "Tunetastic.InstancePing", PipeDirection.InOut);
				client.Connect(1000);
				using var writer = new StreamWriter(client) { AutoFlush = true };
				using var reader = new StreamReader(client);

				if (CliHandler.IsCliCommand(args))
				{
					var jsonPayload = JsonSerializer.Serialize(args);
					writer.WriteLine("CLI:" + jsonPayload);
					string? line;
					while ((line = reader.ReadLine()) != null)
					{
						if (line == "END_CLI") break;
						Console.WriteLine(line);
					}
				}
				else
				{
					writer.WriteLine("PING");
				}
			}
			catch
			{
				//ignore
			}
			return;
		}

		if (CliHandler.IsCliCommand(args))
		{
			Task.Run(async () =>
			{
				await CliHandler.ExecuteCliAsync(args);
			}).GetAwaiter().GetResult();
			return;
		}

		Application.Start(p => new App());
	}
}
