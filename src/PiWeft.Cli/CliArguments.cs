namespace PiWeft.Cli;

/// <summary>一次运行的输出模式。</summary>
public enum CliMode
{
	/// <summary>基于行的交互会话（配合 <c>--print</c> 时是单次输出）。</summary>
	Text,

	/// <summary>会话头部加一条 JSONL 事件流。</summary>
	Json,

	/// <summary>长驻的 JSONL 命令/响应/事件协议。</summary>
	Rpc,
}

/// <summary>解析后的命令行，对应本端口实现的那部分 pi 参数。</summary>
public sealed record CliArguments
{
	public CliMode Mode { get; init; } = CliMode.Text;

	public bool Print { get; init; }

	public string? Provider { get; init; }

	public string? Model { get; init; }

	public string? ApiKey { get; init; }

	public string? ThinkingLevel { get; init; }

	public string? SystemPrompt { get; init; }

	public List<string> AppendSystemPrompt { get; init; } = [];

	public bool Continue { get; init; }

	public bool Resume { get; init; }

	/// <summary>会话文件路径或部分 UUID。</summary>
	public string? Session { get; init; }

	public string? SessionId { get; init; }

	/// <summary>要派生为新会话的会话文件或部分 UUID。</summary>
	public string? Fork { get; init; }

	public string? SessionDir { get; init; }

	public bool NoSession { get; init; }

	public string? SessionName { get; init; }

	public List<string> Prompts { get; init; } = [];

	/// <summary><c>@file</c> 附件参数（已去掉 <c>@</c> 前缀），对应 pi 的 <c>fileArgs</c>。</summary>
	public List<string> FileArgs { get; init; } = [];

	/// <summary>工具白名单。以 <c>+</c>/<c>-</c> 开头的条目在默认集上增减，其余则整体替换默认集。</summary>
	public List<string> Tools { get; init; } = [];

	public List<string> ExcludeTools { get; init; } = [];

	/// <summary><c>--models</c>：Ctrl+P 轮换使用的模型模式列表（支持 glob）。</summary>
	public List<string> Models { get; init; } = [];

	/// <summary><c>--export</c>：把会话文件导出为 HTML 后退出。</summary>
	public string? Export { get; init; }

	/// <summary><c>--approve</c> / <c>--no-approve</c>：本章进程的项目信任覆盖。</summary>
	public bool? ProjectTrustOverride { get; init; }

	public bool NoBuiltinTools { get; init; }

	public bool NoTools { get; init; }

	public bool NoContextFiles { get; init; }

	public bool NoMcp { get; init; }

	public bool NoSkills { get; init; }

	public bool NoPromptTemplates { get; init; }

	public bool NoThemes { get; init; }

	/// <summary><c>--skill</c> 显式技能路径（文件或目录），可重复。</summary>
	public List<string> Skills { get; init; } = [];

	/// <summary><c>--prompt-template</c> 显式模板路径，可重复。</summary>
	public List<string> PromptTemplates { get; init; } = [];

	/// <summary><c>--theme</c> 显式主题路径，可重复。</summary>
	public List<string> Themes { get; init; } = [];

	public bool ListModels { get; init; }

	public string? ListModelsSearch { get; init; }

	public bool ShowHelp { get; init; }

	public bool ShowVersion { get; init; }

	public bool Verbose { get; init; }

	public bool Offline { get; init; }

	/// <summary>
	/// 为兼容而接受但本端口不实现的选项名（<c>-e</c>、<c>--theme</c> 等）。
	/// 调用方据此向用户提示「该开关不会生效」，避免静默降级。
	/// </summary>
	public List<string> AcceptedCompatFlags { get; init; } = [];

