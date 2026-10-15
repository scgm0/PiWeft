using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

[Collection("native-desktop")]
public sealed class NativeMessageInteractionTests(ITestOutputHelper output)
{
	[Fact]
	public async Task PointerFoldsKeepTheirPositionDiffSupportsDragSelectionBackdropClosesAndFileLinksLaunchEditor()
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1" || OperatingSystem.IsWindows()) return;
		var root = Path.Combine(Path.GetTempPath(), "piweft-interactions-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
		File.WriteAllText(Path.Combine(agent, "desktop.json"), "{\"theme\":\"dark\"}");
		var capture = Path.Combine(root, "editor-args.txt");
		var editor = Path.Combine(root, "code");
		File.WriteAllText(editor, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$PIWEFT_OPEN_CAPTURE\"\n");
		File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var file = Path.Combine(root, "sample file.cs");
		File.WriteAllText(file, "sample");
		var manager = SessionManager.Create(root, Path.Combine(agent, "sessions", "fixture"));
		for (var i = 0; i < 20; i++)
		{
			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
			manager.AppendMessage(new ToolResultMessage
			{
				ToolCallId = $"call-{i}", ToolName = "edit", Content = [new TextContent { Text = $"output {i}" }],
				Details = new JsonObject { ["patch"] = "@@ -1,300 +1,300 @@\n" + string.Join('\n', Enumerable.Range(0, 300).Select(line => $"-old value {line}\n+new value {line}")) },
			});
			manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = i == 19 ? "[sample](sample%20file.cs:12)" : $"answer {i}" }] });
		}
		var app = Environment.GetEnvironmentVariable("PIWEFT_INTERACTION_APP") ?? Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		var managed = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo(managed ? "dotnet" : app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		if (managed) info.ArgumentList.Add(app);
		foreach (var arg in new[] { "--cwd", root, "--ui-smoke", "interactions" }) info.ArgumentList.Add(arg);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		info.Environment["VISUAL"] = editor;
		info.Environment["PIWEFT_OPEN_CAPTURE"] = capture;
		var controls = new ConcurrentDictionary<string, string[]>();
		var lines = new ConcurrentQueue<string>();
		using var process = new Process { StartInfo = info };
		process.OutputDataReceived += (_, args) =>
		{
			if (args.Data is not { } line) return;
			lines.Enqueue(line);
			if (!line.StartsWith("interaction:", StringComparison.Ordinal)) return;
			var parts = line.Split(':');
			controls[parts[1]] = [.. parts[2].Split(','), parts[3]];
		};
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			var stderr = process.StandardError.ReadToEndAsync();
			await Wait(() => controls.ContainsKey("run"));
			await Fold("run", true);
			await Wait(() => controls.ContainsKey("group"));
			await Fold("group", true);
			await Wait(() => controls.ContainsKey("tool"));
			await Fold("tool", true);
			await Wait(() => controls.ContainsKey("diff-start"));
			await Run("xdotool", "mousemove", "--sync", controls["diff-start"][0], controls["diff-start"][1]);
			await Run("xdotool", "mousedown", "1");
			await Run("xdotool", "mousemove", "--sync", controls["diff-end"][0], controls["diff-end"][1]);
			await Run("xdotool", "mouseup", "1");
			await Wait(() => int.TryParse(controls["diff-start"][2], out var length) && length > 0);
			await Screenshot("diff-selected");
			await Fold("tool", false);
			await Fold("group", false);
			await Fold("run", false);
			var headerY = controls["run"][1];
			await Run("xdotool", "key", "space");
			await Wait(() => controls["run"][2] == "True");
			await Task.Delay(150);
			Assert.Equal(headerY, controls["run"][1]);
			await Run("xdotool", "key", "space");
			await Wait(() => controls["run"][2] == "False");
			await Task.Delay(150);
			Assert.Equal(headerY, controls["run"][1]);
			await Screenshot("fold-collapsed");
			await Click("link");
			await Wait(() => File.Exists(capture));
			Assert.Equal(new[] { "--goto", $"{file}:12:1" }, File.ReadAllLines(capture));
			await Run("xdotool", "key", "F8");
			await Wait(() => controls.GetValueOrDefault("preview")?[2] == "True");
			await Screenshot("image-preview");
			await Click("preview");
			await Wait(() => controls.GetValueOrDefault("preview")?[2] == "False");
			await Screenshot("image-dismissed");
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await process.WaitForExitAsync(timeout.Token);
			Assert.Equal(0, process.ExitCode);
			Assert.DoesNotContain("UI 线程异常", await stderr);
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
			output.WriteLine(string.Join('\n', lines));
			Directory.Delete(root, true);
		}

		async Task Fold(string name, bool expanded)
		{
			var before = controls[name];
			await Click(name);
			await Wait(() => controls[name][2] == expanded.ToString());
			await Task.Delay(150);
			Assert.InRange(Math.Abs(int.Parse(controls[name][1]) - int.Parse(before[1])), 0, 1);
		}
		async Task Click(string name)
		{
			var point = controls[name];
			await Run("xdotool", "mousemove", "--sync", point[0], point[1]);
			await Run("xdotool", "click", "1");
		}
		async Task Screenshot(string name)
		{
			if (Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is not { } directory || !File.Exists("/usr/bin/import")) return;
			Directory.CreateDirectory(directory);
			await Run("import", "-window", "root", Path.Combine(directory, $"{name}.png"));
		}
	}

	private static async Task Wait(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition()) await Task.Delay(20, timeout.Token);
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
