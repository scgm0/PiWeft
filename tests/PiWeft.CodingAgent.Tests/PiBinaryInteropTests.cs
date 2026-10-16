using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// End-to-end interoperability with the real <c>pi</c> binary: a session written by this port must be readable by the
/// TypeScript implementation. Skipped when <c>pi</c> is not installed.
/// </summary>
public class PiBinaryInteropTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-interop-" + Guid.NewGuid().ToString("N")[..8]);

	public PiBinaryInteropTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	internal static string? FindPi()
	{
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}

		var names = OperatingSystem.IsWindows() ? new[] { "pi.cmd", "pi.exe", "pi" } : ["pi"];
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			foreach (var name in names)
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		return null;
	}

	private async Task<string> WriteSessionAsync()
	{
		var sessionsDir = Path.Combine(_root, "sessions");
		var script = new ScriptedStreamFn();
		script.EnqueueText("hello from the csharp port");

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, sessionsDir),
			Skills = [],
		});

		await session.PromptAsync("hi from csharp");
		return Directory.EnumerateFiles(sessionsDir, "*.jsonl").Single();
	}

	/// <summary>Decodes the base64 session payload pi embeds in its HTML export.</summary>
	private static JsonObject SessionDataPayload(string html)
	{
		const string open = "<script id=\"session-data\" type=\"application/json\">";
		var start = html.IndexOf(open, StringComparison.Ordinal);
		Assert.True(start >= 0, "export has no session-data payload");
		start += open.Length;

		var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
		Assert.True(end > start, "export payload is unterminated");

		var base64 = html[start..end].Trim();
		var decoded = Convert.FromBase64String(base64);
		return JsonNode.Parse(decoded)!.AsObject();
	}

	private (int ExitCode, string Output) Run(string executable, string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo(executable)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		// Keep the real agent directory (settings, sessions) out of the picture.
		startInfo.Environment["PI_CODING_AGENT_DIR"] = Path.Combine(_root, "agent");

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var output = new StringBuilder();
		output.Append(process.StandardOutput.ReadToEnd());
		output.Append(process.StandardError.ReadToEnd());
		process.WaitForExit();
		return (process.ExitCode, output.ToString());
	}

	[Fact]
	public async Task RealPiReadsASessionWrittenByThisPort()
	{
		var pi = FindPi();
		if (pi is null)
		{
			return; // no pi installation on this machine
		}

		var sessionFile = await WriteSessionAsync();

		// `pi --export` parses the session and renders it, which exercises the reader without a model or network.
		// It writes pi-session-<name>.html into the process working directory.
		var exportDir = Path.Combine(_root, "export");
		Directory.CreateDirectory(exportDir);
		var (exitCode, output) = Run(
			pi,
			exportDir,
			"--offline", "--no-extensions", "--no-skills", "--no-prompt-templates", "--export", sessionFile);

		Assert.True(
			exitCode == 0,
			$"pi --export failed with exit code {exitCode}:\n{output}\n\nsession file:\n{File.ReadAllText(sessionFile)}");

		var exported = Directory.EnumerateFiles(exportDir, "*.html").FirstOrDefault();
		Assert.True(exported is not null, $"pi wrote no HTML:\n{output}");

		// The export embeds the parsed session as base64 JSON, so decoding it proves pi understood every entry.
		var payload = SessionDataPayload(File.ReadAllText(exported!));
		Assert.Equal(3, payload["header"]!["version"]!.GetValue<int>());
		Assert.Equal(_root, payload["header"]!["cwd"]!.GetValue<string>());

		var entries = payload["entries"]!.AsArray();

		// 文件里是「用户消息 + 助手回复」：系统提示词**不写进文件**（与 pi 相同，
		// 写进去的话真实 pi 会把它当成一条历史消息），级别没变化时也不写多余条目。
		Assert.Equal(2, entries.Count);
		Assert.Equal("hi from csharp", entries[0]!["message"]!["content"]!.GetValue<string>());
		Assert.Equal(
			"hello from the csharp port",
			entries[1]!["message"]!["content"]!.AsArray()
				.Single(static b => b!["type"]!.GetValue<string>() == "text")!["text"]!.GetValue<string>());

		Assert.DoesNotContain(
			entries,
			static entry => entry!["message"]?["role"]?.GetValue<string>() == "system");
	}
}
