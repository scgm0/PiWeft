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

		// 从二进制目录直接启动时恢复最近项目；Finder/开始菜单启动时 cwd 是文件系统根，
		// 同样退回最近项目或用户主目录。终端传入的目录和自检语义保持不变。
		if (!explicitCwd && !selfCheck)
		{
			var launchCwd = cwd;
			cwd = StartupDirectory.Resolve(
				cwd,
				AppContext.BaseDirectory,
				StartupDirectory.LaunchedByShell,
				DesktopPreference.Load().RecentProjects,
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

			if (!string.Equals(cwd, launchCwd, StringComparison.Ordinal))
			{
				AppLog.For<MainWindow>().ZLogInformation($"Startup directory: {launchCwd} -> {cwd}");
			}
		}

		if (!Directory.Exists(cwd))
		{
			AppLog.For<MainWindow>().ZLogWarning($"Working directory does not exist");
			Console.Error.WriteLine($"piweft-desktop: {Strings.NoSuchDirectory(cwd)}");
			return 2;
		}

		// 启动期迁移（pi 的 migrations.ts）：旧凭据、旧会话落点与 commands/ 目录先搬到当前格式。
		// 项目级迁移只处理已受信项目；启动时还没来得及询问的，会在下次受信启动时迁移。
		try
		{
			var migration = PiWeft.CodingAgent.Migrations.AgentMigrations.Run(cwd);
			foreach (var warning in migration.Warnings)
			{
				AppLog.For<MainWindow>().ZLogWarning($"startup migration: {warning}");
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			AppLog.For<MainWindow>().ZLogWarning($"startup migrations failed: {exception.Message}");
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

		// 诊断要在建窗口之前就开：启动各段（字体、平台宿主、窗口构造、首帧）都值得量。
		// 偏好文件读起来很便宜，读不到就按默认关闭。
		Diagnostics.SetEnabled(DesktopPreference.Load().Diagnostics);
		var startupWatch = System.Diagnostics.Stopwatch.StartNew();

		ApplyTheme();
		AppFonts.Register();
		MewUiLocalization.ApplyChinese();
		Diagnostics.Record("startup.theme-and-fonts", startupWatch);

		// 无窗口自检：检查模型、会话与设置是否装配正确，供 CI 与故障排查使用。
		if (selfCheck)
		{
			return SelfCheck(cwd);
		}

		// 离线模式与代理必须在建 HTTP 客户端之前生效（见 PiEnvironment 的说明）。
		PiWeft.CodingAgent.PiEnvironment.Configure();
		PiWeft.CodingAgent.PiEnvironment.ApplyHttpProxy(
			PiWeft.CodingAgent.Settings.CodingAgentSettings.Load(cwd, includeProject: false).HttpProxy);

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
		// 图片剪贴板是平台原生实现；没有实现的平台保持 null，界面会退回纯文本粘贴。
		using var clipboardImages = ClipboardImages.Create();
		ClipboardImages.Service = clipboardImages;

		var buildWatch = System.Diagnostics.Stopwatch.StartNew();
		var window = new MainWindow(cwd) { ThemeSmoke = themeSmoke, SmokeTab = smokeTab, UiSmoke = uiSmoke };
		Diagnostics.Record("startup.window-build", buildWatch);
		instance.Listen(() => Application.Current.Dispatcher?.BeginInvoke(window.Activate));

		try
		{
			// startup 回调在 UI 线程、dispatcher 就绪之后、窗口显示之前触发：
			// 在这里挂一个 Render 优先级的工作项，它一定排在首帧绘制之后，用来量首屏时延。
			Application.Run(window, () =>
			{
				if (!Diagnostics.Enabled || Application.Current?.Dispatcher is not { } dispatcher)
				{
					return;
				}

				var firstPaint = System.Diagnostics.Stopwatch.StartNew();
				dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
				{
					Diagnostics.Record("startup.first-paint", firstPaint);
					Console.WriteLine($"piweft-desktop: first-paint {firstPaint.Elapsed.TotalMilliseconds:0} ms");
				});
			});
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Desktop runtime", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.WindowStartFailed(ex.ToString())}");
			return 1;
		}
		finally
		{
			PrimarySelectionText.Service = null;
			ClipboardImages.Service = null;
		}

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
		Console.WriteLine($"  {Strings.SelfCheckUiFont}: {AppFonts.UiDisplayName}");
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
		var preference = DesktopPreference.Load();
		// 主题名可能是内建名，也可能是 <agentDir>/themes 下的自定义 pi 主题。
		var resolution = DesktopTheme.Resolve(preference.Theme, Directory.GetCurrentDirectory(), null);
		AppTheme.UseCustomPalette(resolution.Light, resolution.Dark);

		ThemeManager.Default = resolution.Variant;
		// A raw startup color stays fixed across variants. Gray resolves per theme, including System.
		ThemeManager.DefaultAccentColor = null;
		ThemeManager.DefaultAccent = Accent.Gray;
		ThemeManager.DefaultLightSeed = AppTheme.Seed(resolution.Light ?? AppPalette.Light);
		ThemeManager.DefaultDarkSeed = AppTheme.Seed(resolution.Dark ?? AppPalette.Dark);
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
