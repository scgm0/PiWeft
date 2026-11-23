using Aprillz.MewUI;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	/// 关窗收尾与驻留判定在构造时挂上，而不是等 StartupCore：
	/// 项目信任询问可能让 StartupCore 晚几百毫秒，这段时间点关闭也必须走隐藏与收尾。
	private void RegisterWindowLifecycle()
	{
		// 最小化后窗口同样不可见：停掉帧泵，否则后台流式会话会继续以满帧空转。
		this.WindowStateChanged += state => CurrentTab()?.SetWindowVisible(state != Aprillz.MewUI.Controls.WindowState.Minimized);

		// 关闭时记住窗口状态，下次打开原位恢复。
		this.OnClosed(() =>
		{
			if (Application.Current is { } application)
			{
				application.ShutdownRequested -= OnShutdownRequested;
			}
			// 窗口都没了，后台读取与投影不必再跑完。
			_sessionHostClosed = true;
			_sessionAbort.Cancel();
			// 落盘并释放诊断文件句柄；开关状态仍留在偏好里，下次启动继续采。
			Diagnostics.Close();
			_themeWatcher?.Dispose();
			_themeApplyCancellation?.Cancel();
			_themeApplyCancellation?.Dispose();
			_themeApplyCancellation = null;
			// 每个会话面板里的伪终端都要显式收掉，否则窗口关闭后 shell 子进程可能继续挂着。
			foreach (var panel in _sessionPanels.Values) panel.Dispose();
			// 没有标签的子代理：父会话还活着时先收，让 hub 记下 stopped 条目。
			_detachedSweep?.Stop();
			_detachedSweep?.Dispose();
			_detachedSweep = null;
			foreach (var id in _detachedChildren.Keys.ToArray()) DisposeDetachedChild(id);
			foreach (var tab in _tabEntries.OfType<SessionTab>()) tab.Dispose();
			// 侧栏摘要平时在后台线程写，关窗时同步落一次：最后一轮的会话状态不能随进程退出丢掉。
			_catalog.FlushSummaries();
			CapturePanelWidth();
			SaveWindowState();
			// 托盘释放放最后：它失败也不能挡住上面的会话收尾。
			try
			{
				_tray?.Dispose();
				_tray = null;
			}
			catch (Exception ex)
			{
				AppLog.Warning(AppLog.For<MainWindow>(), "Dispose tray", ex);
			}
		});
		this.OnClosing(args =>
		{
			// 先于设置页确认：隐藏后草稿随窗口保留，不需要弹「放弃修改」。
			if (ShouldHideOnClose())
			{
				args.Cancel = true;
				HideToTray();
				return;
			}

			if (_systemShutdownRequested) return;
			if (_settingsView is not { IsDirty: true } settings) return;
			var deferral = args.GetDeferral();
			_ = ConfirmSettingsCloseAsync(settings, args, deferral);
		});
	}
}
