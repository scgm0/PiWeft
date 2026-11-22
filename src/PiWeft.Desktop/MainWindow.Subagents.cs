using Aprillz.MewUI;
using PiWeft.Agent;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using ZLogger;

namespace PiWeft.Desktop;

/// 子代理的桌面宿主：默认只建会话、不开标签（`settings.subagents.openTabs` 可回到旧行为）。
/// 子代理在 hub 里跑，卡片行按需把活的会话挂成标签。
internal sealed class DesktopSubagentHost(MainWindow window) : ISubagentHost
{
	public Task<CodingAgentSession?> CreateChildAsync(
		SubagentStartContext context,
		CancellationToken cancellationToken) =>
		window.CreateSubagentChildAsync(context, cancellationToken);

	public Task<CodingAgentSession?> ReopenChildAsync(
		SubagentReopenContext context,
		CancellationToken cancellationToken) =>
		window.ReopenSubagentChildAsync(context, cancellationToken);
}

public sealed partial class MainWindow
{
	private sealed record SubagentChildPreparation(
		CliEnvironment Environment,
		IReadOnlyList<AgentTool>? ExtraTools,
		string Cwd,
		string? SessionDir,
		string? ParentFile,
		DesktopUiHost UiHost,
		DesktopSubagentHost Host,
		SubagentCatalog Catalog,
		SubagentHub? Hub);

	private DesktopSubagentHost? _subagentHost;

	/// 没有标签的子代理：会话与它的界面宿主都留在这里，等待卡片「打开」时再挂标签。
	/// <param name="ParentFile">父会话文件；父标签关闭时按它成批回收。</param>
	/// <param name="Hub">父会话的 hub：回收与冷恢复都问它。</param>
	/// <param name="Session">空闲回收后置空；条目留着，按同一个 hub 冷恢复，避免同一文件两份会话。</param>
	private sealed record DetachedChild(
		string ParentFile,
		CodingAgentSession? Session,
		DesktopUiHost Host,
		string Name,
		SubagentHub Hub);

	private readonly Dictionary<string, DetachedChild> _detachedChildren = new(StringComparer.Ordinal);

	/// 每拍扫一次空闲回收；没有无标签子代理时停表。
	private DispatcherTimer? _detachedSweep;

	/// 所有会话共用同一个宿主；多线程首次调用用 LazyInitializer 保证只建一个。
	private DesktopSubagentHost SubagentSessionsHost =>
		LazyInitializer.EnsureInitialized(ref _subagentHost, () => new DesktopSubagentHost(this));

