using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>假命令执行器：记录调用，并可按用例模拟命令的副作用。</summary>
internal sealed class FakePackageRunner : IPackageCommandRunner
{
	public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

	public int ExitCode { get; set; }

	public Action<string, IReadOnlyList<string>, string>? OnRun { get; set; }

	public Task<(int ExitCode, string Output, string Error)> RunAsync(
		string fileName,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		CancellationToken cancellationToken)
	{
		Calls.Add((fileName, arguments, workingDirectory));
		OnRun?.Invoke(fileName, arguments, workingDirectory);
		return Task.FromResult((ExitCode, string.Empty, ExitCode == 0 ? string.Empty : "boom"));
	}
}

/// <summary>pi 包的安装、移除与更新（npm / git / 本地路径）。</summary>
public class PackageManagerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pkgmgr-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _agentDir;

	private readonly string _cwd;

	public PackageManagerTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_cwd = Path.Combine(_root, "repo");
		Directory.CreateDirectory(_agentDir);
		Directory.CreateDirectory(_cwd);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private PackageOptions Options(bool local = false) => new()
	{
		AgentDir = _agentDir,
		Cwd = _cwd,
		Local = local,
	};

	[Fact]
	public async Task InstallNpm_RunsNpmAndRecordsSource()
	{
		var runner = new FakePackageRunner();
		// 模拟 npm 把包装到 <prefix>/node_modules/<name>。
		runner.OnRun = (_, arguments, _) =>
		{
			var prefix = arguments[arguments.ToList().IndexOf("--prefix") + 1];
			Directory.CreateDirectory(Path.Combine(prefix, "node_modules", "@scope", "demo"));
		};

		var result = await PiPackageManager.InstallAsync("npm:@scope/demo@1.2.3", Options(), runner);

		Assert.True(result.Ok, result.Message);
		var call = Assert.Single(runner.Calls);
		Assert.Equal("npm", call.FileName);
		Assert.Contains("install", call.Arguments);
		Assert.Contains("@scope/demo@1.2.3", call.Arguments);
		Assert.Contains("--legacy-peer-deps", call.Arguments);

		var settings = JsonNode.Parse(File.ReadAllText(CodingAgentSettings.GlobalPath(_agentDir)))!;
		Assert.Equal("npm:@scope/demo@1.2.3", settings["packages"]![0]!.GetValue<string>());
	}

	[Fact]
	public async Task InstallNpm_Failure_DoesNotRecordSource()
	{
		var runner = new FakePackageRunner { ExitCode = 1 };
		var result = await PiPackageManager.InstallAsync("npm:broken", Options(), runner);

		Assert.False(result.Ok);
		Assert.False(File.Exists(CodingAgentSettings.GlobalPath(_agentDir)));
	}

	[Fact]
	public async Task InstallGit_ClonesWithBranchAndRecordsSource()
	{
		var runner = new FakePackageRunner();
		runner.OnRun = (_, arguments, _) =>
		{
			// clone 的最后一个参数是目标目录。
			Directory.CreateDirectory(arguments[^1]);
		};

		var result = await PiPackageManager.InstallAsync("git:github.com/org/repo@v1", Options(), runner);

		Assert.True(result.Ok, result.Message);
		var call = Assert.Single(runner.Calls);
		Assert.Equal("git", call.FileName);
		Assert.Equal("clone", call.Arguments[0]);
		Assert.Contains("--depth", call.Arguments);
		Assert.Contains("--branch", call.Arguments);
		Assert.Contains("v1", call.Arguments);
		Assert.Contains("https://github.com/org/repo", call.Arguments);
	}

	[Fact]
	public async Task InstallGit_Failure_CleansUpTargetDirectory()
	{
		var runner = new FakePackageRunner { ExitCode = 128 };
		runner.OnRun = (_, arguments, _) => Directory.CreateDirectory(arguments[^1]);

		var result = await PiPackageManager.InstallAsync("git:github.com/org/repo", Options(), runner);

		Assert.False(result.Ok);
		Assert.False(Directory.Exists(Path.Combine(_agentDir, "git", "github.com-org-repo")));
	}

	[Fact]
	public async Task InstallLocal_RegistersExistingPackage()
	{
		var packageRoot = Path.Combine(_root, "local-pkg");
		Directory.CreateDirectory(packageRoot);
		File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{ "name": "local-pkg" }""");

		var runner = new FakePackageRunner();
		var result = await PiPackageManager.InstallAsync(packageRoot, Options(), runner);

		Assert.True(result.Ok, result.Message);
		Assert.Empty(runner.Calls);
		Assert.Equal(packageRoot, result.Detail);
	}

	[Fact]
	public async Task InstallLocal_WithoutManifest_Fails()
	{
		var packageRoot = Path.Combine(_root, "not-a-package");
		Directory.CreateDirectory(packageRoot);

		var result = await PiPackageManager.InstallAsync(packageRoot, Options(), new FakePackageRunner());

		Assert.False(result.Ok);
		Assert.Contains("package.json", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Remove_DeletesRemoteDirectoryAndSettingsEntry()
	{
		var runner = new FakePackageRunner();
		runner.OnRun = (_, arguments, _) => Directory.CreateDirectory(arguments[^1]);
		await PiPackageManager.InstallAsync("git:github.com/org/repo", Options(), runner);

		var target = Path.Combine(_agentDir, "git", "github.com-org-repo");
		Assert.True(Directory.Exists(target));

		var result = await PiPackageManager.RemoveAsync("git:github.com/org/repo", Options());

		Assert.True(result.Ok, result.Message);
		Assert.False(Directory.Exists(target));
		var settings = JsonNode.Parse(File.ReadAllText(CodingAgentSettings.GlobalPath(_agentDir)))!;
		Assert.True(settings["packages"] is null || settings["packages"]!.AsArray().Count == 0);
	}

	[Fact]
	public async Task Remove_LocalPackage_KeepsUserDirectory()
	{
		var packageRoot = Path.Combine(_root, "local-pkg");
		Directory.CreateDirectory(packageRoot);
		File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{ "name": "local-pkg" }""");
		await PiPackageManager.InstallAsync(packageRoot, Options(), new FakePackageRunner());

		var result = await PiPackageManager.RemoveAsync(packageRoot, Options());

		Assert.True(result.Ok, result.Message);
		Assert.True(Directory.Exists(packageRoot));
	}

	[Fact]
	public async Task Remove_UnknownSource_Fails()
	{
		var result = await PiPackageManager.RemoveAsync("npm:never-installed", Options());
		Assert.False(result.Ok);
	}

	[Fact]
	public async Task Update_Npm_ReinstallsDeclaredSpec()
	{
		var runner = new FakePackageRunner();
		runner.OnRun = (_, arguments, _) =>
		{
			var prefix = arguments[arguments.ToList().IndexOf("--prefix") + 1];
			Directory.CreateDirectory(Path.Combine(prefix, "node_modules", "demo"));
		};

		await PiPackageManager.InstallAsync("npm:demo", Options(), runner);
		runner.Calls.Clear();

		var results = await PiPackageManager.UpdateAsync("npm:demo", Options(), runner);

		Assert.Single(results);
		Assert.True(results[0].Ok, results[0].Message);
		Assert.Contains("install", Assert.Single(runner.Calls).Arguments);
	}

	[Fact]
	public async Task Update_WithoutSource_UpdatesDeclaredPackages()
	{
		var runner = new FakePackageRunner();
		runner.OnRun = (_, arguments, _) =>
		{
			var prefix = arguments[arguments.ToList().IndexOf("--prefix") + 1];
			Directory.CreateDirectory(Path.Combine(prefix, "node_modules", "demo"));
		};

		await PiPackageManager.InstallAsync("npm:demo", Options(), runner);
		runner.Calls.Clear();

		var results = await PiPackageManager.UpdateAsync(null, Options(), runner);

		Assert.Single(results);
		Assert.Single(runner.Calls);
	}

	[Fact]
	public async Task Install_LocalScope_WritesProjectSettings()
	{
		var packageRoot = Path.Combine(_root, "local-pkg");
		Directory.CreateDirectory(packageRoot);
		File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{ "name": "local-pkg" }""");

		var result = await PiPackageManager.InstallAsync(packageRoot, Options(local: true), new FakePackageRunner());

		Assert.True(result.Ok, result.Message);
		Assert.True(File.Exists(CodingAgentSettings.ProjectPath(_cwd)));
		Assert.False(File.Exists(CodingAgentSettings.GlobalPath(_agentDir)));
	}
}

/// <summary>settings 的 <c>packages</c> 读写：加、删、幂等与其它键保留。</summary>
public class PackageSettingsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pkgsettings-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _settingsPath;

	public PackageSettingsTests()
	{
		Directory.CreateDirectory(_root);
		_settingsPath = Path.Combine(_root, "settings.json");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void AddPackage_AppendsAndPreservesUnknownKeys()
	{
		File.WriteAllText(_settingsPath, """{ "defaultModel": "x", "packages": ["npm:a"] }""");

		Assert.True(CodingAgentSettings.AddPackage(_settingsPath, "npm:b"));

		var root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
		Assert.Equal("x", root["defaultModel"]!.GetValue<string>());
		Assert.Equal(new[] { "npm:a", "npm:b" }, root["packages"]!.AsArray().Select(node => node!.GetValue<string>()));
	}

	[Fact]
	public void AddPackage_IsIdempotent()
	{
		Assert.True(CodingAgentSettings.AddPackage(_settingsPath, "npm:a"));
		Assert.False(CodingAgentSettings.AddPackage(_settingsPath, "npm:a"));

		var root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
		Assert.Single(root["packages"]!.AsArray());
	}

	[Fact]
	public void RemovePackage_RemovesOnlyMatchingEntry()
	{
		File.WriteAllText(_settingsPath, """{ "packages": ["npm:a", "npm:b"] }""");

		Assert.True(CodingAgentSettings.RemovePackage(_settingsPath, "npm:a"));

		var root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
		Assert.Equal(new[] { "npm:b" }, root["packages"]!.AsArray().Select(node => node!.GetValue<string>()));
	}

	[Fact]
	public void RemovePackage_LastEntry_DropsTheArray()
	{
		File.WriteAllText(_settingsPath, """{ "packages": ["npm:a"], "defaultModel": "x" }""");

		Assert.True(CodingAgentSettings.RemovePackage(_settingsPath, "npm:a"));

		var root = JsonNode.Parse(File.ReadAllText(_settingsPath))!;
		Assert.Null(root["packages"]);
		Assert.Equal("x", root["defaultModel"]!.GetValue<string>());
	}

	[Fact]
	public void RemovePackage_MissingEntry_ReturnsFalse()
	{
		File.WriteAllText(_settingsPath, """{ "packages": ["npm:a"] }""");
		Assert.False(CodingAgentSettings.RemovePackage(_settingsPath, "npm:zzz"));
	}

	[Theory]
	[InlineData("""{ "npmCommand": ["pnpm", "--silent"] }""", "pnpm", "--silent")]
	[InlineData("""{ "npmCommand": [] }""", "npm", null)]
	[InlineData("""{ "defaultModel": "x" }""", "npm", null)]
	[InlineData("not json", "npm", null)]
	public void NpmCommand_ReadsOverrideWithFallback(string json, string expectedFirst, string? expectedSecond)
	{
		File.WriteAllText(_settingsPath, json);

		var command = CodingAgentSettings.NpmCommand(_settingsPath);

		Assert.Equal(expectedFirst, command[0]);
		if (expectedSecond is null)
		{
			Assert.Single(command);
		}
		else
		{
			Assert.Equal(expectedSecond, command[1]);
		}
	}
}
