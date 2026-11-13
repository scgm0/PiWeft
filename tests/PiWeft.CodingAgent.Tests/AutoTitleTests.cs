using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 自动会话标题：PiDeck 的 pi-deck-session-title 在本端口的等价实现。
[Collection("process-environment")]
public sealed class AutoTitleTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-autotitle-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previous = Environment.GetEnvironmentVariable(CodingAgentSession.AutoTitleVariable);

	public AutoTitleTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(CodingAgentSession.AutoTitleVariable, _previous);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private sealed class RecordingUi : IUiHost
	{
		public bool HasUi => true;

		public List<(string Key, string? Text)> Statuses { get; } = [];

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public void SetStatus(UiStatusRequest request) => Statuses.Add((request.Key, request.Text));
	}

	[Fact]
	public void CleanTitleStripsQuotesMarkdownAndEmoji()
	{
		Assert.Equal("修复登录流程", AutoTitle.CleanTitle("「修复登录流程」"));
		Assert.Equal("Fix login flow", AutoTitle.CleanTitle("**Fix login flow**"));
		Assert.Equal("修复登录流程", AutoTitle.CleanTitle("🚀 修复登录流程。"));
		Assert.Equal("Fix the parser", AutoTitle.CleanTitle("Title: Fix the parser"));
		Assert.Equal("Fix the parser", AutoTitle.CleanTitle("```text\nFix the parser\n```"));
	}

	[Fact]
	public void CleanTitleRejectsGenericMarkersAndSecrets()
	{
		Assert.Null(AutoTitle.CleanTitle("新会话"));
		Assert.Null(AutoTitle.CleanTitle("Untitled"));
		Assert.Null(AutoTitle.CleanTitle("A"));
		// 上下文代理的确认标记行不是标题。
		Assert.Null(AutoTitle.CleanTitle("[ACP] acp_status result: ok"));
		Assert.Null(AutoTitle.CleanTitle("[ACP] Compressed 3 ranges, saved 12k tokens"));
		// 标题本身命中凭据模式：丢弃，而不是写入脱敏占位符。
		Assert.Null(AutoTitle.CleanTitle("sk-abcdefghijklmnopqrstuvwxyz"));
	}

	[Fact]
	public void BuildSkipsCommandsAndUsesBlockLabelsWhenOnlyReferencesWereInserted()
	{
		Assert.Null(AutoTitle.Build("/model", "done"));

		var blockOnly = Assert.IsType<AutoTitle.TitleInput>(AutoTitle.Build(
			"<prompt_template name=\"翻译官\">\n一大段模板正文\n</prompt_template>",
			"done"));
		Assert.Equal("/翻译官", blockOnly.DirectTitle);
		Assert.Null(blockOnly.UserText);

		var mixed = Assert.IsType<AutoTitle.TitleInput>(AutoTitle.Build(
			"<skill name=\"review\">\n规则正文\n</skill>\n帮我看看这个 PR",
			"done"));
		Assert.Null(mixed.DirectTitle);
		Assert.Equal("帮我看看这个 PR", mixed.UserText);
	}

	[Fact]
	public void PrepareInputTextRedactsSecretsAndTruncates()
	{
		var prepared = AutoTitle.PrepareInputText("key sk-abcdefghijklmnopqrstuvwxyz and Bearer abcdefghijklmn", AutoTitle.MaxUserInputChars);

		Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", prepared);
		Assert.DoesNotContain("abcdefghijklmn", prepared);

		var longText = new string('字', AutoTitle.MaxUserInputChars + 50);
		Assert.Equal(AutoTitle.MaxUserInputChars, AutoTitle.PrepareInputText(longText, AutoTitle.MaxUserInputChars).EnumerateRunes().Count());
	}

	[Fact]
	public async Task SettledTurnNamesAFreshSessionThroughTheStatusMarker()
	{
		Environment.SetEnvironmentVariable(CodingAgentSession.AutoTitleVariable, "1");
		var script = new ScriptedStreamFn();
		script.EnqueueText("已经改好了。");
		// 第二次调用是标题旁路请求。
		script.EnqueueText("修复登录流程");

		var ui = new RecordingUi();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			UiHost = ui,
		});

		var events = new List<string>();
		using var subscription = session.Subscribe(@event =>
		{
			lock (events)
			{
				if (@event is SessionInfoChangedEvent)
				{
					events.Add("rename");
				}
			}

			return ValueTask.CompletedTask;
		});

		await session.PromptAsync("帮我修一下登录流程");
		await WaitForNameAsync(session);

		Assert.Equal("修复登录流程", session.SessionName);
		var marker = Assert.Single(ui.Statuses, static status => status.Key == CodingAgentSession.AutoTitleStatusKey);
		Assert.Equal("修复登录流程", marker.Text);
		lock (events)
		{
			Assert.Equal(["rename"], events);
		}
	}

	[Fact]
	public async Task ManualRenameCancelsAutomaticNaming()
	{
		Environment.SetEnvironmentVariable(CodingAgentSession.AutoTitleVariable, "1");
		var script = new ScriptedStreamFn();
		script.EnqueueText("已经改好了。");
		script.EnqueueText("自动标题");

		var ui = new RecordingUi();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			UiHost = ui,
		});

		session.SetSessionName("我自己起的");
		await session.PromptAsync("帮我修一下登录流程");
		await Task.Delay(200);

		Assert.Equal("我自己起的", session.SessionName);
		Assert.DoesNotContain(ui.Statuses, static status => status.Key == CodingAgentSession.AutoTitleStatusKey);
		// 旁路没有发请求：只有主轮那一次模型调用。
		Assert.Single(script.Requests);
	}

	[Fact]
	public async Task WithoutTheEnvironmentVariableSessionsKeepTheirName()
	{
		Environment.SetEnvironmentVariable(CodingAgentSession.AutoTitleVariable, null);
		var script = new ScriptedStreamFn();
		script.EnqueueText("已经改好了。");

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
		});

		await session.PromptAsync("帮我修一下登录流程");
		await Task.Delay(200);

		Assert.Null(session.SessionName);
	}

	private static async Task WaitForNameAsync(CodingAgentSession session)
	{
		for (var attempt = 0; attempt < 100 && session.SessionName is null; attempt++)
		{
			await Task.Delay(20);
		}
	}
}
