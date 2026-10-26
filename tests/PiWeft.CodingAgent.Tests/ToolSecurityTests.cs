using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Security;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 工具调用安全门。默认等级必须是完全放行（<c>off</c>）——引入这道门不能改变任何既有行为；
/// 上保护靠显式选 <c>standard</c> / <c>strict</c>。
/// </summary>
public class ToolSecurityTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-security-" + Guid.NewGuid().ToString("N")[..8]);

	public ToolSecurityTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private static JsonObject Args(params (string Key, string Value)[] pairs)
	{
		var node = new JsonObject();
		foreach (var (key, value) in pairs)
		{
			node[key] = value;
		}

		return node;
	}

	private static JsonObject PathArg(string path) => Args(("path", path));

	private static JsonObject CmdArg(string command) => Args(("command", command));

	// ---------------------------------------------------------------- 默认值

	[Fact]
	public void DefaultLevelIsCompletelyPermissive()
	{
		var security = ToolSecurity.Off;

		Assert.False(security.Enabled);
		Assert.Null(security.BlockingReason("bash", CmdArg("rm -rf /"), _root));
		Assert.Null(security.BlockingReason("write", PathArg("/etc/passwd"), _root));
		Assert.Null(security.BlockingReason("read", PathArg("/etc/shadow"), _root));

		// 缺省构造出来的也是 off。
		Assert.False(new ToolSecurity().Enabled);
	}

	[Fact]
	public void SettingsWithoutSecuritySectionStaysOff()
	{
		var settings = PiWeft.CodingAgent.Settings.CodingAgentSettings.Merge(null, null);
		Assert.False(settings.Security.Enabled);
		Assert.Null(settings.Security.BlockingReason("bash", CmdArg("rm -rf /"), _root));
	}

	[Fact]
	public void UnknownLevelFallsBackToOffRatherThanGuessingStricter()
	{
		Assert.False(ToolSecurity.FromLevel("paranoid").Enabled);
		Assert.False(ToolSecurity.FromLevel(null).Enabled);
		Assert.False(ToolSecurity.FromLevel("").Enabled);
	}

	// ---------------------------------------------------------------- standard

	[Fact]
	public void StandardBlocksSensitiveFilesForReadAndWrite()
	{
		var security = ToolSecurity.Standard;

		Assert.Contains("敏感文件", security.BlockingReason("read", PathArg(Path.Combine(_root, ".env")), _root));
		Assert.Contains("敏感文件", security.BlockingReason("write", PathArg(Path.Combine(_root, ".env.local")), _root));
		Assert.Contains("敏感文件", security.BlockingReason("edit", PathArg(Path.Combine(_root, "id_rsa")), _root));
		Assert.Contains("敏感文件", security.BlockingReason("grep", PathArg(Path.Combine(_root, ".git")), _root));
		Assert.Contains("敏感文件", security.BlockingReason("read", PathArg("certs/server.pem"), _root));
		Assert.Contains("敏感文件", security.BlockingReason("read", PathArg("keys/client.key"), _root));
		Assert.Contains("敏感文件", security.BlockingReason("read", PathArg(".npmrc"), _root));

		// 普通文件不受影响。
		Assert.Null(security.BlockingReason("read", PathArg("README.md"), _root));
		Assert.Null(security.BlockingReason("write", PathArg("src/App.cs"), _root));
	}

	[Fact]
	public void StandardStillAllowsDirectoriesOutsideTheWorkspace()
	{
		// standard 的 pathPolicy 是 unrestricted：目录不限制，只有敏感文件与危险命令受管。
		var security = ToolSecurity.Standard;
		Assert.Null(security.BlockingReason("write", PathArg("/tmp/somewhere/else.txt"), _root));
	}

	[Theory]
	[InlineData("rm -rf /")]
	[InlineData("rm -rf /*")]
	[InlineData("rm -rf ~")]
	[InlineData("sudo rm -rf $HOME")]
	[InlineData("mkfs.ext4 /dev/sda1")]
	[InlineData("dd if=/dev/zero of=/dev/sda bs=1M")]
	[InlineData("echo x > /dev/nvme0n1")]
	[InlineData(":(){ :|:& };:")]
	[InlineData("shutdown -h now")]
	[InlineData("chmod 777 /")]
	[InlineData("format C:")]
	public void StandardDeniesCatastrophicCommands(string command)
	{
		Assert.NotNull(ToolSecurity.Standard.BlockingReason("bash", CmdArg(command), _root));
		Assert.NotNull(ToolSecurity.Standard.BlockingReason("powershell", CmdArg(command), _root));
	}

	[Theory]
	[InlineData("rm -rf node_modules")]
	[InlineData("rm -rf ./build")]
	[InlineData("npm install")]
	[InlineData("cp a.txt b.txt")]
	[InlineData("mv old.txt new.txt")]
	[InlineData("git commit -m 'x'")]
	[InlineData("git push --force")]
	[InlineData("dotnet build")]
	[InlineData("chmod +x run.sh")]
	[InlineData("ls -la && cat README.md")]
	public void StandardAllowsEverydayCommands(string command)
	{
		// 本端口没有「先确认再执行」的审批窗口，所以日常动作不能被拒绝 ——
		// 危险命令表只收不可逆的破坏性操作。
		Assert.Null(ToolSecurity.Standard.BlockingReason("bash", CmdArg(command), _root));
	}

	[Fact]
	public void DenyDirsWinsOverEverythingWithNoEscapeHatch()
	{
		var security = ToolSecurity.Standard with { DenyDirs = ["secrets", Path.Combine(_root, "vault")] };

		Assert.NotNull(security.BlockingReason("read", PathArg("secrets/token.txt"), _root));
		Assert.NotNull(security.BlockingReason("write", PathArg("secrets/token.txt"), _root));
		Assert.NotNull(security.BlockingReason("ls", PathArg("secrets"), _root));
		Assert.NotNull(security.BlockingReason("write", PathArg(Path.Combine(_root, "vault", "a.txt")), _root));

		// 名字相似但不在禁地内的路径不受影响。
		Assert.Null(security.BlockingReason("read", PathArg("secrets.md"), _root));
	}

	// ---------------------------------------------------------------- strict

	[Fact]
	public void StrictDeniesWritesOutsideWorkspaceButAllowsReads()
	{
		var security = ToolSecurity.Strict;

		var outside = Path.Combine(Path.GetTempPath(), "piweft-outside-" + Guid.NewGuid().ToString("N")[..8], "a.txt");
		Assert.Contains("工作目录之外", security.BlockingReason("write", PathArg(outside), _root));
		Assert.Contains("工作目录之外", security.BlockingReason("edit", PathArg(outside), _root));

		// 读不设边界（与 PiDeck 一致：越界读降级为放行）。
		Assert.Null(security.BlockingReason("read", PathArg(outside), _root));

		// 工作目录内正常放行；相对路径与绝对路径等价。
		Assert.Null(security.BlockingReason("write", PathArg("src/App.cs"), _root));
		Assert.Null(security.BlockingReason("write", PathArg(Path.Combine(_root, "src", "App.cs")), _root));
	}

	[Fact]
	public void StrictRejectsDotDotEscape()
	{
		var security = ToolSecurity.Strict;
		Assert.Contains("工作目录之外", security.BlockingReason("write", PathArg("../escape.txt"), _root));
		Assert.Contains("工作目录之外", security.BlockingReason("write", PathArg("src/../../escape.txt"), _root));
	}

	[Fact]
	public void CustomPolicyAllowsListedDirectories()
	{
		var allowed = Path.Combine(Path.GetTempPath(), "piweft-allowed-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(allowed);
		try
		{
			var security = ToolSecurity.Strict with
			{
				PathPolicy = ToolPathPolicy.Custom,
				CustomAllowDirs = [allowed],
			};

			Assert.Null(security.BlockingReason("write", PathArg(Path.Combine(allowed, "ok.txt")), _root));
			Assert.Contains("工作目录之外", security.BlockingReason("write", PathArg(Path.Combine(Path.GetTempPath(), "nope.txt")), _root));
		}
		finally
		{
			Directory.Delete(allowed, recursive: true);
		}
	}

	[Fact]
	public void SymlinkEscapeIsNotAWayAroundTheBoundary()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // Windows 上建符号链接通常需要管理员权限，这条只能在其平台上验证。
		}

		var outside = Path.Combine(Path.GetTempPath(), "piweft-link-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(outside);
		var link = Path.Combine(_root, "escape");
		try
		{
			Directory.CreateSymbolicLink(link, outside);

			// 指向工作目录外的软链目录：解析真实路径后仍然越界。
			Assert.Contains("工作目录之外", ToolSecurity.Strict.BlockingReason("write", PathArg("escape/owned.txt"), _root));

			// 同样的软链指向 .env：敏感文件规则要按真实路径命中。
			var envTarget = Path.Combine(outside, ".env");
			File.WriteAllText(envTarget, "SECRET=1");
			var envLink = Path.Combine(_root, "notes.txt");
			File.CreateSymbolicLink(envLink, envTarget);
			Assert.Contains("敏感文件", ToolSecurity.Standard.BlockingReason("read", PathArg("notes.txt"), _root));
		}
		finally
		{
			Directory.Delete(outside, recursive: true);
		}
	}

	// ---------------------------------------------------------------- 边界与非目标

	[Fact]
	public void ToolsWithoutPathSemanticsAreNotJudged()
	{
		var security = ToolSecurity.Strict;

		// MCP 与自定义工具没有可判定的路径语义，与 PiDeck 只覆盖内置工具集一致。
		Assert.Null(security.BlockingReason("mcp__fs__write", Args(("path", "/etc/passwd")), _root));
		Assert.Null(security.BlockingReason("todo", new JsonObject(), _root));
		Assert.Null(security.BlockingReason("ask_question", new JsonObject(), _root));
		Assert.Null(security.BlockingReason("codemode", Args(("code", "1")), _root));
	}

	[Fact]
	public void MissingPathArgumentIsLeftToTheTool()
	{
		var security = ToolSecurity.Strict;
		Assert.Null(security.BlockingReason("ls", new JsonObject(), _root));
		Assert.Null(security.BlockingReason("read", new JsonObject(), _root));
		Assert.Null(security.BlockingReason("write", Args(("path", string.Empty)), _root));
	}

	[Fact]
	public void EmptyDenyPatternEntryDoesNotBlockEverything()
	{
		// 空串正则会匹配任意命令 = 全部拒绝；这类空行必须在配置边界被丢掉。
		var security = ToolSecurity.Standard with { DenyBashPatterns = ["", "   "] };
		Assert.Null(security.BlockingReason("bash", CmdArg("echo hi"), _root));
	}

	[Fact]
	public void InvalidRegexEntryIsSkippedInsteadOfCrashingTheGate()
	{
		var security = ToolSecurity.Standard with { DenyBashPatterns = ["[unclosed", @"\bmkfs\b"] };
		Assert.Null(security.BlockingReason("bash", CmdArg("echo hi"), _root));
		Assert.NotNull(security.BlockingReason("bash", CmdArg("mkfs.ext4 /dev/sdb1"), _root));
	}
}