	/// 工具线程调进来：建会话与读资源放后台，标签页的挂载回 UI 线程。
	internal async Task<CodingAgentSession?> CreateSubagentChildAsync(
		SubagentStartContext context,
		CancellationToken cancellationToken)
	{
		var prepared = await PrepareChildAsync(context.Parent, cancellationToken).ConfigureAwait(false);
		if (prepared is null)
		{
			return null;
		}

		CodingAgentSession child;
		string? sessionFile;
		try
		{
			(child, sessionFile) = await Task.Run(
				() =>
				{
					// 子会话头记录 parentSession，桌面侧栏与恢复都靠它认亲。
					var manager = SessionManager.Create(
						prepared.Cwd,
						prepared.SessionDir,
						parentSession: prepared.ParentFile,
						origin: "subagent");
					try
					{
						// fork：把父会话最后一段完整回合写进子会话，再交给会话构造投影。
						if (context.Seed is { Count: > 0 } seed)
						{
							foreach (var message in seed)
							{
								manager.AppendMessage(message);
							}
						}

						var session = prepared.Environment.CreateChildSession(
							_arguments,
							prepared.Cwd,
							manager,
							context,
							prepared.Catalog,
							prepared.Host,
							prepared.ExtraTools,
							prepared.UiHost);
						return (session, manager.SessionFile);
					}
					catch
					{
						DiscardLinkedChild(manager.SessionFile);
						throw;
					}
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return null;
		}

		child.SetSessionName($"{context.Name} · {ShortLabel(context.Task)}");
		return await AttachChildAsync(child, prepared, sessionFile, context.Name, cancellationToken).ConfigureAwait(false);
	}

	/// 会话装配的三样东西：环境、父会话的工具与目录、界面宿主。
	private async Task<SubagentChildPreparation?> PrepareChildAsync(
		CodingAgentSession parent,
		CancellationToken cancellationToken) =>
		await DispatchAsync(() =>
		{
			if (_sessionHostClosed || _environment is null)
			{
				return (SubagentChildPreparation?)null;
			}

			var parentFile = parent.Session.SessionFile;
			var sessionDir = parentFile is { Length: > 0 }
				? Path.GetDirectoryName(parentFile)
				: null;
			return new SubagentChildPreparation(
				EnvironmentFor(parent.Cwd),
				McpToolsFor(parent.Cwd),
				parent.Cwd,
				sessionDir,
				parentFile,
				new DesktopUiHost(),
				SubagentSessionsHost,
				parent.Subagents?.Catalog
					?? SubagentCatalog.Discover(parent.Cwd, includeProject: parent.ProjectTrusted),
				parent.Subagents);
		}, cancellationToken).ConfigureAwait(false);

	/// 把建好的子会话交给界面：默认只登记（不开标签），`openTabs` 打开时挂成后台标签。
	/// 挂不上就释放并删掉空会话文件。
	private async Task<CodingAgentSession?> AttachChildAsync(
		CodingAgentSession child,
		SubagentChildPreparation prepared,
		string? sessionFile,
		string name,
		CancellationToken cancellationToken,
		bool reopened = false)
	{
		void Announce()
		{
			// 冷恢复是常态（回合结束后会回收会话），只在真正新建时提示；
			// 打开转录或再发消息本身就是用户在操作，不需要再弹一次。
			if (!reopened)
			{
				Toasts.Show(this, Strings.SubagentStarted(child.SessionName ?? child.Session.SessionId));
			}

			AppLog.For<MainWindow>().ZLogInformation(
				$"Subagent session ready: parent={prepared.ParentFile} child={child.Session.SessionId} " +
				$"reopened={reopened} tab={prepared.Environment.Settings.Subagents.OpenTabs}");
		}

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var attached = await DispatchAsync(
				() =>
				{
					if (_sessionHostClosed)
					{
						return false;
					}

					if (prepared.Environment.Settings.Subagents.OpenTabs)
					{
						var tab = AttachTab(child);
						tab.UiHost = prepared.UiHost;
						prepared.UiHost.Attach(tab);
						UpdateTabHeaders();
						RefreshSessions(force: true);
					}
					else
					{
						if (FindTabBySessionFile(prepared.ParentFile) is not { } parent)
						{
							// 父标签已经不在了：子代理没有可问话的对象，别把它建成孤儿。
							return false;
						}

						// 问题转给父标签：子代理默认没有自己的标签，但提问必须有地方显示。
						prepared.UiHost.AttachParent(parent, name, child.Session.SessionId);
						if (prepared.Hub is { } hub)
						{
							_detachedChildren[child.Session.SessionId] = new DetachedChild(
								prepared.ParentFile ?? string.Empty,
								child,
								prepared.UiHost,
								name,
								hub);
							EnsureDetachedSweep();
						}

						// 侧栏按会话文件扫描：刷新一次，子会话才挂到父行下面。
						RefreshSessions(force: true);
					}

					Announce();
					return true;
				},
				CancellationToken.None).ConfigureAwait(false);

			if (!attached)
			{
				child.Dispose();
				DiscardLinkedChild(sessionFile);
				return null;
			}
		}
		catch
		{
			child.Dispose();
			DiscardLinkedChild(sessionFile);
			throw;
		}

		return child;
	}

	/// 冷恢复：按会话文件把已结束的子代理重新打开，让它能继续收消息。
	/// 与新建同一条装配路径，唯一的区别是 SessionManager 用 Open 而不是 Create。
	internal async Task<CodingAgentSession?> ReopenSubagentChildAsync(
		SubagentReopenContext context,
		CancellationToken cancellationToken)
	{
		var prepared = await PrepareChildAsync(context.Parent, cancellationToken).ConfigureAwait(false);
		if (prepared is null)
		{
			return null;
		}

		CodingAgentSession child;
		try
		{
			child = await Task.Run(
				() =>
				{
					var manager = SessionManager.Open(context.SessionFile, prepared.SessionDir);
					if (!string.Equals(manager.SessionId, context.SessionId, StringComparison.Ordinal))
					{
						throw new InvalidOperationException(
							$"session file {context.SessionFile} holds {manager.SessionId}, not {context.SessionId}");
					}

					return prepared.Environment.CreateChildSession(
						_arguments,
						manager.Cwd,
						manager,
						new SubagentStartContext(
							context.Parent,
							context.ParentTitle,
							context.Agent,
							context.Name,
							string.Empty,
							context.Depth)
						{
							PlanMode = context.PlanMode,
							OutputSchema = context.OutputSchema,
							// 重开的会话也要知道宿主会复核：不然模型又开始口头声称测试通过。
							Verify = context.Verify,
						},
						prepared.Catalog,
						prepared.Host,
						prepared.ExtraTools,
						prepared.UiHost);
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Reopen subagent session", exception);
			return null;
		}

		// 冷恢复不删文件：会话本来就在，里面是历史。
		return await AttachChildAsync(child, prepared, sessionFile: null, context.Name, cancellationToken, reopened: true).ConfigureAwait(false);
	}

	/// 标签标题上的一行任务摘要：取第一行，压掉多余空白，超过 32 字截断。
	private static string ShortLabel(string task)
	{
		var line = task.AsSpan();
		var newline = line.IndexOf('\n');
		if (newline >= 0)
		{
			line = line[..newline];
		}

		line = line.Trim();
		if (line.Length > 32)
		{
			line = line[..32];
		}

		return line.ToString().Replace('\r', ' ');
	}
}
