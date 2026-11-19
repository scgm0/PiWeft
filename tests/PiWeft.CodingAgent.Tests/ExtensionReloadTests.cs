using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <c>/reload</c> 重建扩展宿主时，生命周期事件要接上：旧宿主收 <c>session_shutdown</c>，
/// 新宿主收 <c>session_start</c>。否则依赖它初始化的扩展在热重载后停摆。
/// 用例改 <c>PI_CODING_AGENT_DIR</c>，归到串行集合。
[Collection("process-environment")]
public sealed class ExtensionReloadTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-extreload-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;
	private readonly string _previousAgentDir;

	public ExtensionReloadTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
		_previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
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

	[Fact]
	public void ReloadResourcesEmitsShutdownThenStartAgain()
	{
		var extensionPath = Path.Combine(_root, "reload.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.on("session_start", function () { pi.ui.notify("session_start", "info"); });
				pi.on("session_shutdown", function () { pi.ui.notify("session_shutdown", "info"); });
			};
			""");

		var ui = new RecordingUi();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
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

		Assert.Single(ui.Notifications, "session_start");

		session.ReloadResources();

		Assert.Equal(2, ui.Notifications.Count(static message => message == "session_start"));
		Assert.Single(ui.Notifications, "session_shutdown");
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
