using Aprillz.MewUI;
using PiWeft.Agent;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using ZLogger;

namespace PiWeft.Desktop;

/// 关联会话的桌面宿主：把 <c>start_session</c> 的工具调用落到一个真正的后台会话与标签页上。
internal sealed class DesktopLinkedSessionHost(MainWindow window) : ILinkedSessionHost
{
	public Task<CodingAgentSession?> CreateChildAsync(
		LinkedSessionStartContext context,
		CancellationToken cancellationToken) =>
		window.CreateLinkedChildAsync(context, cancellationToken);
}

public sealed partial class MainWindow
{
	private sealed record LinkedChildPreparation(
		CliEnvironment Environment,
		IReadOnlyList<AgentTool>? ExtraTools,
		string Cwd,
		string? SessionFile,
		DesktopLinkedSessionHost Host,
		DesktopUiHost UiHost);

	private DesktopLinkedSessionHost? _linkedSessionHost;

	/// 所有会话共用同一个宿主；多线程首次调用用 LazyInitializer 保证只建一个。
	private DesktopLinkedSessionHost LinkedSessionsHost =>
		LazyInitializer.EnsureInitialized(ref _linkedSessionHost, () => new DesktopLinkedSessionHost(this));

	/// 工具线程调进来：建会话与读资源可以慢，放后台；标签页的挂载回 UI 线程，父会话保持选中。
	internal async Task<CodingAgentSession?> CreateLinkedChildAsync(
		LinkedSessionStartContext context,
		CancellationToken cancellationToken)
	{
		var prepared = await DispatchAsync(() =>
		{
			if (_sessionHostClosed || _environment is null)
			{
				return (LinkedChildPreparation?)null;
			}

			// 与手动「新会话」同一条路径：同 cwd、同一份参数与 MCP 工具。
			var manager = _catalog.Create(context.Parent.Cwd);
			var environment = EnvironmentFor(manager.Cwd);
			return new LinkedChildPreparation(
				environment,
				McpToolsFor(manager.Cwd),
				manager.Cwd,
				manager.SessionFile,
				LinkedSessionsHost,
				new DesktopUiHost());
		}, cancellationToken).ConfigureAwait(false);

		if (prepared is null)
		{
			return null;
		}

		var child = await Task.Run(
			() => prepared.Environment.CreateSession(
				_arguments,
				prepared.Cwd,
				extraTools: prepared.ExtraTools,
				recordInitialThinkingLevel: false,
				uiHost: prepared.UiHost,
				preferredModel: context.Parent.Model,
				extraSystemPrompt: context.SystemBrief,
				linkedSessionHost: prepared.Host,
				subagentHost: SubagentSessionsHost,
				linkedParent: context.Parent,
				linkedParentTitle: context.ParentTitle,
				initialPlanMode: context.Parent.PlanModeEnabled),
			cancellationToken).ConfigureAwait(false);

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			child.SetSessionName(context.Title);

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
				// 关系表在 CreateChildAsync 返回后才落盘；下一拍再读一次，侧栏才挂得上父行。
				QueueLinkedLinksRefresh();
				Toasts.Show(this, Strings.LinkedSessionStarted(context.Title));
				AppLog.For<MainWindow>().ZLogInformation(
					$"Linked session started: parent={context.Parent.Session.SessionId} child={child.Session.SessionId} title={context.Title}");
				return true;
			}, CancellationToken.None).ConfigureAwait(false);

			if (!attached)
			{
				child.Dispose();
				DiscardLinkedChild(prepared.SessionFile);
				return null;
			}
		}
		catch
		{
			child.Dispose();
			DiscardLinkedChild(prepared.SessionFile);
			throw;
		}

		return child;
	}

	/// 建出来但没挂上标签的子会话（取消/关窗）：把空会话文件清掉，别在侧栏留一条没人管的会话。
	private void DiscardLinkedChild(string? sessionFile)
	{
		if (sessionFile is not { Length: > 0 })
		{
			return;
		}

		_ = DispatchAsync(() =>
		{
			try
			{
				_catalog.Delete(sessionFile);
				RefreshSessions(force: true);
			}
			catch (Exception ex)
			{
				AppLog.Failure(AppLog.For<MainWindow>(), "Discard linked child session", ex);
			}

			return true;
		}, CancellationToken.None);
	}

	/// 关联会话的关系表在 <c>CreateChildAsync</c> 返回后才落盘；
	/// 立刻扫描可能读到旧表，侧栏会把子会话留在顶层。排到下一拍再读一次。
	private void QueueLinkedLinksRefresh()
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null)
		{
			return;
		}

		dispatcher.BeginInvoke(() => _ = RefreshLinkedLinksAsync());
	}

	/// 只重读关系表并重建侧栏，不再扫一遍会话目录。
	/// 同时开多个关联会话时共用一次读盘：后来的请求排在这一次后面。
	private Task RefreshLinkedLinksAsync()
	{
		if (_linkedLinksRefresh is { IsCompleted: false } running)
		{
			// 这一拍读到的可能是旧表：记一笔，读完再补一次，别把后来的关系吞掉。
			_linkedLinksDirty = true;
			return running;
		}

		var task = RefreshLinkedLinksOnceAsync();
		_linkedLinksRefresh = task;
		return task;
	}

	private async Task RefreshLinkedLinksOnceAsync()
	{
		try
		{
			var links = await Task.Run(() => LinkedSessionStore.Load().Links).ConfigureAwait(true);
			if (_sessionHostClosed)
			{
				return;
			}

			_linkedLinks = links;
			if (_sessionsLoaded)
			{
				FilterSessions();
			}
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Reload linked sessions", ex);
		}
		finally
		{
			_linkedLinksRefresh = null;
			if (_linkedLinksDirty)
			{
				_linkedLinksDirty = false;
				_ = RefreshLinkedLinksAsync();
			}
		}
	}

	/// 把 UI 线程的工作投递出去并等它完成；调用方取消或窗口关闭时不会无限等。
	private static Task<T> DispatchAsync<T>(Func<T> action, CancellationToken cancellationToken)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(action());
		}

		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(
			static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(),
			completion);
		dispatcher.BeginInvoke(() =>
		{
			// 调用方已经取消：不要在这里继续建标签/挂界面，结果也没人接。
			if (completion.Task.IsCompleted)
			{
				return;
			}

			try
			{
				completion.TrySetResult(action());
			}
			catch (Exception ex)
			{
				completion.TrySetException(ex);
			}
		});
		return completion.Task;
	}
}
