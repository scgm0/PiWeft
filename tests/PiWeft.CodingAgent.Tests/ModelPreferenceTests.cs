using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 「记住会话使用的模型与思考级别」（对齐 PiDeck 的 lastUsedModel / modelThinkingLevels）：
/// 每模型默认档位写进 settings.json；恢复会话时记录的模型优先于宿主默认。
/// </summary>
public sealed class ModelPreferenceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-model-pref-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;
	private readonly string _project;

	public ModelPreferenceTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_project = Path.Combine(_root, "project");
		Directory.CreateDirectory(_agentDir);
		Directory.CreateDirectory(_project);
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
	public void SetModelThinkingLevelMergesThePerModelTable()
	{
		var path = CodingAgentSettings.GlobalPath(_agentDir);
		File.WriteAllText(path, """{"defaultThinkingLevel":"low","unknownKey":{"keep":true}}""");

		CodingAgentSettings.SetModelThinkingLevel("test", "a", "high", _agentDir);
		CodingAgentSettings.SetModelThinkingLevel("test", "b", "max", _agentDir);

		var settings = CodingAgentSettings.Load(_project, _agentDir, includeProject: false);
		Assert.Equal("high", settings.ModelThinkingLevels["test/a"]);
		Assert.Equal("max", settings.ModelThinkingLevels["test/b"]);
		Assert.Equal("low", settings.DefaultThinkingLevel);
		// 未知字段必须原样保留：写设置不能吃掉用户的其它配置。
		var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		Assert.True(raw["unknownKey"]!["keep"]!.GetValue<bool>());
	}

	[Fact]
	public void RecordedModelWinsOverTheHostDefaultWhenRestoring()
	{
		var recorded = ScriptedSessions.Model("recorded");
		var hostDefault = ScriptedSessions.Model("host-default");
		var registry = new ModelRegistry();
		registry.Add(recorded);
		registry.Add(hostDefault);

		var manager = SessionManager.InMemory(_project);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello") });
		manager.AppendModelChange(recorded.Provider, recorded.Id);

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _project,
			Model = hostDefault,
			StreamFn = new ScriptedStreamFn().Invoke,
			Registry = registry,
			RestoreRecordedModel = true,
			Session = manager,
			Skills = [],
			Settings = new CodingAgentSettings(),
		});

		Assert.Equal(recorded.Id, session.Model.Id);
	}

	[Fact]
	public void MissingRecordedModelFallsBackToTheHostDefault()
	{
		var hostDefault = ScriptedSessions.Model("host-default");
		var registry = new ModelRegistry();
		registry.Add(hostDefault);

		var manager = SessionManager.InMemory(_project);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello") });
		manager.AppendModelChange("test", "deleted-model");

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _project,
			Model = hostDefault,
			StreamFn = new ScriptedStreamFn().Invoke,
			Registry = registry,
			RestoreRecordedModel = true,
			Session = manager,
			Skills = [],
			Settings = new CodingAgentSettings(),
		});

		Assert.Equal(hostDefault.Id, session.Model.Id);
	}
}
