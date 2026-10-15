using System.Diagnostics;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Runs the shell's headless self-check as a child process. It is the only end-to-end gate that can run without a
/// display, and it covers exactly the wiring a window needs: model resolution, the provider gateway, tool discovery
/// and the session catalog.
/// </summary>
public class SelfCheckTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktop-check-" + Guid.NewGuid().ToString("N")[..8]);

	public SelfCheckTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task SelfCheckReportsAModelToolsAndSessions()
	{
		var shell = Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		Assert.True(File.Exists(shell), $"the shell is not built next to the tests: {shell}");

		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(shell);
		info.ArgumentList.Add("--self-check");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(_root);
		info.Environment["PI_CODING_AGENT_DIR"] = Path.Combine(_root, "agent");
		info.Environment["PI_LOG_LEVEL"] = "Information";

		using var process = Process.Start(info)!;
		var stdout = await process.StandardOutput.ReadToEndAsync();
		var stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		Assert.True(process.ExitCode == 0, $"self-check exited {process.ExitCode}\n{stdout}\n{stderr}");
		Assert.Contains("PiWeft Desktop 自检", stdout);
		Assert.Contains(Strings.SelfCheckModel, stdout);
		Assert.Contains($"{Strings.SelfCheckTools}: read, bash, edit, write, grep, find, ls", stdout);
		Assert.Contains(Strings.SelfCheckModels, stdout);
		Assert.Contains(Strings.SelfCheckOk, stdout);
		Assert.Contains("Logging started: application=desktop", stderr);
		Assert.Contains("Desktop stopped: exitCode=0", stderr);
		Assert.DoesNotContain("Logging started", stdout);
		var logs = Directory.GetFiles(Path.Combine(_root, "agent", "logs"), "piweft-desktop-*.log");
		Assert.Contains("Desktop stopped: exitCode=0", string.Concat(logs.Select(File.ReadAllText)));
	}

	[Theory]
	[InlineData("Warning", true)]
	[InlineData("None", false)]
	public async Task TerminalLogsRespectTheConfiguredLevel(string level, bool expectWarning)
	{
		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll"));
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(Path.Combine(_root, "missing"));
		info.Environment["PI_CODING_AGENT_DIR"] = Path.Combine(_root, "agent");
		info.Environment["PI_LOG_LEVEL"] = level;

		using var process = Process.Start(info)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		var stderr = await stderrTask;
		Assert.Equal(2, process.ExitCode);
		Assert.Empty(await stdoutTask);
		Assert.DoesNotContain("Logging started", stderr);
		Assert.DoesNotContain("Desktop stopped", stderr);
		Assert.Equal(expectWarning, stderr.Contains("Working directory does not exist", StringComparison.Ordinal));
	}

	[Fact]
	public async Task TerminalLogsRemainAvailableWhenTheLogDirectoryCannotBeCreated()
	{
		var agent = Path.Combine(_root, "agent");
		Directory.CreateDirectory(agent);
		File.WriteAllText(Path.Combine(agent, "logs"), "blocks directory creation");
		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll"));
		info.ArgumentList.Add("--self-check");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(_root);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		info.Environment["PI_LOG_LEVEL"] = "Information";

		using var process = Process.Start(info)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);
		Assert.Contains(Strings.SelfCheckOk, await stdoutTask);
		var stderr = await stderrTask;
		Assert.Contains("Logging started: application=desktop", stderr);
		Assert.Contains("Desktop stopped: exitCode=0", stderr);
	}

	[Fact]
	public async Task SelfCheckFailsOnAMissingDirectory()
	{
		var shell = Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		Assert.True(File.Exists(shell), $"the shell is not built next to the tests: {shell}");

		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(shell);
		info.ArgumentList.Add("--self-check");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(Path.Combine(_root, "missing"));
		info.Environment["PI_CODING_AGENT_DIR"] = Path.Combine(_root, "agent");
		info.Environment["PI_LOG_LEVEL"] = "Information";

		using var process = Process.Start(info)!;
		var stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		Assert.Equal(2, process.ExitCode);
		Assert.Contains("Working directory does not exist", stderr);
		Assert.Contains("Desktop stopped: exitCode=2", stderr);
		Assert.Contains(Strings.NoSuchDirectory(string.Empty).TrimEnd('：', ':'), stderr);
	}
}
