using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiWeft.CodingAgent.Session;
using ZLogger;
using ZLogger.Providers;

namespace PiWeft.Cli;

/// 日志消息里的枚举参数一律先 <c>ToString()</c>：ZLogger 用反射建枚举数组，
/// NativeAOT 裁掉该数组类型后类型初始化抛异常，整条记录丢失，前缀还会粘到下一行。
public sealed class AppLog : IDisposable
{
	private static ILoggerFactory _factory = NullLoggerFactory.Instance;
	private static int _reportedFailure;
	private static int _minimumLevel = (int)LogLevel.Information;
	private static LogRedactor _redactor = LogRedactor.ForCurrentDirectory();
	private readonly ILoggerFactory _ownedFactory;
	private bool _disposed;

	private AppLog(ILoggerFactory factory) => _ownedFactory = factory;

	public static string DirectoryPath { get; private set; } = string.Empty;

	/// 启动时传入的应用名（<c>cli</c> / <c>desktop</c>），进文件名与崩溃记录。
	public static string Application { get; private set; } = "pi";

	/// 当前生效的最低级别。运行期可改（<see cref="SetMinimumLevel"/>），不必重启或改环境变量。
	public static LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

	/// 写入与导出共用的脱敏器。
	public static LogRedactor Redactor => Volatile.Read(ref _redactor);

	public static ILogger For<T>() => Volatile.Read(ref _factory).CreateLogger(typeof(T).FullName ?? typeof(T).Name);

	/// 启停文件日志。
	/// <param name="application">应用名，进文件名。</param>
	/// <param name="logToConsole">同时写标准错误。</param>
	/// <param name="minimumLevel">最低级别。环境变量 <c>PI_LOG_LEVEL</c> 优先于它，都没有时用 Information。</param>
	public static AppLog Start(string application, bool logToConsole = false, LogLevel? minimumLevel = null)
	{
		DirectoryPath = Path.Combine(SessionPaths.GetAgentDir(), "logs");
		Application = application;
		// 环境变量优先：临时排障时改一行环境变量比重启设置页快。
		Volatile.Write(ref _minimumLevel, (int)(LevelFromEnvironment() ?? minimumLevel ?? LogLevel.Information));
		Volatile.Write(ref _redactor, LogRedactor.ForCurrentDirectory());
		var fileLoggingAvailable = false;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				Directory.CreateDirectory(DirectoryPath);
			}
			else
			{
				Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
			fileLoggingAvailable = true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			ReportFailure(ex);
		}

		ILoggerFactory factory = NullLoggerFactory.Instance;
		try
		{
			var directory = DirectoryPath;
			var processId = Environment.ProcessId;
			factory = LoggerFactory.Create(builder =>
			{
				// 真正生效的过滤在下面这条规则里：它每次记录时读一遍 MinimumLevel，
				// 所以设置页改级别立刻生效，不用重建工厂（重建会把文件切成两份）。
				builder.SetMinimumLevel(LogLevel.Trace);
				builder.AddFilter(static (_, level) => level >= MinimumLevel);
				if (logToConsole)
				{
					builder.AddZLoggerConsole(options =>
					{
						options.LogToStandardErrorThreshold = LogLevel.Trace;
						ConfigureOutput(options);
					});
				}

				if (!fileLoggingAvailable) return;
				builder.AddZLoggerRollingFile(options =>
				{
					options.FilePathSelector = (timestamp, sequence) =>
					{
						Prune(directory);
						return Path.Combine(directory, $"piweft-{application}-{processId}-{timestamp:yyyyMMdd}-{sequence:000}.log");
					};
					options.RollingInterval = RollingInterval.Day;
					options.RollingSizeKB = 4096;
					options.InternalErrorLogger = ReportFailure;
					ConfigureOutput(options);
				});
			});
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
		{
			ReportFailure(ex);
		}

		Volatile.Write(ref _factory, factory);
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		For<AppLog>().ZLogInformation($"Logging started: application={application}, process={Environment.ProcessId}, runtime={Environment.Version}, level={MinimumLevel.ToString()}");
		return new AppLog(factory);
	}

	/// 改最低级别。已打开的记录器会立刻按新值过滤。
	public static void SetMinimumLevel(LogLevel level) => Volatile.Write(ref _minimumLevel, (int)level);

