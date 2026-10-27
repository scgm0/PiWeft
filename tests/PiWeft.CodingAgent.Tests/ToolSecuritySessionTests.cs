using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 安全门的接线验证：纯策略单测在 <see cref="ToolSecurityTests"/>，这里证明它真的挂在
/// 工具调用链路上 —— 直接工具调用与 codemode 嵌套调用都要被拦住，<c>off</c> 等级下必须完全不干预。
/// </summary>
public class ToolSecuritySessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-security-session-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _outside = Path.Combine(Path.GetTempPath(), "piweft-security-outside-" + Guid.NewGuid().ToString("N")[..8]);

	public ToolSecuritySessionTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_outside);
	}

	public void Dispose()
	{
		foreach (var directory in new[] { _root, _outside })
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (IOException)
			{
				// 临时目录清理失败不影响断言结论。
			}
		}
	}

	private CodingAgentSession CreateSession(ScriptedStreamFn script, ToolSecurity security, bool includeCodemode = false) =>
		new(new CodingAgentSessionOptions
		{
			// codemode 不在默认工具集里（与 pi 一致，要显式选中），需要它的用例自己带上完整目录。
			Tools = includeCodemode ? PiWeft.CodingAgent.Tools.BuiltinTools.CatalogTools(_root) : null,
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings { Security = security },
		});

	private static ToolResultMessage LastToolResult(CodingAgentSession session) =>
		Assert.IsType<ToolResultMessage>(
			session.Session.Entries.OfType<SessionMessageEntry>().Last(static entry => entry.Message is ToolResultMessage).Message);

	[Fact]
	public async Task OffLevelWritesOutsideTheWorkspace()
	{
		var target = Path.Combine(_outside, "written.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "write", new JsonObject { ["path"] = target, ["content"] = "hello" });
		script.EnqueueText("done");

		using var session = CreateSession(script, ToolSecurity.Off);
		await session.PromptAsync("write it");

		var result = LastToolResult(session);
		Assert.False(result.IsError);
		Assert.Equal("hello", File.ReadAllText(target));
	}

	[Fact]
	public async Task StrictLevelBlocksTheSameWriteAndTellsTheModelWhy()
	{
		var target = Path.Combine(_outside, "blocked.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "write", new JsonObject { ["path"] = target, ["content"] = "hello" });
		script.EnqueueText("ok");

		using var session = CreateSession(script, ToolSecurity.Strict);
		await session.PromptAsync("write it");

		var result = LastToolResult(session);
		Assert.True(result.IsError);
		Assert.Contains("安全策略", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Contains("工作目录之外", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.False(File.Exists(target));
	}

	[Fact]
	public async Task StandardLevelStopsSensitiveReadsAndDangerousShellCommands()
	{
		File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=1");

		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "read", new JsonObject { ["path"] = ".env" });
		script.EnqueueToolCall("c2", "bash", new JsonObject { ["command"] = "rm -rf /" });
		script.EnqueueToolCall("c3", "read", new JsonObject { ["path"] = "README.md" });
		script.EnqueueText("done");
		File.WriteAllText(Path.Combine(_root, "README.md"), "hi");

		using var session = CreateSession(script, ToolSecurity.Standard);
		await session.PromptAsync("go");

		var results = session.Session.Entries.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<ToolResultMessage>()
			.ToList();

		Assert.Equal(3, results.Count);
		Assert.True(results[0].IsError);
		Assert.Contains("敏感文件", Assert.IsType<TextContent>(results[0].Content[0]).Text);

		Assert.True(results[1].IsError);
		Assert.Contains("危险模式", Assert.IsType<TextContent>(results[1].Content[0]).Text);

		// 普通文件照常可读：安全门不是「一刀切拒绝」。
		Assert.False(results[2].IsError);
		Assert.Contains("hi", Assert.IsType<TextContent>(results[2].Content[0]).Text);
	}

	[Fact]
	public async Task NestedCodemodeCallsGoThroughTheSameGate()
	{
		// codemode 的嵌套调用走 ExecuteNestedToolAsync，它复用同一个 beforeToolCall 钩子；
		// 这条钉住「嵌套调用不能成为绕过口」。
		var target = Path.Combine(_outside, "nested.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "codemode", new JsonObject
		{
			["code"] = $"const r = await tools.write({{ path: {JsonValue.Create(target)!.ToJsonString()}, content: 'x' }}); return r;",
		});
		script.EnqueueText("done");

		using var session = CreateSession(script, ToolSecurity.Strict, includeCodemode: true);
		Assert.Contains(session.Tools, static tool => tool.Name == "codemode");

		await session.PromptAsync("go");

		// codemode 自己成功返回（脚本跑完了），但嵌套的写必须被拒绝。
		Assert.False(File.Exists(target));
		var texts = string.Join(
			"\n",
			session.Session.Entries.OfType<SessionMessageEntry>()
				.Select(static entry => entry.Message)
				.OfType<ToolResultMessage>()
				.SelectMany(static result => result.Content.OfType<TextContent>())
				.Select(static text => text.Text));
		Assert.Contains("工作目录之外", texts);
	}

	[Fact]
	public void StaticPatternTablesAreLoaded()
	{
		// 静态属性按文本顺序初始化：模式表若声明在档位快照之后，Standard 拿到的就是 null，
		// 表现为「危险命令表静默失效」而不是编译错误。这条把这个顺序约束钉住。
		Assert.NotEmpty(ToolSecurity.Standard.DenyBashPatterns);
		Assert.NotEmpty(ToolSecurity.Strict.DenyBashPatterns);
		Assert.Equal(3, ToolSecurity.Builtins.Count);
		Assert.NotEmpty(Assert.Single(ToolSecurity.Builtins, static level => level.Level == "standard").DenyBashPatterns);
	}

	[Fact]
	public void ConfigResolvesLevelsAndSessionOverrides()
	{
		var config = new SecurityConfig
		{
			DefaultLevelId = "standard",
			SessionLevels = new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp/x/a.jsonl"] = "strict" },
		};

		Assert.True(config.Enabled);
		Assert.Equal("standard", config.Resolve(null).Level);
		Assert.Equal("strict", config.Resolve("/tmp/x/a.jsonl").Level);

		// 覆盖指向不存在的档位时退回默认档，而不是卡死或猜一个更严的档。
		var broken = config with
		{
			SessionLevels = new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp/x/b.jsonl"] = "nope" },
		};
		Assert.Equal("standard", broken.Resolve("/tmp/x/b.jsonl").Level);

		// 总开关优先于一切档位与覆盖。
		Assert.Equal("off", (config with { Enabled = false }).Resolve("/tmp/x/a.jsonl").Level);
	}

	[Fact]
	public void ToolActionsAskAndDeny()
	{
		var level = ToolSecurity.Off with
		{
			Level = "review",
			Name = "需确认",
			ToolActions = new Dictionary<string, string>(StringComparer.Ordinal) { ["bash"] = "ask", ["edit"] = "deny" },
		};

		Assert.Equal(SecurityEffect.Ask, level.Evaluate("bash", new JsonObject { ["command"] = "ls" }, _root).Effect);
		Assert.Equal(SecurityEffect.Deny, level.Evaluate("edit", new JsonObject { ["path"] = "a.cs" }, _root).Effect);
		// 未列出的工具走兜底动作（默认 allow）。
		Assert.Equal(SecurityEffect.Allow, level.Evaluate("read", new JsonObject { ["path"] = "a.cs" }, _root).Effect);

		// ask 也要先过路径与危险命令检查：不可逆操作不该靠一次回车放行。
		var strictAsk = ToolSecurity.Strict with
		{
			ToolActions = new Dictionary<string, string>(StringComparer.Ordinal) { ["write"] = "ask" },
		};
		Assert.Equal(SecurityEffect.Deny, strictAsk.Evaluate("write", new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "outside-ask.txt") }, _root).Effect);
		Assert.Equal(SecurityEffect.Ask, strictAsk.Evaluate("write", new JsonObject { ["path"] = "inside.cs" }, _root).Effect);

		// BlockingReason 只报拒绝：Ask 不是拒绝。
		Assert.Null(level.BlockingReason("bash", new JsonObject { ["command"] = "ls" }, _root));
	}

	[Fact]
	public void ConfigJsonRoundTripsThroughSettings()
	{
		var custom = ToolSecurity.Off with
		{
			Level = "release",
			Name = "发布分支",
			Description = "只读 + 禁地",
			Builtin = false,
			ProtectSensitivePaths = true,
			DenyDirs = ["secrets"],
			DenyBashPatterns = [@"\bgit\b"],
			ToolActions = new Dictionary<string, string>(StringComparer.Ordinal) { ["bash"] = "ask" },
		};

		var config = new SecurityConfig
		{
			DefaultLevelId = "release",
			Levels = [.. ToolSecurity.Builtins, custom],
			SessionLevels = new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp/a.jsonl"] = "strict" },
		};

		var restored = SecurityConfig.FromJson(JsonNode.Parse(config.ToJson().ToJsonString()));

		Assert.Equal("release", restored.DefaultLevelId);
		Assert.Equal(4, restored.Levels.Count);
		var roundTripped = restored.Find("release");
		Assert.NotNull(roundTripped);
		Assert.False(roundTripped!.Builtin);
		Assert.Equal("发布分支", roundTripped.Name);
		Assert.Equal(["secrets"], roundTripped.DenyDirs);
		Assert.Equal([@"\bgit\b"], roundTripped.DenyBashPatterns);
		Assert.Equal("ask", roundTripped.ActionOf("bash"));
		Assert.Equal("strict", restored.Resolve("/tmp/a.jsonl").Level);
		// 没被改动过的内建档不落盘，读回来仍是内建形状。
		Assert.True(restored.Find("standard")!.ProtectSensitivePaths);
		Assert.NotEmpty(restored.Find("standard")!.DenyBashPatterns);
	}

	[Fact]
	public void DefaultConfigWritesNoSettingsKeys()
	{
		// 默认形状（门开着但什么都不限制）不该往用户的 settings.json 里塞一堆默认键。
		Assert.Empty(new SecurityConfig().ToJson());
		Assert.Equal("off", SecurityConfig.Off.Resolve(null).Level);
	}

	[Fact]
	public void LegacyFlatSecuritySectionStillLoads()
	{
		// 上一轮的形状是扁平字段（level/pathPolicy/customAllowDirs/...），必须继续能读。
		var config = SecurityConfig.FromJson(JsonNode.Parse(
			"""{"level":"strict","pathPolicy":"custom","customAllowDirs":["~/shared"],"protectSensitivePaths":true}"""));

		Assert.Equal("strict", config.DefaultLevelId);
		var level = config.Resolve(null);
		Assert.Equal(ToolPathPolicy.Custom, level.PathPolicy);
		Assert.Equal(["~/shared"], level.CustomAllowDirs);
		Assert.True(level.ProtectSensitivePaths);
	}

	[Fact]
	public void LevelsCanBeAddedAndRemoved()
	{
		var config = new SecurityConfig();
		var custom = ToolSecurity.Off with { Level = "mine", Name = "我的", Builtin = false };

		config = config.WithLevel(custom) with { DefaultLevelId = "mine" };
		Assert.Equal("mine", config.Resolve(null).Level);

		// 内建档删不掉。
		Assert.Equal(config.Levels.Count, config.WithoutLevel("standard").Levels.Count);

		var removed = config.WithoutLevel("mine");
		Assert.Null(removed.Find("mine"));
		// 删掉当前默认档时回退 off，而不是留一个悬空引用。
		Assert.Equal("off", removed.DefaultLevelId);
	}

	[Fact]
	public void SettingsCarryTheGlobalSecurityConfig()
	{
		// 设置对象里带的是整个配置容器；项目级设置不能参与（见 SecurityConfig.Load）。
		var strict = CodingAgentSettings.Merge(null, null) with { Security = ToolSecurity.Strict };
		Assert.True(strict.Security.Enabled);
		Assert.Equal("strict", strict.Security.Resolve(null).Level);
		Assert.Contains("工作目录之外", strict.Security.Resolve(null).BlockingReason("write", new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "x.txt") }, _root) ?? string.Empty);

		Assert.Equal("off", CodingAgentSettings.Merge(null, null).Security.Resolve(null).Level);
	}

	[Fact]
	public void SaveAndLoadRoundTripThroughSettingsJson()
	{
		var agentDir = Path.Combine(_root, "agent");
		var config = new SecurityConfig
		{
			DefaultLevelId = "standard",
			SessionLevels = new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp/s.jsonl"] = "strict" },
		};

		SecurityConfig.Save(config, agentDir);

		// 只写 security 段：用户手写的其它键不能被覆盖。
		File.AppendAllText(
			Path.Combine(agentDir, "settings.json"),
			string.Empty);
		var restored = SecurityConfig.Load(agentDir);
		Assert.Equal("standard", restored.DefaultLevelId);
		Assert.Equal("strict", restored.Resolve("/tmp/s.jsonl").Level);

		// 回到默认形状时把该键删掉，不留一堆等于默认的字段。
		SecurityConfig.Save(SecurityConfig.Off, agentDir);
		Assert.DoesNotContain("security", File.ReadAllText(Path.Combine(agentDir, "settings.json")), StringComparison.Ordinal);
	}

	[Fact]
	public void SavedSettingsKeepForeignKeysAndDropDefaults()
	{
		// 这是设置页真正走的路径：写盘 → 重读 → 生效档位一致。
		var agentDir = Path.Combine(_root, "agent-a");
		var config = new SecurityConfig
		{
			DefaultLevelId = "standard",
			Levels = [.. ToolSecurity.Builtins, ToolSecurity.Off with { Level = "release", Name = "发布", Builtin = false, DenyDirs = ["secrets"] }],
			SessionLevels = new Dictionary<string, string>(StringComparer.Ordinal) { ["/tmp/s.jsonl"] = "release" },
		};

		SecurityConfig.Save(config, agentDir);
		var restored = SecurityConfig.Load(agentDir);

		Assert.Equal("standard", restored.DefaultLevelId);
		Assert.Equal("release", restored.Resolve("/tmp/s.jsonl").Level);
		Assert.Equal(["secrets"], restored.Find("release")!.DenyDirs);
		// 内建档不落盘，读回来仍是内建形状（含敏感文件保护与危险命令表）。
		Assert.True(restored.Find("standard")!.ProtectSensitivePaths);
		Assert.NotEmpty(restored.Find("standard")!.DenyBashPatterns);

		// 其它设置键不能被安全门的写入吃掉。
		var settingsPath = Path.Combine(agentDir, "settings.json");
		// 插一个无关键（安全门的写入必须保留用户手写的其它设置）。落盘是缩进格式，
		// 所以按 JSON 改而不是拼字符串。
		var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
		root.Insert(0, "theme", "dark");
		File.WriteAllText(settingsPath, root.ToJsonString());
		Assert.Equal("release", SecurityConfig.Load(agentDir).Resolve("/tmp/s.jsonl").Level);
		Assert.Contains("\"theme\"", File.ReadAllText(Path.Combine(agentDir, "settings.json")), StringComparison.Ordinal);

		// 回到默认形状时删掉该键，不留一堆等于默认的字段。
		SecurityConfig.Save(SecurityConfig.Off, agentDir);
		Assert.DoesNotContain("security", File.ReadAllText(Path.Combine(agentDir, "settings.json")), StringComparison.Ordinal);
	}

	[Fact]
	public void LegacyFlatShapeSurvivesSaveAndLoad()
	{
		// 上一轮的形状是平铺字段。读进来必须映射到档位上，再写回去也不能把用户改的东西丢掉。
		var agentDir = Path.Combine(_root, "agent-b");
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "settings.json"),
			"""{"security":{"level":"strict","pathPolicy":"custom","customAllowDirs":["~/shared"],"protectSensitivePaths":true}}""");

		var loaded = SecurityConfig.Load(agentDir);
		Assert.Equal(ToolPathPolicy.Custom, loaded.Resolve(null).PathPolicy);
		Assert.Equal(["~/shared"], loaded.Resolve(null).CustomAllowDirs);

		SecurityConfig.Save(loaded, agentDir);
		var again = SecurityConfig.Load(agentDir);
		Assert.Equal(ToolPathPolicy.Custom, again.Resolve(null).PathPolicy);
		Assert.Equal(["~/shared"], again.Resolve(null).CustomAllowDirs);
	}

	[Fact]
	public void LoadRefusesToOverwriteCorruptSettings()
	{
		// 现有文件不是合法 JSON：宁可报错也不能凭空造一份覆盖掉用户的全部设置。
		var agentDir = Path.Combine(_root, "agent-c");
		Directory.CreateDirectory(agentDir);
		var path = Path.Combine(agentDir, "settings.json");
		File.WriteAllText(path, "{ 这不是 JSON");

		Assert.Throws<InvalidDataException>(() => SecurityConfig.Save(SecurityConfig.Off with { DefaultLevelId = "standard" }, agentDir));
		Assert.Equal("{ 这不是 JSON", File.ReadAllText(path));

		// 读取端要能扛住同样的坏文件：按「没有安全门」处理而不是抛出去。
		Assert.Equal("off", SecurityConfig.Load(agentDir).Resolve(null).Level);
	}
}
