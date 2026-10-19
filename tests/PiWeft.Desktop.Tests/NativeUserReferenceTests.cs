using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>Opt-in visual check for file references in the composer strip and user message chips.</summary>
[Collection("native-desktop")]
public sealed class NativeUserReferenceTests(ITestOutputHelper output)
{
	[Fact]
	public async Task UserReferencesRenderInTheComposerAndHistory()
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		var root = Path.Combine(Path.GetTempPath(), "piweft-references-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
		var theme = Environment.GetEnvironmentVariable("PIWEFT_WIDGET_THEME") ?? "dark";
		File.WriteAllText(Path.Combine(agent, "desktop.json"), $"{{\"theme\":\"{theme}\"}}");
		File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"defaultProvider\":\"fixture\",\"defaultModel\":\"fixture-model\"}");
		File.WriteAllText(Path.Combine(agent, "models.json"), new JsonObject
		{
			["providers"] = new JsonObject
			{
				["fixture"] = new JsonObject
				{
					["baseUrl"] = "http://127.0.0.1:9", ["api"] = "openai-completions", ["apiKey"] = "fixture-token",
					["models"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["name"] = "Fixture", ["contextWindow"] = 128000, ["maxTokens"] = 4096 }),
				},
			},
		}.ToJsonString());

		var session = SessionManager.Create(root, Path.Combine(agent, "sessions", "fixture"));
		session.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromText(
				"<skill name=\"mewui\" location=\"/home/scgm/.pi/agent/skills/mewui/SKILL.md\">\nReferences are relative to the skill directory.\n</skill>\n\n" +
				"请检查 @\"src/App.cs\" 和 @\"/home/scgm/My Docs/design spec.pdf\"，再看 @docs/。"),
			Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
		});
		session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "好的。" }] });

		var app = Environment.GetEnvironmentVariable("PIWEFT_INTERACTION_APP") ?? Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		var managed = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo(managed ? "dotnet" : app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		if (managed) info.ArgumentList.Add(app);
		foreach (var argument in new[] { "--cwd", root, "--ui-smoke", "references" }) info.ArgumentList.Add(argument);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		using var process = new Process { StartInfo = info };
		var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var ready = false;
		process.OutputDataReceived += (_, args) =>
		{
			if (args.Data is { } line) lines.Enqueue(line);
			if (args.Data == "reference-ready") Volatile.Write(ref ready, true);
		};
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			while (!Volatile.Read(ref ready)) await Task.Delay(50, timeout.Token);
			Assert.False(process.HasExited, "The desktop exited before the reference chips were rendered.");
			await Task.Delay(400);
			if (Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is { } directory && File.Exists("/usr/bin/import"))
			{
				Directory.CreateDirectory(directory);
				var screenshot = Path.Combine(directory, "user-references.png");
				await Run("import", "-window", "root", screenshot);
				Assert.True(new FileInfo(screenshot).Length > 5_000);
			}
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
			output.WriteLine(await process.StandardError.ReadToEndAsync());
			output.WriteLine(string.Join('\n', lines));
			Directory.Delete(root, true);
		}
	}

	private static async Task Run(string executable, params string[] arguments)
	{
		var info = new ProcessStartInfo(executable) { UseShellExecute = false };
		foreach (var argument in arguments) info.ArgumentList.Add(argument);
		using var process = Process.Start(info)!;
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);
	}
}
