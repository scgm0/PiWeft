using Aprillz.MewUI;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// <summary>
/// 桌面外壳的入口：注册平台宿主与渲染后端、保证单实例、然后运行主窗口。
/// 用法：<c>piweft-desktop [--cwd &lt;目录&gt;] [--self-check]</c>。
/// </summary>
public static class Program
{
	public static int Main(string[] args)
	{
		using var logging = AppLog.Start("desktop", logToConsole: true);
		try
		{
			var exitCode = Run(args);
			AppLog.For<MainWindow>().ZLogInformation($"Desktop stopped: exitCode={exitCode}");
			return exitCode;
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Desktop startup", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.WindowStartFailed(ex.Message)}");
			return 1;
		}
	}

	private static int Run(string[] args)
	{
		var cwd = Environment.CurrentDirectory;
		var selfCheck = false;
		var explicitCwd = false;
		var themeSmoke = false;
		string? smokeTab = null;
		string? uiSmoke = null;

		for (var i = 0; i < args.Length; i++)
		{
			if (args[i] is "--cwd" or "-C" && i + 1 < args.Length)
			{
				cwd = Path.GetFullPath(args[++i]);
				explicitCwd = true;
			}
			else if (args[i] == "--self-check")
			{
				selfCheck = true;
			}
			else if (args[i] == "--theme-smoke")
			{
				themeSmoke = true;
			}
			else if (args[i] == "--smoke-tab" && i + 1 < args.Length)
			{
				smokeTab = args[++i];
			}
			else if (args[i] == "--ui-smoke" && i + 1 < args.Length)
			{
				uiSmoke = args[++i];
			}
		}

		// 从二进制目录直接启动时恢复最近项目；终端传入的目录和自检语义保持不变。
		if (!explicitCwd && !selfCheck &&
			Path.TrimEndingDirectorySeparator(cwd) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
			DesktopPreference.Load().RecentProjects.FirstOrDefault(Directory.Exists) is { } recent)
		{
			cwd = recent;
		}

		if (!Directory.Exists(cwd))
		{
			AppLog.For<MainWindow>().ZLogWarning($"Working directory does not exist");
			Console.Error.WriteLine($"piweft-desktop: {Strings.NoSuchDirectory(cwd)}");
			return 2;
		}

		// 启动期配置：主题模式、内嵌字体、框架内置文案。三者都必须在创建控件之前就位，
		// 并且对 --self-check 同样生效，便于自检反映真实启动状态。
		// UI 线程上的未处理异常交给 MewUI 的事件，打印完整堆栈便于定位（默认只有一句概览）。
		Application.DispatcherUnhandledException += args =>
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "UI dispatcher", args.Exception);
			Console.Error.WriteLine($"piweft-desktop: UI 线程异常：{args.Exception}");
			args.Handled = true;
		};

		ApplyTheme();
		AppFonts.Register();
		MewUiLocalization.ApplyChinese();

		// 无窗口自检：检查模型、会话与设置是否装配正确，供 CI 与故障排查使用。
		if (selfCheck)
		{
			return SelfCheck(cwd);
		}

		// 每个工作目录一个实例，避免两个窗口同时编辑同一批会话。
		var instanceKey = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cwd)))[..12];

		using var instance = SingleInstance.Acquire(instanceKey);
		if (!instance.IsFirstInstance)
		{
			SingleInstance.SignalRunningInstance(instanceKey);
			AppLog.For<MainWindow>().ZLogInformation($"Existing desktop instance activated");
			Console.Error.WriteLine($"piweft-desktop: {Strings.AnotherInstance}");
			return 0;
		}

		RegisterPlatform();
		ReadingPreferences.Initialize(DesktopPreference.Load());
		using var primarySelection = LinuxPrimarySelection.Create();
		PrimarySelectionText.Service = primarySelection;

		var window = new MainWindow(cwd) { ThemeSmoke = themeSmoke, SmokeTab = smokeTab, UiSmoke = uiSmoke };
		instance.Listen(() => Application.Current.Dispatcher?.BeginInvoke(window.Activate));

		try
		{
			Application.Run(window);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Desktop runtime", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.WindowStartFailed(ex.ToString())}");
			return 1;
		}
		finally { PrimarySelectionText.Service = null; }

		return 0;
	}

	/// <summary>
	/// 不打开窗口地校验运行环境：模型解析、provider 网关、发现的工具与会话目录。
	/// 外壳能够启动时返回 0，否则返回 1。
	/// </summary>
	private static int SelfCheck(string cwd)
	{
		Console.WriteLine(Strings.SelfCheckTitle);
		Console.WriteLine($"  {Strings.SelfCheckCwd}: {cwd}");
		Console.WriteLine($"  {Strings.SelfCheckAgentDir}: {PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir()}");

		var (environment, error) = PiWeft.Cli.CliEnvironment.Create(
			PiWeft.Cli.CliArguments.Parse(["--mode", "text"]).Arguments!,
			cwd);

		if (environment is null)
		{
			Console.Error.WriteLine($"  {Strings.SelfCheckFailed(error ?? string.Empty)}");
			return 1;
		}

		Console.WriteLine($"  {Strings.SelfCheckModel}: {environment.Model.Provider}/{environment.Model.Id} ({environment.Model.Api})");
		Console.WriteLine($"  {Strings.SelfCheckTools}: {string.Join(", ", environment.Tools.Select(static tool => tool.Name))}");
		Console.WriteLine($"  {Strings.SelfCheckUiFont}: {AppFonts.Ui}");
		Console.WriteLine($"  {Strings.SelfCheckMonoFont}: {AppFonts.Mono}");

		// 框架文案是否已切到中文（例如右键菜单），一眼可查。
		Console.WriteLine($"  {Strings.SelfCheckLocalization}: {MewUIStrings.CommandCopy.Value} / {MewUIStrings.CommonCancel.Value}");

		var models = environment.ListModels();
		Console.WriteLine($"  {Strings.SelfCheckModels}: {models.Count}");

		var sessions = new SessionCatalog().List(cwd);
		Console.WriteLine($"  {Strings.SelfCheckSessions}: {sessions.Count}");
		foreach (var session in sessions.Take(5))
		{
			Console.WriteLine($"    {session.Id}  {session.Display}");
		}

		Console.WriteLine($"  {Strings.SelfCheckOk}");
		return 0;
	}

	/// <summary>应用界面主题：按偏好取深色或浅色，配合紫色强调。</summary>
	private static void ApplyTheme()
	{
		ThemeManager.Default = DesktopPreference.Load().Variant;
		// A raw startup color stays fixed across variants. Gray resolves per theme, including System.
		ThemeManager.DefaultAccentColor = null;
		ThemeManager.DefaultAccent = Accent.Gray;
		ThemeManager.DefaultLightSeed = AppTheme.Seed(AppPalette.Light);
		ThemeManager.DefaultDarkSeed = AppTheme.Seed(AppPalette.Dark);
	}

	/// <summary>
	/// 注册平台宿主与渲染后端。这里按**构建时**的目标平台选择（csproj 用 <c>TargetPlatform</c> 与
	/// 条件包引用决定），而不是运行时探测 —— MewUI 的注册 API 是按包提供的，编译进哪个包就用哪个。
	/// </summary>
	private static void RegisterPlatform()
	{
#if MEWUI_WIN32
		Win32Platform.Register();
		Direct2DBackend.Register();
#elif MEWUI_MACOS
		MacOSPlatform.Register();
		MewVGMacOSBackend.Register();
#elif MEWUI_X11
		X11Platform.Register();
		MewVGX11Backend.Register();
#else
#error 没有选择平台：请在 csproj 里设置 TargetPlatform（linux/windows/macos）或传入 -r <rid>。
#endif
	}
}