	/// <summary>解析 argv。以错误信息代替异常返回，便于调用方打印用法。</summary>
	public static (CliArguments? Arguments, string? Error) Parse(IReadOnlyList<string> argv)
	{
		var result = new CliArguments();
		var index = 0;
		var onlyPrompts = false;

		string? NextValue(string flag)
		{
			if (index + 1 >= argv.Count)
			{
				throw new CliParseException($"Missing value for {flag}");
			}

			index++;
			return argv[index];
		}

		try
		{
			for (; index < argv.Count; index++)
			{
				var argument = argv[index];
				if (onlyPrompts || !argument.StartsWith('-') || argument == "-")
				{
					// 单独的 @file 是附件参数，不是提示词；<c>@</c> 自身仍当作普通输入。
					if (argument.Length > 1 && argument[0] == '@')
					{
						result.FileArgs.Add(argument[1..]);
					}
					else
					{
						result.Prompts.Add(argument);
					}

					continue;
				}

				if (argument == "--")
				{
					onlyPrompts = true;
					continue;
				}

				// 支持 --flag=value 写法。
				string flag = argument;
				string? inlineValue = null;
				var equals = argument.IndexOf('=');
				if (equals > 2)
				{
					flag = argument[..equals];
					inlineValue = argument[(equals + 1)..];
				}

				string Value() => inlineValue ?? NextValue(flag) ?? string.Empty;

				switch (flag)
				{
					case "-h" or "--help":
						result = result with { ShowHelp = true };
						break;
					case "-v" or "--version":
						result = result with { ShowVersion = true };
						break;
					case "--provider":
						result = result with { Provider = Value() };
						break;
					case "--model":
						result = result with { Model = Value() };
						break;
					case "--api-key":
						result = result with { ApiKey = Value() };
						break;
					case "--system-prompt":
						result = result with { SystemPrompt = Value() };
						break;
					case "--append-system-prompt":
						result = result with { AppendSystemPrompt = [.. result.AppendSystemPrompt, Value()] };
						break;
					case "--mode":
					{
						var mode = Value();
						if (mode is not ("text" or "json" or "rpc"))
						{
							return (null, $"Unknown mode '{mode}'. Expected text, json or rpc.");
						}

						result = result with
						{
							Mode = mode switch { "json" => CliMode.Json, "rpc" => CliMode.Rpc, _ => CliMode.Text },
						};
						break;
					}

					case "-p" or "--print":
						result = result with { Print = true };
						break;
					case "-c" or "--continue":
						result = result with { Continue = true };
						break;
					case "-r" or "--resume":
						result = result with { Resume = true };
						break;
					case "--session":
						result = result with { Session = Value(), Resume = true };
						break;
					case "--session-id":
						result = result with { SessionId = Value() };
						break;
					case "--fork":
						result = result with { Fork = Value() };
						break;
					case "--session-dir":
						result = result with { SessionDir = Value() };
						break;
					case "--no-session":
						result = result with { NoSession = true };
						break;
					case "-n" or "--name":
						result = result with { SessionName = Value() };
						break;
					case "--thinking":
						result = result with { ThinkingLevel = Value() };
						break;
					case "-t" or "--tools":
						result = result with { Tools = [.. result.Tools, .. SplitList(Value())] };
						break;
					case "-xt" or "--exclude-tools":
						result = result with { ExcludeTools = [.. result.ExcludeTools, .. SplitList(Value())] };
						break;
					case "-nt" or "--no-tools":
						result = result with { NoTools = true };
						break;
					case "-nbt" or "--no-builtin-tools":
						result = result with { NoBuiltinTools = true };
						break;
					case "-nc" or "--no-context-files":
						result = result with { NoContextFiles = true };
						break;
					case "-ns" or "--no-skills":
						result = result with { NoSkills = true };
						break;
					case "-np" or "--no-prompt-templates":
						result = result with { NoPromptTemplates = true };
						break;
					case "--list-models":
					{
						// 同时支持 `--list-models gpt` 与 `--list-models=gpt`；只认后者的下一个参数会丢掉内联值。
						var search = inlineValue
							?? (index + 1 < argv.Count && !argv[index + 1].StartsWith('-') ? NextValue(flag) : null);
						result = result with { ListModels = true, ListModelsSearch = search };
						break;
					}

					case "--models":
						result = result with { Models = [.. result.Models, .. SplitList(Value())] };
						break;

					case "--export":
						result = result with { Export = Value() };
						break;

					case "-a" or "--approve":
						result = result with { ProjectTrustOverride = true };
						break;

					case "-na" or "--no-approve":
						result = result with { ProjectTrustOverride = false };
						break;

					case "--verbose":
						result = result with { Verbose = true };
						break;
					case "--no-mcp":
						result = result with { NoMcp = true };
						break;
					case "--offline":
						result = result with { Offline = true };
						break;

					case "--skill":
						result = result with { Skills = [.. result.Skills, Value()] };
						break;
					case "--prompt-template":
						result = result with { PromptTemplates = [.. result.PromptTemplates, Value()] };
						break;
					case "--theme":
						result = result with { Themes = [.. result.Themes, Value()] };
						break;
					case "--no-themes":
						result = result with { NoThemes = true };
						break;

					// 为兼容性而接受；对应功能不在本端口范围内（保留参数以免破坏 PiDeck 启动）。
					case "-e" or "--extension" or "-ne" or "--no-extensions"
						or "--use-theme"
						or "--tui-mode" or "--autocomplete-max-visible":
						result.AcceptedCompatFlags.Add(flag);
						if (inlineValue is null && flag is "-e" or "--extension" or "--skill" or "--prompt-template"
							or "--theme" or "--use-theme" or "--tui-mode" or "--autocomplete-max-visible")
						{
							NextValue(flag); // 消费掉该值，避免它被当成提示词
						}

						break;

					default:
						return (null, $"Unknown option '{flag}'. Run with --help.");
				}
			}
		}
		catch (CliParseException ex)
		{
			return (null, ex.Message);
		}

		return (SplitModelThinkingSuffix(result), null);
	}

