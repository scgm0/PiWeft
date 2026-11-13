using PiWeft.CodingAgent.Security;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// PiDeck 0.12 的安全策略快照：切到 PiWeft 后档位、路径边界与工具动作要照旧生效。
[Collection("process-environment")]
public sealed class PiDeckSecuritySnapshotTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-security-snapshot-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousConfig = Environment.GetEnvironmentVariable(PiDeckSecuritySnapshot.ConfigPathVariable);
	private readonly string? _previousSession = Environment.GetEnvironmentVariable(PiDeckSecuritySnapshot.SessionIdVariable);

	public PiDeckSecuritySnapshotTests()
	{
		Directory.CreateDirectory(_root);
		PiDeckSecuritySnapshot.ResetCache();
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.ConfigPathVariable, _previousConfig);
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.SessionIdVariable, _previousSession);
		PiDeckSecuritySnapshot.ResetCache();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private string WriteSnapshot(string json)
	{
		var path = Path.Combine(_root, "security-policy.json");
		File.WriteAllText(path, json);
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.ConfigPathVariable, path);
		return path;
	}

	[Fact]
	public void SnapshotMapsLevelsAndSessionOverride()
	{
		WriteSnapshot("""
			{
			  "schemaVersion": 1,
			  "enabled": true,
			  "defaultLevelId": "standard",
			  "levels": [
			    {
			      "id": "standard", "name": "标准", "description": "d", "builtin": true,
			      "toolActions": { "read": "allow", "bash": "ask" },
			      "denyBashPatterns": ["rm -rf /"],
			      "pathPolicy": "workspace", "customAllowDirs": [], "denyDirs": ["/etc"],
			      "protectSensitivePaths": true, "defaultAction": "allow"
			    },
			    {
			      "id": "strict", "name": "严格", "description": "d", "builtin": true,
			      "toolActions": { "write": "deny" },
			      "denyBashPatterns": [], "pathPolicy": "custom", "customAllowDirs": ["/tmp"],
			      "denyDirs": [], "protectSensitivePaths": true, "defaultAction": "deny"
			    }
			  ],
			  "sessionLevels": { "ses-1": "strict" }
			}
			""");
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.SessionIdVariable, "ses-1");

		var config = Assert.IsType<SecurityConfig>(PiDeckSecuritySnapshot.Load());
		Assert.True(config.Enabled);
		Assert.Equal("standard", config.DefaultLevelId);

		var standard = Assert.IsType<ToolSecurity>(config.Find("standard"));
		Assert.Equal(ToolPathPolicy.Workspace, standard.PathPolicy);
		Assert.True(standard.ProtectSensitivePaths);
		Assert.Equal(ToolAction.Ask, standard.ActionOf("bash"));
		Assert.Equal(ToolAction.Allow, standard.ActionOf("mcp_tool"));

		var strict = Assert.IsType<ToolSecurity>(config.Find("strict"));
		Assert.Equal(ToolPathPolicy.Custom, strict.PathPolicy);
		Assert.Equal(["/tmp"], strict.CustomAllowDirs);
		Assert.Equal(ToolAction.Deny, strict.ActionOf("write"));
		// 未列出的受管工具走 defaultAction；非受管工具一律放行（与 PiDeck 扩展同口径）。
		Assert.Equal(ToolAction.Deny, strict.ActionOf("read"));
		Assert.Equal(ToolAction.Allow, strict.ActionOf("mcp_tool"));

		var resolved = PiDeckSecuritySnapshot.Resolve(SecurityConfig.Off, sessionFile: null);
		Assert.Equal("strict", resolved.Level);
	}

	[Fact]
	public void InvalidSnapshotFallsBackToAllowInsteadOfNativeSettings()
	{
		WriteSnapshot("{ not json");
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.SessionIdVariable, null);

		var resolved = PiDeckSecuritySnapshot.Resolve((SecurityConfig)ToolSecurity.Strict, sessionFile: null);

		// 坏快照按扩展的 fail-safe 放行，不能用本端口更严的设置把 PiDeck 会话锁死。
		Assert.Equal(ToolSecurity.OffLevel, resolved.Level);
	}

	[Fact]
	public void WithoutEnvironmentVariableNativeSettingsWin()
	{
		Environment.SetEnvironmentVariable(PiDeckSecuritySnapshot.ConfigPathVariable, null);
		PiDeckSecuritySnapshot.ResetCache();

		Assert.Null(PiDeckSecuritySnapshot.Load());
		var resolved = PiDeckSecuritySnapshot.Resolve((SecurityConfig)ToolSecurity.Strict, sessionFile: null);
		Assert.Equal(ToolSecurity.StrictLevel, resolved.Level);
	}
}
