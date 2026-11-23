using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	private TrayIcon? _tray;
	// 托盘菜单首项随窗口状态在「显示主窗口」与「隐藏主窗口」之间切换。
	private MenuItem? _trayToggle;
	private Command? _trayShow;
	private Command? _trayHide;
	private bool _hiddenToTray;
	// 托盘退出与系统退出都必须压过「关闭即隐藏」；用户取消退出确认后复位。
	private bool _exitRequested;

	/// 常驻托盘：窗口可见时也保留，关闭到后台后用它唤回。托盘不可用的平台不显示，也不影响窗口。
	private void InitializeTray()
	{
		if (_tray is not null)
		{
			return;
		}

		var show = new Command("pi.tray.show", Strings.TrayShowWindow);
		var hide = new Command("pi.tray.hide", Strings.TrayHideWindow);
		var quit = new Command("pi.tray.quit", Strings.TrayQuit);
		var toggle = new MenuItem(show);
		var menu = new ContextMenu();
		menu.Items.Add(toggle);
		menu.AddSeparator();
		menu.Items.Add(new MenuItem(quit));

		var tray = new TrayIcon()
			.Icon(Icon)
			.ToolTipText(Strings.WindowTitle(_cwd))
			.ContextMenu(menu)
			.Command(show)
			.IsVisible(true);
		tray.Commands.Register(show, Reveal);
		tray.Commands.Register(hide, HideToTray);
		tray.Commands.Register(quit, ExitFromTray);
		// 菜单打开前才读状态：窗口可能在此期间被最小化、恢复或隐藏。
		tray.MenuOpening += UpdateTrayToggle;

		_tray = tray;
		_trayToggle = toggle;
		_trayShow = show;
		_trayHide = hide;
	}

	/// 菜单首项只反映可执行的动作：窗口不在屏幕（隐藏或最小化）时是「显示主窗口」，否则是「隐藏主窗口」。
	private void UpdateTrayToggle()
	{
		if (_trayToggle is not { } toggle || _trayShow is null || _trayHide is null)
		{
			return;
		}

		toggle.Command = TrayBehavior.MenuShowsWindow(_hiddenToTray, WindowState == WindowState.Minimized)
			? _trayShow
			: _trayHide;
	}

	/// <summary>把窗口从托盘或第二个实例唤回前台；窗口已在前台时只激活。</summary>
	public void Reveal()
	{
		// 窗口已关闭（进程退出中）时第二次启动的信号仍可能到达；已关的窗口不能再 Show。
		if (_sessionHostClosed)
		{
			return;
		}

		// 隐藏前最小化过的窗口要先回普通态：X11 的 Hide 不会清掉 WM 的最小化标记。
		if (WindowState == WindowState.Minimized)
		{
			Restore();
		}

		Show();
		X11WindowMapping.Map(Handle);
		Activate();
		_hiddenToTray = false;
	}

	private void HideToTray()
	{
		Hide();
		_hiddenToTray = true;
		AppLog.For<MainWindow>().ZLogInformation($"Window hidden to tray");
	}

	private void ExitFromTray()
	{
		_exitRequested = true;
		// 设置页的未保存确认挂在窗口上：从隐藏状态退出时先亮出窗口，确认框才看得见。
		if (_settingsView is { IsDirty: true })
		{
			Reveal();
		}

		Close();
	}

	/// 窗口关闭时是否驻留。退出请求先短路；托盘可用性每次现查：
	/// Linux 的面板可能晚于应用启动才提供 StatusNotifier host。
	private bool ShouldHideOnClose()
	{
		if (_exitRequested || _systemShutdownRequested)
		{
			return false;
		}

		return TrayBehavior.ShouldHide(DesktopPreference.Load().CloseToTray, TrayIcon.IsSupported);
	}

	/// <summary>关闭→隐藏→唤回的无人值守演练：阶段写进 stdout，外部脚本在对应窗口检查窗口是否可见。</summary>
	internal async Task RunTraySmokeAsync()
	{
		await WaitForSessionOpensAsync().ConfigureAwait(false);
		Dispatch(() =>
		{
			// 隐藏后流式计时器仍在跑：stdout 的 stream t= 行继续增长即是任务没被关窗打断的证据。
			CurrentTab()?.PrepareStreamSmoke();
			UpdateTrayToggle();
			Console.WriteLine($"piweft-desktop: tray supported={TrayIcon.IsSupported} closeToTray={DesktopPreference.Load().CloseToTray}");
			Console.WriteLine($"piweft-desktop: tray-menu={_trayToggle?.Command?.Id}");
			Console.WriteLine("piweft-desktop: tray-phase=shown");
		});
		await Task.Delay(4000).ConfigureAwait(false);

		Dispatch(() =>
		{
			Console.WriteLine("piweft-desktop: tray-phase=hidden");
			Close();
			UpdateTrayToggle();
			Console.WriteLine($"piweft-desktop: tray-menu={_trayToggle?.Command?.Id}");
		});
		await Task.Delay(15000).ConfigureAwait(false);

		Dispatch(() =>
		{
			Console.WriteLine("piweft-desktop: tray-phase=revealed");
			Reveal();
			UpdateTrayToggle();
			Console.WriteLine($"piweft-desktop: tray-menu={_trayToggle?.Command?.Id}");
		});
		await Task.Delay(3000).ConfigureAwait(false);

		Dispatch(() =>
		{
			// 走托盘退出的真实路径：置退出标记、关窗、OnClosed 收尾；不是直接 Shutdown。
			Console.WriteLine("piweft-desktop: tray smoke ok");
			ExitFromTray();
		});
	}
}