	/// <summary>
	/// pi 允许在 <c>--model</c> 里用 <c>provider/id:level</c> 顺带指定思考级别
	///（<c>off</c>、<c>minimal</c>…<c>max</c>）。显式 <c>--thinking</c> 优先于后缀。
	/// 解析在全部参数处理完后进行，因此 <c>--thinking</c> 与 <c>--model</c> 的先后顺序不影响结果。
	/// </summary>
	private static CliArguments SplitModelThinkingSuffix(CliArguments arguments)
	{
		if (arguments.Model is not { Length: > 0 } model)
		{
			return arguments;
		}

		var colon = model.LastIndexOf(':');
		if (colon <= 0 || colon == model.Length - 1)
		{
			return arguments;
		}

		var level = model[(colon + 1)..];
		if (!PiWeft.Ai.ThinkingLevels.Model.Contains(level, StringComparer.Ordinal))
		{
			return arguments;
		}

		return arguments with
		{
			Model = model[..colon],
			ThinkingLevel = arguments.ThinkingLevel ?? level,
		};
	}

	public static string HelpText => """
		pi - AI coding assistant with read, bash, edit, write tools

		Usage:
		  pi [options] [--] [@files...] [messages...]

		Commands:
		  login [provider]              Sign in to an OAuth provider (default: openai-codex)
		  logout [provider]             Remove stored OAuth credentials
		  auth check                    Show stored OAuth credentials
		  auth print-bearer-token       Print a valid access token (refreshing when needed)
		  install/list/remove/update     Manage pi packages (npm, git, or local paths)
		  mcp list|add|remove|login|logout  Manage MCP servers and their OAuth tokens

		Options:
		  --provider <name>              Provider to search for --model (requires --model)
		  --model <pattern>              Model pattern or ID (supports "provider/id")
		  --api-key <key>                API key (defaults to env vars)
		  --system-prompt <text>         System prompt override
		  --append-system-prompt <text>  Append text to the system prompt (repeatable)
		  --mode <mode>                  Output mode: text (default), json, or rpc
		  --print, -p                    Non-interactive mode: process prompt and exit
		  --continue, -c                 Continue previous session
		  --resume, -r                   Select a session to resume
		  --session <path|id>            Use specific session file or partial UUID
		  --session-id <id>              Use exact project session ID, creating it if missing
		  --fork <path|id>               Fork a session file or partial UUID into a new session
		  --session-dir <dir>            Directory for session storage and lookup
		  --no-session                   Don't save session (ephemeral)
		  --name, -n <name>              Set session display name
		  --thinking <level>             Set thinking level: off, minimal, low, medium, high, xhigh, max
		  --tools, -t <tools>            Comma-separated allowlist of tool names
		  --exclude-tools, -xt <tools>   Comma-separated denylist of tool names
		  --no-tools, -nt                Disable all tools
		  --no-builtin-tools, -nbt       Disable built-in tools
		  --no-context-files, -nc        Disable AGENTS.md and CLAUDE.md discovery
		  -a, --approve                  Trust the current project for this run
		  -na, --no-approve              Do not trust the current project for this run
		  --no-skills, -ns               Disable skills discovery
		  --no-prompt-templates, -np     Disable prompt template discovery
		  --skill <path>                 Load an explicit skill file or directory (repeatable)
		  --prompt-template <path>       Load an explicit prompt template (repeatable)
		  --theme <path>                 Load an explicit theme file or directory (repeatable)
		  --no-themes                    Disable theme discovery
		  --models <patterns>            Comma-separated model patterns for Ctrl+P cycling
		  --export <session> [output]    Export a session file to HTML and exit
		  --list-models [search]         List available models
		  --verbose                      Force verbose startup
		  --offline                      Accepted for compatibility (no effect in this port)
		  --version, -v                  Print the version
		  --help, -h                     Show this help

		Accepted for pi/PiDeck compatibility but not implemented in this port:
		  -e/--extension, -ne/--no-extensions, --use-theme, --tui-mode.
		These flags are consumed so PiDeck can launch the binary; they do not enable extensions
		or a full-screen TUI. Custom pi themes are read by the desktop app from the themes
		directory. MCP is supported separately (see --no-mcp).
		""";

	private static IEnumerable<string> SplitList(string value) =>
		value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private sealed class CliParseException(string message) : Exception(message);
}
