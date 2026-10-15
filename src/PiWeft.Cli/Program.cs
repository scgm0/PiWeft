using PiWeft.Cli;
using PiWeft.CodingAgent;
using ZLogger;

// 入口。环境变量：PI_CODING_AGENT_DIR（agent 目录）与各 provider 的 API key。
// 控制台输出统一为 UTF-8，保证 JSONL 记录与中日韩文本在任何 locale 下都不出错。

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var logging = args is ["--version"] or ["-v"] or ["--help"] or ["-h"]
	? null
	: AppLog.Start("cli");

// 凭据命令最先处理，并且**不经过通用参数解析**：它们有自己的开关
// （--json / --credentials / --no-refresh / --min-expiry），通用解析器会把它们当成未知选项。
// 这也和 pi 一致 —— 官方在解析参数之前先看是不是 auth 命令。
if (args.Length > 0 && args[0] is "login" or "logout" or "auth")
{
	var authExit = await AuthCommands.RunAsync(
		args[0],
		args[1..],
		Console.Out,
		Console.Error,
		CancellationToken.None).ConfigureAwait(false);

	if (authExit is { } code)
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
