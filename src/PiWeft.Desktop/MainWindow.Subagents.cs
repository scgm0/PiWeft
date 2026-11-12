using PiWeft.Agent;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using ZLogger;

namespace PiWeft.Desktop;

/// 子代理的桌面宿主：每个子代理开一个后台标签，不抢当前标签。
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
		SubagentCatalog Catalog);

	private DesktopSubagentHost? _subagentHost;

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
		return await AttachChildAsync(child, prepared, sessionFile, cancellationToken).ConfigureAwait(false);
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
					?? SubagentCatalog.Discover(parent.Cwd, includeProject: parent.ProjectTrusted));
		}, cancellationToken).ConfigureAwait(false);

	/// 把建好的子会话挂成后台标签。挂不上就释放并删掉空会话文件。
	private async Task<CodingAgentSession?> AttachChildAsync(
		CodingAgentSession child,
		SubagentChildPreparation prepared,
		string? sessionFile,
		CancellationToken cancellationToken,
		bool reopened = false)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var attached = await DispatchAsync(() =>
			{
				if (_sessionHostClosed)
				{
					return false;
				}

				var tab = AttachTab(child);
				tab.UiHost = prepared.UiHost;
				prepared.UiHost.Attach(tab);
				UpdateTabHeaders();
				RefreshSessions(force: true);
				Toasts.Show(this, reopened
					? Strings.SubagentReopened(child.SessionName ?? child.Session.SessionId)
					: Strings.SubagentStarted(child.SessionName ?? child.Session.SessionId));
				AppLog.For<MainWindow>().ZLogInformation(
					$"Subagent session attached: parent={prepared.ParentFile} child={child.Session.SessionId}");
				return true;
			}, CancellationToken.None).ConfigureAwait(false);

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
		return await AttachChildAsync(child, prepared, sessionFile: null, cancellationToken, reopened: true).ConfigureAwait(false);
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
