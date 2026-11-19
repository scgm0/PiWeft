using PiWeft.Ai;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// `session_start` / `session_shutdown` 要带 reason 与 previousSessionFile（pi 的词汇：
/// startup / reload / new / resume / fork / quit）。扩展靠它分清「退出」与「换会话」。
[Collection("process-environment")]
public sealed class ExtensionLifecycleReasonTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-extreason-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _previousAgentDir;

	public ExtensionLifecycleReasonTests()
	{
		Directory.CreateDirectory(_root);
		_previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(_root, "agent"));
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(
			"PI_CODING_AGENT_DIR",
			_previousAgentDir.Length == 0 ? null : _previousAgentDir);
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private (CodingAgentSession Session, RecordingUi Ui) CreateSession()
	{
		var extensionPath = Path.Combine(_root, "reason.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.on("session_start", function (event) {
					pi.ui.notify("start:" + event.reason + ":" + (event.previousSessionFile || ""), "info");
				});
				pi.on("session_shutdown", function (event) {
					pi.ui.notify("stop:" + event.reason, "info");
				});
			};
			""");

		var ui = new RecordingUi();
		// 用会话自己加载的扩展：注入的宿主生命周期归宿主，/reload 不会重建它。
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			UiHost = ui,
			ExtensionPaths = [extensionPath],
			DiscoverExtensions = false,
		});
		return (session, ui);
	}

	[Fact]
	public void ReloadSwitchForkAndDisposeCarryTheirReasons()
	{
		var (session, ui) = CreateSession();
		using (session)
		{
			Assert.Contains("start:startup:", ui.Notifications);

			session.ReloadResources();
			Assert.Contains("stop:reload", ui.Notifications);
			Assert.Contains("start:reload:", ui.Notifications);

			var other = SessionManager.InMemory(_root);
			session.SwitchSession(other);
			Assert.Contains("stop:resume", ui.Notifications);
			Assert.Contains(ui.Notifications, static message => message.StartsWith("start:resume:", StringComparison.Ordinal));

			var entryId = session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("fork here") });
			session.ForkAt(entryId);
			Assert.Contains("stop:fork", ui.Notifications);
			Assert.Contains(ui.Notifications, static message => message.StartsWith("start:fork:", StringComparison.Ordinal));
		}

		// Dispose 只派发一次 quit；前面的 shutdown 不能把最终退出吞掉。
		Assert.Equal(1, ui.Notifications.Count(static message => message == "stop:quit"));
	}

	private sealed class RecordingUi : IUiHost
	{
		public bool HasUi => true;

		public List<string> Notifications { get; } = [];

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public void Notify(UiNotifyRequest request) => Notifications.Add(request.Message);
	}
}
