using PiWeft.Cli;
using PiWeft.CodingAgent;
using ZLogger;

// 入口。环境变量：PI_CODING_AGENT_DIR（agent 目录）与各 provider 的 API key。
// 控制台输出统一为 UTF-8，保证 JSONL 记录与中日韩文本在任何 locale 下都不出错。

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 进程标记（与 pi 的 cli/rpc 入口一致）：子进程与扩展凭它识别「宿主是 pi」。
// SDK 嵌入时由宿主自行决定是否设置，因此只在值缺失时补默认。
Environment.SetEnvironmentVariable("AI_AGENT", Environment.GetEnvironmentVariable("AI_AGENT") ?? "pi");
Environment.SetEnvironmentVariable("PI_CODING_AGENT", Environment.GetEnvironmentVariable("PI_CODING_AGENT") ?? "true");

using var logging = args is ["--version"] or ["-v"] or ["--help"] or ["-h"]
	? null
	: AppLog.Start("cli");

// 子命令（auth/mcp/package）可能跑网络请求或外部进程：未预期的异常要转成
// 「pi: ...」+ 退出码 1，而不是让 .NET 的栈回溯直接砸到用户脸上。
async Task<int?> RunPreParseAsync(string label, Func<Task<int?>> command)
{
	try
	{
		return await command().ConfigureAwait(false);
	}
	catch (Exception exception) when (exception
		is HttpRequestException or System.Net.Sockets.SocketException
		or System.Text.Json.JsonException or InvalidDataException
		or IOException or UnauthorizedAccessException
		or InvalidOperationException or ArgumentException)
	{
		Console.Error.WriteLine($"pi: {label} 失败：{exception.Message}");
		return 1;
	}
}

// 凭据命令最先处理，并且**不经过通用参数解析**：它们有自己的开关
// （--json / --credentials / --no-refresh / --min-expiry），通用解析器会把它们当成未知选项。
// 这也和 pi 一致 —— 官方在解析参数之前先看是不是 auth 命令。
if (args.Length > 0 && args[0] is "login" or "logout" or "auth")
{
	var authExit = await RunPreParseAsync("auth", () =>
		AuthCommands.RunAsync(
			args[0],
			args[1..],
			Console.Out,
			Console.Error,
			Console.In,
			CancellationToken.None));

	if (authExit is { } code)
	{
		return code;
	}
}

// MCP 子命令同样在解析前分发：自带 --local/--url/--command 等开关。
if (args.Length > 0 && args[0] == "mcp")
{
	IReadOnlyList<string> mcpRest = args.Length > 2 ? args[2..] : [];
	var mcpExit = await RunPreParseAsync("mcp", () =>
		McpCommands.RunAsync(
			args.Length > 1 ? args[1] : "list",
			mcpRest,
			Console.Out,
			Console.Error,
			Directory.GetCurrentDirectory(),
			CancellationToken.None));

	if (mcpExit is { } code)
	{
		return code;
	}
}

// 包命令同样在解析前分发：pi install/remove/update 自带 --local/-l/--json 开关。
if (args.Length > 0 && args[0] is "install" or "remove" or "uninstall" or "list" or "update" or "config")
{
	var packageExit = await RunPreParseAsync("package", () =>
		PackageCommands.RunAsync(
			args[0],
			args[1..],
			Console.Out,
			Console.Error,
			Directory.GetCurrentDirectory(),
			CancellationToken.None));

	if (packageExit is { } code)
	{
		return code;
	}
}

var (arguments, parseError) = CliArguments.Parse(args);
if (arguments is null)
{
	Console.Error.WriteLine($"pi: {parseError}");
	Console.Error.WriteLine("Run 'pi --help' for usage.");
	return 2;
}

if (arguments.ShowHelp)
{
	Console.WriteLine(CliArguments.HelpText);
	return 0;
}

if (arguments.ShowVersion)
{
	Console.WriteLine(CliRunner.VersionString);
	return 0;
}

var cwd = Environment.CurrentDirectory;

// 交互模式：默认策略为 ask 且存在受保护项目资源时，先询问是否信任（RPC/print 无法弹框，按不受信处理）。
var interactive = arguments.Mode == CliMode.Text && !arguments.Print &&
	arguments.Prompts.Count == 0 && arguments.FileArgs.Count == 0;
if (interactive && ProjectTrust.HasPendingDecision(cwd, trustOverride: arguments.ProjectTrustOverride))
{
	var trusted = await ProjectTrust
		.PromptAsync(cwd, new ConsoleUiHost(Console.In, Console.Error))
		.ConfigureAwait(false);
	arguments = arguments with { ProjectTrustOverride = trusted };
}

var (environment, error) = CliEnvironment.Create(arguments, cwd);
if (environment is null)
{
	if (arguments.ListModels)
	{
		// 列举不需要可用的模型，但要把自定义端点包含进来。
		foreach (var model in CliEnvironment.ListModels(cwd, arguments.ListModelsSearch))
		{
			Console.WriteLine($"{model.Provider}/{model.Id}");
		}

		return 0;
	}

	AppLog.For<CliRunner>().ZLogWarning($"CLI environment assembly failed");
	Console.Error.WriteLine($"pi: {error}");
	return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
	eventArgs.Cancel = true;
	cancellation.Cancel();
};

var runner = new CliRunner(environment, Console.Out, Console.Error);
var exitCode = await runner.RunAsync(arguments, cwd, Console.In, cancellation.Token).ConfigureAwait(false);
AppLog.For<CliRunner>().ZLogInformation($"CLI stopped: mode={arguments.Mode.ToString()}, exitCode={exitCode}");
return exitCode;