	/// 记录工作目录：脱敏要按当前会话替换路径，桌面启动后才确定 cwd。
	public static void ConfigureRedaction(string? cwd, string? agentDir = null) =>
		Volatile.Write(ref _redactor, new LogRedactor(cwd, agentDir ?? SessionPaths.GetAgentDir()));

	private static LogLevel? LevelFromEnvironment() =>
		Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("PI_LOG_LEVEL"), true, out var level) &&
		Enum.IsDefined(level) ? level : null;

	private static void ConfigureOutput(ZLoggerOptions options)
	{
		options.FullMode = BackgroundBufferFullMode.Drop;
		options.BackgroundBufferCapacity = 1024;
		options.UsePlainTextFormatter(formatter => formatter.SetPrefixFormatter(
			$"{0:utc-longdate} [{1:short}] {2} | ",
			static (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel, info.Category)));
	}

	/// 记一条失败：异常类型、脱敏后的消息、内部异常链与栈。
	/// <remarks>
	/// 以前只写类型与栈，消息全丢：文件里留下 <c>IOException</c> 却看不出是磁盘满还是文件被占用。
	/// 现在消息先过 <see cref="LogRedactor"/>，凭据换成占位符，路径换成 <c>~</c> / <c>&lt;workspace&gt;</c>。
	/// </remarks>
	public static void Failure(ILogger logger, string operation, Exception exception) =>
		logger.ZLogError($"{operation}: {Describe(exception)}");

	/// 记一条不致命的失败：脱敏口径与 <see cref="Failure"/> 相同，级别是警告。
	public static void Warning(ILogger logger, string operation, Exception exception) =>
		logger.ZLogWarning($"{operation}: {Describe(exception)}");

	/// 异常链：<c>Type: message &lt;- InnerType: message</c>，最多八层。
	/// 不脱敏：调用方决定用哪个 <see cref="LogRedactor"/>，或把消息与栈分开存。
	public static string Chain(Exception exception)
	{
		var builder = new StringBuilder(256);
		var current = exception;
		for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
		{
			if (builder.Length > 0)
			{
				builder.Append(" <- ");
			}

			builder.Append(current.GetType().FullName).Append(": ").Append(current.Message);
		}

		return builder.ToString();
	}

	/// 记日志用的描述：异常链 + 栈，脱敏后压成一行。
	public static string Describe(Exception exception) =>
		Redactor.Line(exception.StackTrace is { Length: > 0 } stack
			? $"{Chain(exception)}, stack={stack}"
			: Chain(exception));

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
	{
		if (args.ExceptionObject is Exception exception)
		{
			Failure(For<AppLog>(), "Unhandled exception", exception);
			CrashLog.Record(CrashKind.UnhandledException, exception, cwd: Environment.CurrentDirectory);
		}

		if (args.IsTerminating)
		{
			// 未处理异常终止进程时 using 不保证执行，必须在这里排空已入队的错误记录。
			try
			{
				Volatile.Read(ref _factory).Dispose();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
			{
				ReportFailure(ex);
			}
		}
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
		Failure(For<AppLog>(), "Unobserved task exception", args.Exception);

	private static void ReportFailure(Exception exception)
	{
		if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
		{
			// 这条也覆盖单条记录格式化失败，此时文件日志仍在工作，不能报成不可用。
			// 只报类型名看不出根因（TypeInitializationException 的 InnerException 才是原因）。
			Console.Error.WriteLine($"pi: 日志写入失败（{Redactor.Line(Chain(exception))}），应用将继续运行。");
		}
	}

	private static void Prune(string directory)
	{
		try
		{
			var cutoff = DateTime.UtcNow.AddDays(-14);
			var files = new DirectoryInfo(directory).EnumerateFiles("*.log")
				.Where(static file => file.Name.StartsWith("piweft-", StringComparison.Ordinal) ||
					file.Name.StartsWith("pi-", StringComparison.Ordinal))
				.OrderByDescending(static file => file.LastWriteTimeUtc).ToArray();
			for (var index = 0; index < files.Length; index++)
			{
				if (index < 31 && files[index].LastWriteTimeUtc >= cutoff)
				{
					continue;
				}

				try
				{
					// 跳过仍由其它窗口/CLI 写入的文件，不删除正在使用的日志。
					using (new FileStream(files[index].FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
					files[index].Delete();
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			ReportFailure(ex);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
		TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
		Interlocked.CompareExchange(ref _factory, NullLoggerFactory.Instance, _ownedFactory);
		try
		{
			_ownedFactory.Dispose();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
		{
			ReportFailure(ex);
		}
	}
}
