using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiWeft.CodingAgent.Session;
using ZLogger;
using ZLogger.Providers;

namespace PiWeft.Cli;

/// <summary>CLI 与桌面端共用的日志；可选标准错误输出，不记录提示词、工具参数或凭据。</summary>
/// <remarks>自定义枚举先转为字符串，避免 ZLogger 在 NativeAOT 下反射创建枚举数组。</remarks>
public sealed class AppLog : IDisposable
{
	private static ILoggerFactory _factory = NullLoggerFactory.Instance;
	private static int _reportedFailure;
	private readonly ILoggerFactory _ownedFactory;
	private bool _disposed;

	private AppLog(ILoggerFactory factory) => _ownedFactory = factory;

	public static string DirectoryPath { get; private set; } = string.Empty;
	public static LogLevel MinimumLevel { get; private set; } = LogLevel.Information;

	public static ILogger For<T>() => Volatile.Read(ref _factory).CreateLogger(typeof(T).FullName ?? typeof(T).Name);

	/// <summary>每个进程一个宿主，在入口用 using 保证退出前排空后台日志队列。</summary>
	public static AppLog Start(string application, bool logToConsole = false)
	{
		DirectoryPath = Path.Combine(SessionPaths.GetAgentDir(), "logs");
		MinimumLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("PI_LOG_LEVEL"), true, out var level) &&
			Enum.IsDefined(level) ? level : LogLevel.Information;
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
				builder.SetMinimumLevel(MinimumLevel);
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
		For<AppLog>().ZLogInformation($"Logging started: application={application}, process={Environment.ProcessId}, runtime={Environment.Version}");
		return new AppLog(factory);
	}

	private static void ConfigureOutput(ZLoggerOptions options)
	{
		options.FullMode = BackgroundBufferFullMode.Drop;
		options.BackgroundBufferCapacity = 1024;
		options.UsePlainTextFormatter(formatter => formatter.SetPrefixFormatter(
			$"{0:utc-longdate} [{1:short}] {2} | ",
			static (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel, info.Category)));
	}

	/// <summary>异常消息可能含 provider 响应和密钥，只记录类型与堆栈，不转储 Message/Data/InnerException。</summary>
	public static void Failure(ILogger logger, string operation, Exception exception) =>
		logger.ZLogError($"{operation}: errorType={exception.GetType().FullName}, stack={exception.StackTrace}");

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
	{
		if (args.ExceptionObject is Exception exception)
		{
			Failure(For<AppLog>(), "Unhandled exception", exception);
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
			Console.Error.WriteLine($"pi: 文件日志不可用（{exception.GetType().Name}），应用将继续运行。");
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
