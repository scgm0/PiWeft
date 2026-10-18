using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Stats;
using PiWeft.Mcp;
using ZLogger;

namespace PiWeft.Cli;

/// <summary>按请求的模式执行一次 CLI 调用。</summary>
public sealed partial class CliRunner(CliEnvironment environment, TextWriter output, TextWriter error)
{
	private readonly Lock _outputGate = new();
	private readonly List<Task> _backgroundRuns = [];
	private IReadOnlyList<AgentTool> _extraTools = [];

	private IReadOnlyList<AgentTool> _resourceTools = [];

	private McpConnection? _mcp;
	private CodingAgentSession? _session;

	/// <summary>当前运行的参数，MCP 服务器变化时需要它来重新组装工具。</summary>
	private CliArguments? _arguments;
	private string _cwd = string.Empty;
	private CancellationTokenSource? _bashCancellation;

	/// <summary>按参数分派到对应模式，返回进程退出码。</summary>
	public async Task<int> RunAsync(CliArguments arguments, string cwd, TextReader input, CancellationToken cancellationToken)
	{
		if (arguments.ShowHelp)
		{
			output.WriteLine(CliArguments.HelpText);
			return 0;
		}

		if (arguments.ShowVersion)
		{
			output.WriteLine(VersionString);
			return 0;
		}

		if (arguments.ListModels)
		{
			foreach (var model in CliEnvironment.ListModels(cwd, arguments.ListModelsSearch))
			{
				output.WriteLine($"{model.Provider}/{model.Id}");
			}

			return 0;
		}

		_arguments = arguments;
		AppLog.For<CliRunner>().ZLogInformation($"CLI run: mode={arguments.Mode.ToString()}");

		// 兼容参数被静默吞掉会让用户以为功能生效了（例如 -e 期望扩展加载）；显式提示一次。
		foreach (var flag in arguments.AcceptedCompatFlags)
		{
			error.WriteLine($"pi: warning: {flag} is accepted for pi/PiDeck compatibility but not implemented in this port (see --help).");
		}

		if (arguments.Mode == CliMode.Rpc && arguments.FileArgs.Count > 0)
		{
			// pi 在 RPC 模式下拒绝 @file：stdin 已被协议占用，无法再读附件。
			error.WriteLine("pi: @file arguments are not supported in RPC mode");
			return 1;
		}

		if (arguments.Export is { Length: > 0 } exportSource)
		{
			try
			{
				return ExportSession(exportSource, arguments, cwd);
			}
			catch (Exception ex)
			{
				AppLog.Failure(AppLog.For<CliRunner>(), "Session export", ex);
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

			try
			{
				if (!arguments.NoMcp)
				{
					// 先连接 MCP 服务器，使其工具包含在提示词的工具声明里。
					await ConnectMcpAsync(cwd, environment.ProjectTrusted, cancellationToken).ConfigureAwait(false);
				}

			return arguments.Mode switch
			{
				CliMode.Rpc => await RunRpcAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false),
				CliMode.Json => await RunJsonAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false),
				_ => arguments.Print || arguments.Prompts.Count > 0
					? await RunPrintAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false)
					: await RunInteractiveAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false),
			};
		}
		catch (OperationCanceledException)
		{
			return 130;
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<CliRunner>(), "CLI run", ex);
			error.WriteLine($"pi: {ex.Message}");
			return 1;
		}
		finally
		{
			_session = null;

			if (_mcp is not null)
			{
				await _mcp.DisposeAsync().ConfigureAwait(false);
				_mcp = null;
			}
		}
	}

	/// <summary>连接 MCP 服务器并把工具装配进当前会话；<c>reconnect_mcp</c> 复用同一路径。</summary>
	private async Task ConnectMcpAsync(string cwd, bool includeProject, CancellationToken cancellationToken)
	{
		_mcp = await McpServers.ConnectConfiguredAsync(
			cwd,
			onError: static message => Console.Error.WriteLine($"pi: mcp {message}"),
			includeProject: includeProject).ConfigureAwait(false);

		// 资源工具会指向所有声明了 resources 能力的已连接服务器。
		_resourceTools = McpResourceTools.Create(() => _mcp.ResourceServers);
		_extraTools = [.. _mcp.Tools, .. _resourceTools];

		// 服务器在运行期间增删工具时，会刷新当前会话的工具集。
		_mcp.ToolsChanged += connection =>
		{
			var session = _session;
			if (session is null)
			{
				return;
			}

			try
			{
				session.SetTools(environment.ComposeTools(_arguments!, [.. connection.Tools, .. _resourceTools]));
			}
			catch (Exception ex)
			{
				AppLog.Failure(AppLog.For<CliRunner>(), "MCP tool refresh", ex);
				Console.Error.WriteLine($"pi: mcp tool refresh failed: {ex.Message}");
			}
		};
	}

	/// <summary>MCP 连接状态的 RPC 载荷。</summary>
	private JsonObject McpStatus()
	{
		var servers = new List<JsonNode?>(_mcp?.Servers.Count ?? 0);
		foreach (var server in _mcp?.Servers ?? [])
		{
			servers.Add(new JsonObject
			{
				["name"] = server.Name,
				["connected"] = server.Connected,
				["toolCount"] = server.ToolCount,
				["error"] = server.Error,
			});
		}

		var errors = new List<JsonNode?>(_mcp?.Errors.Count ?? 0);
		foreach (var message in _mcp?.Errors ?? [])
		{
			errors.Add(JsonValue.Create(message));
		}

		return new JsonObject
		{
			["connected"] = _mcp is not null,
			["servers"] = new JsonArray([.. servers]),
			["errors"] = new JsonArray([.. errors]),
			["toolCount"] = _extraTools.Count,
		};
	}

	/// <summary>跨会话统计的 RPC 载荷：合计 + 每日热力图 + 模型/项目排行。</summary>
	private static JsonObject UsageStatsJson(UsageStatsReport report, int weeks)
	{
		var days = new List<JsonNode?>(report.Days.Count);
		foreach (var day in report.Days)
		{
			days.Add(new JsonObject { ["date"] = day.Date, ["totals"] = TotalsJson(day.Totals) });
		}

		var models = new List<JsonNode?>(report.Models.Count);
		foreach (var model in report.Models)
		{
			models.Add(new JsonObject
			{
				["provider"] = model.Provider,
				["model"] = model.Model,
				["totals"] = TotalsJson(model.Totals),
			});
		}

		var projects = new List<JsonNode?>(report.Projects.Count);
		foreach (var project in report.Projects)
		{
			projects.Add(new JsonObject { ["directory"] = project.Directory, ["totals"] = TotalsJson(project.Totals) });
		}

		var heatmap = new List<JsonNode?>();
		foreach (var cell in UsageStats.Heatmap(report, weeks))
		{
			heatmap.Add(new JsonObject { ["date"] = cell.Date, ["tokens"] = cell.Tokens, ["level"] = cell.Level });
		}

		return new JsonObject
		{
			["totals"] = TotalsJson(report.Totals),
			["days"] = new JsonArray([.. days]),
			["models"] = new JsonArray([.. models]),
			["projects"] = new JsonArray([.. projects]),
			["heatmap"] = new JsonArray([.. heatmap]),
		};
	}

	private static JsonObject TotalsJson(StatsTotals totals) => new()
	{
		["input"] = totals.Input,
		["output"] = totals.Output,
		["cacheRead"] = totals.CacheRead,
		["cacheWrite"] = totals.CacheWrite,
		["tokens"] = totals.Tokens,
		["messages"] = totals.Messages,
		["cost"] = totals.Cost,
	};

	/// <summary><c>pi --export &lt;session&gt; [output]</c>：把会话渲染成自包含 HTML 后退出。</summary>
	private int ExportSession(string source, CliArguments arguments, string cwd)
	{
		var sessionPath = CliEnvironment.ResolveSessionPath(source, cwd, arguments.SessionDir)
			?? throw new InvalidOperationException($"Session not found: {source}");

		// 导出是只读操作：不记录初始思考级别，以免改动会话文件。
		using var session = environment.CreateSession(
			arguments,
			cwd,
			session: SessionManager.Open(sessionPath, arguments.SessionDir),
			recordInitialThinkingLevel: false);

		var outputPath = ExportHtml(session, arguments.Prompts.Count > 0 ? arguments.Prompts[0] : null);
		output.WriteLine($"Exported to: {outputPath}");
		return 0;
	}

	/// <summary><c>pi --version</c> 报告的 RPC 契约版本，供客户端判断特性支持；不是自身构建号。</summary>
	public const string RpcContractVersion = "1.3.0";

	public static string VersionString => RpcContractVersion;

	// ----------------------------------------------------------------- 单次输出

	/// <summary>单次模式：执行提示并打印最终的助手文本。</summary>
	private async Task<int> RunPrintAsync(CliArguments arguments, string cwd, TextReader input, CancellationToken cancellationToken)
	{
		using var session = environment.CreateSession(arguments, cwd, extraTools: _extraTools);

		var (prompts, images) = await PreparePromptsAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false);
		for (var index = 0; index < prompts.Count; index++)
		{
			// 图片附件只挂在第一条提示上（与 pi 的 initialImages 一致）。
			var message = session.PrepareInput(prompts[index], index == 0 ? images : null);
			await RunAndPrintAsync(session, message, cancellationToken).ConfigureAwait(false);
		}

		// 与 pi 一致：被输出上限截断时打印部分文本、在 stderr 说明原因，并以 1 退出 ——
		// 静默地以 0 退出会让脚本把半截回答当成完整结果。
		if (LastStopReasonIsTruncated(session))
		{
			error.WriteLine("Response was truncated: the model reached its output token limit.");
			return 1;
		}

		// 提供方错误也以非零退出，不管是带 stopReason 的助手消息还是提前失败。
		return LastStopReasonWasError(session) || session.ErrorMessage is { Length: > 0 } ? 1 : 0;
	}

	/// <summary>
	/// 把管道 stdin、<c>@file</c> 附件与第一个命令行提示合成初始提示，其余提示保持顺序。
	/// 返回的列表至少含一项（可能为空字符串），保证无参数调用仍会跑一轮。
	/// </summary>
	private static async Task<(List<string> Prompts, List<ImageContent> Images)> PreparePromptsAsync(
		CliArguments arguments,
		string cwd,
		TextReader input,
		CancellationToken cancellationToken)
	{
		var fileText = string.Empty;
		var images = new List<ImageContent>();
		if (arguments.FileArgs.Count > 0)
		{
			(fileText, images) = CliInput.ProcessFiles(arguments.FileArgs, cwd);
		}

		// 仅在 stdin 被重定向时读取，否则交互式 TTY 会在这里阻塞。RPC 模式由协议占用 stdin。
		string? stdin = null;
		if (arguments.Mode != CliMode.Rpc && Console.IsInputRedirected)
		{
			stdin = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			if (stdin.Length == 0)
			{
				stdin = null;
			}
		}

		var prompts = new List<string>(arguments.Prompts.Count + 1);
		var first = new StringBuilder();
		if (stdin is not null)
		{
			first.Append(stdin);
		}

		first.Append(fileText);
		if (arguments.Prompts.Count > 0)
		{
			first.Append(arguments.Prompts[0]);
			for (var index = 1; index < arguments.Prompts.Count; index++)
			{
				prompts.Add(arguments.Prompts[index]);
			}
		}

		if (first.Length > 0 || prompts.Count == 0)
		{
			prompts.Insert(0, first.ToString());
		}

		return (prompts, images);
	}

	private async Task RunAndPrintAsync(CodingAgentSession session, Message message, CancellationToken cancellationToken)
	{
		var collected = new StringBuilder();

		using var subscription = session.Subscribe(@event =>
		{
			switch (@event)
			{
				case MessageEndEvent { Message: AssistantMessage assistant }:
					lock (_outputGate)
					{
						foreach (var block in assistant.Content.OfType<TextContent>())
						{
							collected.Append(block.Text);
						}
					}

					break;
			}

			return ValueTask.CompletedTask;
		});

		try
		{
			await session.PromptAsync(message, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// 取消（Ctrl+C）时也要把已经生成的部分写出来：用户等了很久的回答不该凭空消失。
			lock (_outputGate)
			{
				var text = collected.ToString();
				if (text.Length > 0)
				{
					output.WriteLine(text);
				}
			}
		}

		if (session.ErrorMessage is { Length: > 0 } errorMessage)
		{
			error.WriteLine($"pi: {errorMessage}");
		}
	}

	private Task RunAndPrintAsync(CodingAgentSession session, string prompt, CancellationToken cancellationToken) =>
		RunAndPrintAsync(session, session.PrepareInput(prompt), cancellationToken);

	/// <summary>最后一条助手回复是否因为达到输出上限而被截断。</summary>
	private static bool LastStopReasonIsTruncated(CodingAgentSession session) =>
		session.Session.Entries
			.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<AssistantMessage>()
			.LastOrDefault() is { StopReason: StopReason.Length };

	private static bool LastStopReasonWasError(CodingAgentSession session) =>
		session.Session.Entries
			.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<AssistantMessage>()
			.LastOrDefault() is { StopReason: StopReason.Error or StopReason.Aborted };

	/// <summary>最后一次运行是否以「被中止」结束，供 <c>agent_settled.aborted</c> 使用。</summary>
	private static bool LastStopReasonWasAborted(CodingAgentSession session) =>
		session.Session.Entries
			.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<AssistantMessage>()
			.LastOrDefault() is { StopReason: StopReason.Aborted };

	// ------------------------------------------------------------------ JSON

	/// <summary>JSON 模式：先输出会话头部，再为每个代理事件输出一行 JSONL。</summary>
	private async Task<int> RunJsonAsync(CliArguments arguments, string cwd, TextReader input, CancellationToken cancellationToken)
	{
		using var session = environment.CreateSession(arguments, cwd, extraTools: _extraTools);

		WriteRecord(SessionHeaderRecord(session.Session));

		using var subscription = session.Subscribe(@event =>
		{
			WriteRecord(AgentEventWriter.Write(@event));
			return ValueTask.CompletedTask;
		});

		var (prompts, images) = await PreparePromptsAsync(arguments, cwd, input, cancellationToken).ConfigureAwait(false);
		for (var index = 0; index < prompts.Count; index++)
		{
			await session.PromptAsync(session.PrepareInput(prompts[index], index == 0 ? images : null), cancellationToken)
				.ConfigureAwait(false);
		}

		WriteRecord(AgentEventWriter.AgentSettled(LastStopReasonWasAborted(session)));
		// 与 pi 一致：被输出上限截断时打印部分文本、在 stderr 说明原因，并以 1 退出 ——
		// 静默地以 0 退出会让脚本把半截回答当成完整结果。
		if (LastStopReasonIsTruncated(session))
		{
			error.WriteLine("Response was truncated: the model reached its output token limit.");
			return 1;
		}

		return LastStopReasonWasError(session) || session.ErrorMessage is { Length: > 0 } ? 1 : 0;
	}

	/// <summary>JSON 模式的头部记录，字段与会话文件头部一致。</summary>
	public static string SessionHeaderRecord(SessionManager session)
	{
		var header = session.Header;
		var record = new JsonObject
		{
			["type"] = "session",
			["version"] = SessionFormat.CurrentVersion,
			["id"] = session.SessionId,
			["timestamp"] = header?.Timestamp ?? SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			["cwd"] = session.Cwd,
		};

		if (header?.ParentSession is { Length: > 0 } parent)
		{
			record["parentSession"] = parent;
		}

		return CliJson.Serialize(record);
	}

	// ------------------------------------------------------------------- RPC

	/// <summary>当前 RPC 会话的事件订阅；换会话实例时必须重建。</summary>
	private IDisposable? _sessionEvents;

	/// <summary>
	/// 订阅当前会话的代理事件并写成 JSONL。订阅绑定在会话实例上：<c>new_session</c> 会换掉实例，
	/// 必须重新订阅，否则客户端只收到 response、收不到任何 message_*/tool_* 事件。
	/// </summary>
	private void SubscribeSessionEvents()
	{
		_sessionEvents?.Dispose();
		_sessionEvents = _session?.Subscribe(@event =>
		{
			WriteRecord(AgentEventWriter.Write(@event));
			return ValueTask.CompletedTask;
		});
	}

	/// <summary>
	/// RPC 模式：在 stdin/stdout 上运行长驻的 JSONL 命令/响应/事件协议。不输出会话头部，
	/// 客户端改为调用 <c>get_state</c>。
	/// </summary>
	private async Task<int> RunRpcAsync(CliArguments arguments, string cwd, TextReader input, CancellationToken cancellationToken)
	{
		_arguments = arguments;
		_cwd = cwd;
		// 当前 RPC 会话的事件订阅；换会话实例时必须重建。
		_session = environment.CreateSession(arguments, cwd, extraTools: _extraTools, uiHost: this);
		SubscribeSessionEvents();

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				string? line;
				try
				{
					line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				if (line is null)
				{
					break; // stdin 已关闭
				}

				if (line.Trim().Length == 0)
				{
					continue;
				}

				var shouldExit = await HandleRpcCommandAsync(line, cancellationToken).ConfigureAwait(false);
				if (shouldExit)
				{
					break;
				}
			}

			// 命令在提示被接受时即返回；让已接受的运行跑完，使事件能送达客户端，
			// 并在进程退出前把会话写入磁盘。
			// stdin 关闭后不应再等待客户端回答扩展 UI，否则会挂住退出。
			CancelPendingUiRequests();
			Task[] pending;
			lock (_backgroundRuns)
			{
				pending = [.. _backgroundRuns];
			}

			await Task.WhenAll(pending).ConfigureAwait(false);
			return 0;
		}
		finally
		{
			_bashCancellation?.Dispose();
			_bashCancellation = null;
			_session?.Dispose();
			_session = null;
		}
	}

	private async Task<bool> HandleRpcCommandAsync(string line, CancellationToken cancellationToken)
	{
		var session = _session ?? throw new InvalidOperationException("No active session");

		JsonObject? command;
		try
		{
			command = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException ex)
		{
			WriteRecord(Response(null, "unknown", success: false, data: null, errorMessage: $"Invalid JSON: {ex.Message}"));
			return false;
		}

		if (command is null)
		{
			WriteRecord(Response(null, "unknown", success: false, data: null, errorMessage: "Record must be a JSON object"));
			return false;
		}

		// id 按协议是字符串，但客户端发数字时也必须回显（否则响应无法关联）；
		// 数字原样字符串化，保持与请求值一致的可读形式。
		var id = command["id"] switch
		{
			JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
			JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
			_ => null,
		};
		var type = command["type"]?.GetValueKind() == JsonValueKind.String ? command["type"]!.GetValue<string>() : null;

		try
		{
			switch (type)
			{
				case "prompt":
					await HandlePromptAsync(session, id, command, cancellationToken).ConfigureAwait(false);
					break;

				case "steer":
				case "follow_up":
				{
					// 排队输入也要展开技能命令/提示词模板并保留图片内容块。
					var message = session.PrepareInput(TextMessage(command), ImagesOf(command));
					if (type == "steer")
					{
						session.Steer(message);
					}
					else
					{
						session.FollowUp(message);
					}

					WriteRecord(Response(id, type, true, new JsonObject { ["disposition"] = "queued" }, null));
					break;
				}

				case "abort":
					session.Abort();
					WriteRecord(Response(id, "abort", true, new JsonObject(), null));
					break;

				case "get_state":
					WriteRecord(Response(id, "get_state", true, StateRecord(session), null));
					break;

				case "get_session_stats":
					WriteRecord(Response(id, "get_session_stats", true, SessionStatsRecord(session), null));
					break;

				case "get_entries":
				{
					var since = command["since"]?.GetValue<string>();
					IReadOnlyList<SessionEntry>? entries = session.Session.Entries;

					if (since is { Length: > 0 })
					{
						entries = session.Session.EntriesSince(since);
						if (entries is null)
						{
							WriteRecord(Response(id, "get_entries", false, null, $"Unknown entry id: {since}"));
							break;
						}
					}

					WriteRecord(Response(id, "get_entries", true, EntriesRecord(session.Session, entries!), null));
					break;
				}

				case "get_tree":
					WriteRecord(Response(id, "get_tree", true, TreeRecord(session.Session), null));
					break;

				case "get_last_assistant_text":
					WriteRecord(Response(
						id,
						"get_last_assistant_text",
						true,
						new JsonObject { ["text"] = session.Session.LastAssistantText() },
						null));
					break;

				case "get_fork_messages":
				{
					var messages = new List<JsonNode?>();
					foreach (var message in session.Session.GetUserMessagesForForking())
					{
						messages.Add(new JsonObject
						{
							["entryId"] = message.EntryId,
							["text"] = message.Text,
						});
					}

					WriteRecord(Response(
						id,
						"get_fork_messages",
						true,
						new JsonObject { ["messages"] = new JsonArray([.. messages]) },
						null));
					break;
				}

				case "fork":
				{
					var entryId = command["entryId"]?.GetValue<string>()
						?? throw new InvalidOperationException("entryId is required");
					var position = command["position"]?.GetValue<string>() ?? "before";
					var text = session.ForkAt(entryId, position);

					WriteRecord(Response(id, "fork", true, new JsonObject
					{
						["text"] = text,
						["cancelled"] = false,
					}, null));
					break;
				}

				case "clone":
					session.CloneAsync();
					WriteRecord(Response(id, "clone", true, new JsonObject { ["cancelled"] = false }, null));
					break;

				case "switch_session":
				{
					var path = command["sessionPath"]?.GetValue<string>()
						?? throw new InvalidOperationException("sessionPath is required");

					if (!File.Exists(path))
					{
						WriteRecord(Response(id, "switch_session", false, null, $"Session file not found: {path}"));
						break;
					}

					session.SwitchSession(SessionManager.Open(path, _arguments?.SessionDir));
					WriteRecord(Response(id, "switch_session", true, new JsonObject { ["cancelled"] = false }, null));
					break;
				}

				case "set_session_name":
				{
					var name = command["name"]?.GetValue<string>()
						?? throw new InvalidOperationException("name is required");
					session.SetSessionName(name);
					WriteRecord(Response(id, "set_session_name", true, new JsonObject(), null));
					break;
				}

				case "get_commands":
					WriteRecord(Response(id, "get_commands", true, CommandsRecord(session), null));
					break;

				case "get_resources":
					WriteRecord(Response(id, "get_resources", true, ResourcesRecord(session), null));
					break;

				case "reload_resources":
					session.ReloadResources();
					WriteRecord(Response(id, "reload_resources", true, ResourcesRecord(session), null));
					break;

				case "export_html":
				{
					var path = ExportHtml(session, command["outputPath"]?.GetValue<string>());
					WriteRecord(Response(id, "export_html", true, new JsonObject { ["path"] = path }, null));
					break;
				}

				case "cycle_model":
				{
					var data = CycleModel(session);
					WriteRecord(Response(id, "cycle_model", true, data, null));
					break;
				}

				case "cycle_thinking_level":
				{
					var data = CycleThinkingLevel(session);
					WriteRecord(Response(id, "cycle_thinking_level", true, data, null));
					break;
				}

				case "set_auto_retry":
					_autoRetry = command["enabled"]?.GetValue<bool>() ?? true;
					session.SetAutoRetryEnabled(_autoRetry);
					WriteRecord(Response(id, "set_auto_retry", true, new JsonObject { ["enabled"] = _autoRetry }, null));
					break;

				case "abort_retry":
					session.AbortRetry();
					WriteRecord(Response(id, "abort_retry", true, null, null));
					break;


				case "extension_ui_response":
					// 客户端回答扩展 UI 请求；命中等待中的 ask_question 时唤醒它。
					ResolveUiResponse(command);
					break;

				case "get_messages":
				{
					// 一次性从节点构建数组：JsonArray.Add<T> 需要运行时代码生成。
					// 系统提示词是运行时注入的，不属于会话消息（与 pi 一致）。
					var visible = session.Messages.Where(static message => message is not SystemMessage).ToList();
					var messages = new List<JsonNode?>(visible.Count);
					foreach (var message in visible)
					{
						messages.Add(JsonNode.Parse(SerializeMessage(message)));
					}

					WriteRecord(Response(
						id,
						"get_messages",
						true,
						new JsonObject { ["messages"] = new JsonArray([.. messages]) },
						null));
					break;
				}

				case "set_model":
				{
					var pattern = command["modelId"]?.GetValue<string>() ?? command["model"]?.GetValue<string>();
					var provider = command["provider"]?.GetValue<string>();
					if (pattern is null)
					{
						WriteRecord(Response(id, "set_model", false, null, "modelId is required"));
						break;
					}

					var model = CliEnvironment.FindModel(environment.Registry, pattern, provider);
					if (model is null)
					{
						WriteRecord(Response(id, "set_model", false, null, $"Model not found: {pattern}"));
						break;
					}

					if (!CliEnvironment.SupportedApis.Contains(model.Api, StringComparer.Ordinal))
					{
						WriteRecord(Response(id, "set_model", false, null, $"Unsupported api: {model.Api}"));
						break;
					}

					session.SetModel(model);
					WriteRecord(Response(id, "set_model", true, ModelRecord(model), null));
					break;
				}

				case "get_available_models":
				{
					var models = new List<JsonNode?>();
					foreach (var model in environment.ListAvailableModels(_arguments?.ApiKey))
					{
						models.Add(ModelRecord(model));
					}

					WriteRecord(Response(
						id,
						"get_available_models",
						true,
						new JsonObject { ["models"] = new JsonArray([.. models]) },
						null));
					break;
				}

				case "set_thinking_level":
				{
					var level = command["level"]?.GetValue<string>()
						?? command["thinkingLevel"]?.GetValue<string>()
						?? throw new InvalidOperationException("level is required");
					session.SetThinkingLevel(level);

					// pi 在这里不返回 data；客户端读的是 `level`，因此两个名字都发。
					WriteRecord(Response(
						id,
						"set_thinking_level",
						true,
						new JsonObject
						{
							["level"] = session.ThinkingLevel,
							["thinkingLevel"] = session.ThinkingLevel,
						},
						null));
					break;
				}

				case "get_available_thinking_levels":
				{
					// 用 JsonValue.Create 保持 AOT 安全；JsonArray.Add<T> 需要运行时代码生成。
					var levels = new List<JsonNode?>();
					foreach (var level in ThinkingLevelResolver.GetSupported(session.Model))
					{
						levels.Add(JsonValue.Create(level));
					}

					WriteRecord(Response(
						id,
						"get_available_thinking_levels",
						true,
						new JsonObject { ["levels"] = new JsonArray([.. levels]) },
						null));
					break;
				}

				case "new_session":
				{
					var parent = command["parentSession"]?.GetValue<string>();
					var manager = SessionManager.Create(
						_cwd,
						_arguments?.SessionDir,
						parentSession: parent);

					var previous = _session;
					_session = environment.CreateSession(
						_arguments!,
						_cwd,
						session: manager,
						extraTools: _extraTools,
						uiHost: this);
					previous?.Dispose();

					// 新实例需要新的订阅：旧订阅随旧会话一起失效。
					SubscribeSessionEvents();

					WriteRecord(Response(id, "new_session", true, new JsonObject { ["cancelled"] = false }, null));
					break;
				}

				case "clear_queue":
				{
					var (steering, followUp) = session.ClearQueue();
					WriteRecord(Response(id, "clear_queue", true, new JsonObject
					{
						["steering"] = ToTextArray(steering),
						["followUp"] = ToTextArray(followUp),
					}, null));
					break;
				}

				case "set_steering_mode":
				{
					session.SteeringMode = ParseQueueMode(command["mode"]?.GetValue<string>());
					WriteRecord(Response(id, "set_steering_mode", true, new JsonObject
					{
						["mode"] = session.SteeringMode == AgentQueueMode.All ? "all" : "one-at-a-time",
					}, null));
					break;
				}

				case "set_follow_up_mode":
				{
					session.FollowUpMode = ParseQueueMode(command["mode"]?.GetValue<string>());
					WriteRecord(Response(id, "set_follow_up_mode", true, new JsonObject
					{
						["mode"] = session.FollowUpMode == AgentQueueMode.All ? "all" : "one-at-a-time",
					}, null));
					break;
				}

				case "set_auto_compaction":
				{
					session.AutoCompactionEnabled = command["enabled"]?.GetValue<bool>() ?? true;
					WriteRecord(Response(id, "set_auto_compaction", true, new JsonObject
					{
						["enabled"] = session.AutoCompactionEnabled,
					}, null));
					break;
				}

				case "set_plan_mode":
				{
					session.SetPlanMode(command["enabled"]?.GetValue<bool>() ?? true);
					WriteRecord(Response(id, "set_plan_mode", true, new JsonObject
					{
						["enabled"] = session.PlanModeEnabled,
					}, null));
					break;
				}

				case "fetch_remote_models":
				{
					var pattern = command["modelId"]?.GetValue<string>() ?? command["model"]?.GetValue<string>();
					var provider = command["provider"]?.GetValue<string>();
					var model = pattern is null
						? session.Model
						: CliEnvironment.FindModel(environment.Registry, pattern, provider);

					if (model is null)
					{
						WriteRecord(Response(id, "fetch_remote_models", false, null, $"Model not found: {pattern}"));
						break;
					}

					var api = command["api"]?.GetValue<string>() ?? model.Api;
					var baseUrl = command["baseUrl"]?.GetValue<string>() ?? model.BaseUrl;
					var apiKey = _arguments?.ApiKey ?? environment.ProviderApiKeys.GetValueOrDefault(model.Provider);
					var catalog = await RemoteModelCatalog
						.FetchAsync(api, baseUrl, apiKey, cancellationToken: cancellationToken)
						.ConfigureAwait(false);

					var models = new List<JsonNode?>(catalog.Models.Count);
					foreach (var catalogId in catalog.Models)
					{
						models.Add(JsonValue.Create(catalogId));
					}

					WriteRecord(Response(id, "fetch_remote_models", true, new JsonObject
					{
						["ok"] = catalog.Ok,
						["models"] = new JsonArray([.. models]),
						["error"] = catalog.Error,
					}, null));
					break;
				}

				case "get_usage_stats":
				{
					// 跨会话统计：有界扫描会话 JSONL，按需触发，不做轮询。
					var weeks = command["weeks"]?.GetValue<int>() ?? 26;
					var since = command["since"]?.GetValue<string>();
					var project = command["project"]?.GetValue<string>();
					var options = new UsageStatsOptions
					{
						Project = string.IsNullOrEmpty(project) ? null : project,
						Since = DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var cutoff) ? cutoff : null,
						Rates = (provider, model) => environment.Registry.Get(provider, model)?.Cost,
					};
					var root = string.IsNullOrEmpty(_arguments?.SessionDir) ? UsageStats.DefaultRoot() : _arguments!.SessionDir!;
					var report = await Task.Run(() => UsageStats.Scan(root, options, cancellationToken), cancellationToken).ConfigureAwait(false);

					WriteRecord(Response(id, "get_usage_stats", true, UsageStatsJson(report, weeks), null));
					break;
				}

				case "list_mcp":
				{
					WriteRecord(Response(id, "list_mcp", true, McpStatus(), null));
					break;
				}

				case "reconnect_mcp":
				{
					// 保持会话不变，只重建 MCP 连接并热替换工具集。
					var previous = _mcp;
					_mcp = null;
					if (previous is not null)
					{
						await previous.DisposeAsync().ConfigureAwait(false);
					}

					await ConnectMcpAsync(_cwd, environment.ProjectTrusted, cancellationToken).ConfigureAwait(false);
					session.SetTools(environment.ComposeTools(_arguments!, _extraTools));
					WriteRecord(Response(id, "reconnect_mcp", true, McpStatus(), null));
					break;
				}

				case "test_model":
				{
					var pattern = command["modelId"]?.GetValue<string>() ?? command["model"]?.GetValue<string>();
					var provider = command["provider"]?.GetValue<string>();
					var model = pattern is null
						? session.Model
						: CliEnvironment.FindModel(environment.Registry, pattern, provider);

					if (model is null)
					{
						WriteRecord(Response(id, "test_model", false, null, $"Model not found: {pattern}"));
						break;
					}

					if (!CliEnvironment.SupportedApis.Contains(model.Api, StringComparer.Ordinal))
					{
						WriteRecord(Response(id, "test_model", false, null, $"Unsupported api: {model.Api}"));
						break;
					}

					var apiKey = _arguments?.ApiKey ?? environment.ProviderApiKeys.GetValueOrDefault(model.Provider);
					var result = await ProviderProbe
						.TestAsync(environment.ProbeStreamFn, model, new StreamOptions { ApiKey = apiKey }, cancellationToken)
						.ConfigureAwait(false);

					WriteRecord(Response(id, "test_model", true, new JsonObject
					{
						["ok"] = result.Ok,
						["latencyMs"] = result.LatencyMs,
						["error"] = result.Error,
						["usage"] = result.Usage is null ? null : AgentEventWriter.UsageRecord(result.Usage),
					}, null));
					break;
				}

				case "bash":
					await HandleRpcBashAsync(session, id, command, cancellationToken).ConfigureAwait(false);
					break;

				case "abort_bash":
					_bashCancellation?.Cancel();
					WriteRecord(Response(id, "abort_bash", true, new JsonObject(), null));
					break;

				case "compact":
				{
					var instructions = command["customInstructions"]?.GetValue<string>()
						?? command["instructions"]?.GetValue<string>();
					var result = await session.CompactAsync(instructions, cancellationToken).ConfigureAwait(false);

					if (result is null)
					{
						// 没有值得压缩的内容时，pi 返回成功但不带 data。
						WriteRecord(Response(id, "compact", true, null, null));
						break;
					}

					// pi 返回 CompactionResult，客户端据此渲染检查点。
					WriteRecord(Response(
						id,
						"compact",
						true,
						new JsonObject
						{
							["summary"] = result.Summary,
							["firstKeptEntryId"] = result.FirstKeptEntryId,
							["tokensBefore"] = result.TokensBefore,
							["estimatedTokensAfter"] = session.ContextTokens().Tokens,
							["usage"] = AgentEventWriter.UsageRecord(result.Usage),
						},
						null));
					break;
				}

				case "exit":
					WriteRecord(Response(id, "exit", true, new JsonObject(), null));
					return true;

				default:
					WriteRecord(Response(id, type ?? "unknown", false, null, $"Unsupported command: {type}"));
					break;
			}
		}
		catch (Exception ex)
		{
			WriteRecord(Response(id, type ?? "unknown", success: false, data: null, errorMessage: ex.Message));
		}

		return false;
	}

	private static AgentQueueMode ParseQueueMode(string? mode) =>
		mode == "all" ? AgentQueueMode.All : AgentQueueMode.OneAtATime;

	private static JsonArray ToTextArray(IReadOnlyList<Message> messages)
	{
		var array = new List<JsonNode?>();
		foreach (var message in messages)
		{
			array.Add(JsonValue.Create(MessageText(message)));
		}

		return new JsonArray([.. array]);
	}

	private static string MessageText(Message message) => message switch
	{
		UserMessage user => user.Content.Text ?? string.Empty,
		AssistantMessage assistant => string.Concat(assistant.Content.OfType<TextContent>().Select(static block => block.Text)),
		CustomMessage custom => custom.Content.Text ?? string.Empty,
		_ => string.Empty,
	};

	private async Task HandleRpcBashAsync(
		CodingAgentSession session,
		string? id,
		JsonObject command,
		CancellationToken cancellationToken)
	{
		var shellCommand = command["command"]?.GetValueKind() == JsonValueKind.String
			? command["command"]!.GetValue<string>()
			: throw new InvalidOperationException("command is required");

		var excludeFromContext = command["excludeFromContext"]?.GetValue<bool>() ?? false;

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_bashCancellation = cancellation;

		var updates = new List<Task>();
		void OnOutput(string line)
		{
			// 流式输出只是过程信息；最终结果在响应里。
			updates.Add(Task.Run(() => WriteRecord(CliJson.Serialize(new JsonObject
			{
				["type"] = "bash_execution_update",
				["id"] = id,
				["output"] = line + "\n",
			}))));

		}

		try
		{
			var result = await session
				.RunBashAsync(shellCommand, excludeFromContext, OnOutput, cancellation.Token)
				.ConfigureAwait(false);

			await Task.WhenAll(updates).ConfigureAwait(false);

			var data = new JsonObject
			{
				["output"] = result.Output,
				["exitCode"] = result.ExitCode,
				["cancelled"] = result.Cancelled,
				["truncated"] = result.Truncated,
			};

			if (result.FullOutputPath is { Length: > 0 } path)
			{
				data["fullOutputPath"] = path;
			}

			WriteRecord(Response(id, "bash", true, data, null));
		}
		finally
		{
			_bashCancellation = null;
		}
	}

	private async Task HandlePromptAsync(
		CodingAgentSession session,
		string? id,
		JsonObject command,
		CancellationToken cancellationToken)
	{
		var text = command["message"]?.GetValueKind() == JsonValueKind.String
			? command["message"]!.GetValue<string>()
			: throw new InvalidOperationException("message is required");

		if (session.IsStreaming)
		{
			var behavior = command["streamingBehavior"]?.GetValue<string>();
			var queued = session.PrepareInput(text, ImagesOf(command));

			switch (behavior)
			{
				case "steer":
					session.Steer(queued);
					break;
				case "followUp":
					session.FollowUp(queued);
					break;
				default:
					WriteRecord(Response(
						id,
						"prompt",
						false,
						null,
						"Agent is already streaming; pass streamingBehavior \"steer\" or \"followUp\"."));
					return;
			}

			WriteRecord(Response(id, "prompt", true, new JsonObject { ["disposition"] = "queued" }, null));
			return;
		}

		// 空闲路径同样保留图片内容块，并把展开交给会话（技能命令 + 提示词模板）。
		var started = session.PrepareInput(text, ImagesOf(command));
		WriteRecord(Response(id, "prompt", true, new JsonObject { ["disposition"] = "started" }, null));

		// 响应只表示“已接受”；运行期间事件会继续在 stdout 上流式输出。
		var run = Task.Run(
			async () =>
			{
				try
				{
					await session.PromptAsync(started, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					error.WriteLine($"pi: prompt failed: {ex.Message}");
				}
				finally
				{
					// pi 的客户端（含 PiDeck）以 agent_settled 判定「本次运行不会再自动继续」，
					// 只等 agent_end 会在自动重试/压缩后提前收尾。
					WriteRecord(AgentEventWriter.AgentSettled(LastStopReasonWasAborted(session)));
				}
			},
			CancellationToken.None);

		lock (_backgroundRuns)
		{
			_backgroundRuns.Add(run);
		}
	}

	private static string TextMessage(JsonObject command) =>
		command["message"]?.GetValueKind() == JsonValueKind.String
			? command["message"]!.GetValue<string>()
			: throw new InvalidOperationException("message is required");

	/// <summary>从 RPC 命令里读取 <c>images</c> 内容块，缺失时返回空列表。</summary>
	private static List<ImageContent> ImagesOf(JsonObject command)
	{
		if (command["images"] is not JsonArray images || images.Count == 0)
		{
			return [];
		}

		var result = new List<ImageContent>(images.Count);
		foreach (var image in images)
		{
			if (image is not JsonObject obj)
			{
				continue;
			}

			result.Add(new ImageContent
			{
				Data = obj["data"]?.GetValue<string>() ?? string.Empty,
				MimeType = obj["mimeType"]?.GetValue<string>() ?? "image/png",
			});
		}

		return result;
	}

	/// <summary>pi 返回的模型对象，便于客户端复用自己的渲染逻辑。</summary>
	private static JsonObject ModelRecord(Model model) => new()
	{
		["id"] = model.Id,
		["name"] = model.Name.Length > 0 ? model.Name : model.Id,
		["api"] = model.Api,
		["provider"] = model.Provider,
		["baseUrl"] = model.BaseUrl,
		["reasoning"] = model.Reasoning,
		["input"] = new JsonArray([.. model.Input.Select(static value => (JsonNode?)JsonValue.Create(value))]),
		["type"] = model.Type switch
		{
			ModelType.Image => "image",
			ModelType.Classifier => "classifier",
			_ => "chat",
		},
		["contextWindow"] = model.ContextWindow,
		["maxTokens"] = model.MaxTokens,
		["cost"] = new JsonObject
		{
			["input"] = model.Cost?.Input ?? 0,
			["output"] = model.Cost?.Output ?? 0,
			["cacheRead"] = model.Cost?.CacheRead ?? 0,
			["cacheWrite"] = model.Cost?.CacheWrite ?? 0,
		},
	};

	/// <summary>模型对象，包含模型选择器会读取的字段，以及思考级别映射。</summary>
	private static JsonObject ModelRecordWithThinking(Model model)
	{
		var record = ModelRecord(model);
		if (model.Compat is { Count: > 0 } compat)
		{
			record["compat"] = compat.DeepClone();
		}

		if (model.InputLimits is { Count: > 0 } limits)
		{
			record["inputLimits"] = limits.DeepClone();
		}

		if (model.ThinkingLevelMap is { Count: > 0 } map)
		{
			// 值为 null 表示模型不支持该级别，pi 也会显式发出。
			var levels = new JsonObject();
			foreach (var (level, value) in map)
			{
				levels[level] = value is null ? null : JsonValue.Create(value);
			}

			record["thinkingLevelMap"] = levels;
		}

		return record;
	}

	private static string Response(string? id, string commandType, bool success, JsonObject? data, string? errorMessage)
	{
		var record = new JsonObject
		{
			["type"] = "response",
			["command"] = commandType,
			["success"] = success,
		};

		if (id is not null)
		{
			record["id"] = id;
		}

		if (success && data is not null)
		{
			record["data"] = data;
		}

		if (!success)
		{
			record["error"] = errorMessage ?? "Command failed";
		}

		return CliJson.Serialize(record);
	}

	// ------------------------------------------------------------ 交互模式

	/// <summary>基于行的 REPL。全屏 TUI 渲染不在本端口范围内。</summary>
	private async Task<int> RunInteractiveAsync(CliArguments arguments, string cwd, TextReader input, CancellationToken cancellationToken)
	{
		using var session = environment.CreateSession(
			arguments,
			cwd,
			extraTools: _extraTools,
			uiHost: new ConsoleUiHost(input, error));
		error.WriteLine($"pi (PiWeft) — model {session.Model.Provider}/{session.Model.Id}. Type /exit to quit.");

		// 交互模式不读管道 stdin（会把 TTY 阻塞），但 <c>@file</c> 附件仍作为第一条提示发出。
		if (arguments.FileArgs.Count > 0)
		{
			var (fileText, images) = CliInput.ProcessFiles(arguments.FileArgs, cwd);
			await RunAndPrintAsync(session, session.PrepareInput(fileText, images), cancellationToken).ConfigureAwait(false);
		}

		while (!cancellationToken.IsCancellationRequested)
		{
			error.Write("> ");
			error.Flush();

			var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
			{
				break;
			}

			var trimmed = line.Trim();
			if (trimmed.Length == 0)
			{
				continue;
			}

			if (trimmed is "/exit" or "/quit")
			{
				break;
			}

			if (trimmed == "/model")
			{
				error.WriteLine($"{session.Model.Provider}/{session.Model.Id} (thinking {session.ThinkingLevel})");
				continue;
			}

			if (trimmed == "/compact")
			{
				var result = await session.CompactAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
				error.WriteLine(result is null ? "Nothing to compact." : $"Compacted {result.TokensBefore} tokens.");
				continue;
			}

			if (trimmed == "/plan")
			{
				session.SetPlanMode(!session.PlanModeEnabled);
				error.WriteLine(session.PlanModeEnabled
					? "计划模式已开启：只读探索，edit / write 已停用。用 /plan 再次关闭。"
					: "计划模式已关闭。");
				continue;
			}

			if (trimmed == "/reload")
			{
				// 与 pi 的 /reload 对应：重新发现技能、提示词模板与主题，不重建会话。
				session.ReloadResources();
				error.WriteLine(
					$"已重载 {session.Resources.Skills.Count} 个技能、{session.Resources.Templates.Count} 个提示词模板、{session.Resources.Themes.Count} 个主题。");
				continue;
			}

			if (trimmed == "/copy")
			{
				// 与 pi 的 /copy 对应：把最近一条助手回复放进系统剪贴板。
				var lastText = session.Session.LastAssistantText();
				if (string.IsNullOrEmpty(lastText))
				{
					error.WriteLine("还没有可复制的助手回复。");
				}
				else
				{
					error.WriteLine(CliClipboard.TryCopy(lastText)
						? $"已复制 {lastText.Length} 个字符到剪贴板。"
						: "复制失败：未找到可用的剪贴板工具（wl-copy/xclip/pbcopy/clip）。");
				}

				continue;
			}

			if (trimmed.StartsWith("/import", StringComparison.Ordinal))
			{
				// 与 pi 的 /import 对应：把另一个会话的当前分支消息追加进本会话。
				var source = trimmed["/import".Length..].Trim().Trim('"');
				if (source.Length == 0)
				{
					error.WriteLine("用法：/import <会话文件路径>");
					continue;
				}

				var resolved = PiWeft.CodingAgent.Session.UserPath.Resolve(source);
				if (!File.Exists(resolved))
				{
					error.WriteLine($"pi: 找不到会话文件 {resolved}");
					continue;
				}

				try
				{
					var imported = ImportSession(session, resolved);
					error.WriteLine($"已导入 {imported} 条消息自 {resolved}。");
				}
				catch (Exception exception) when (exception is InvalidOperationException or IOException
					or UnauthorizedAccessException or System.Text.Json.JsonException)
				{
					error.WriteLine($"pi: 导入失败：{exception.Message}");
				}

				continue;
			}

			if (trimmed == "/bug")
			{
				// 与 pi 的 /bug 对应：给出反馈入口与本会话路径，由用户自行整理提交。
				error.WriteLine("反馈问题：https://github.com/earendil-works/pi/issues");
				error.WriteLine($"当前会话文件：{session.Session.SessionFile ?? "（未落盘）"}");
				continue;
			}

			// pi 的内联 shell：`!cmd` 把输出并入对话，`!!cmd` 只执行不进模型上下文。
			if (trimmed.StartsWith('!'))
			{
				await RunInlineShellAsync(session, trimmed, cancellationToken).ConfigureAwait(false);
				continue;
			}

			// pi 的其余内置斜杠命令；未识别的 `/…` 仍然回落到技能命令与提示词模板展开。
			if (await HandleSlashCommandAsync(session, arguments, trimmed, input, cancellationToken).ConfigureAwait(false))
			{
				continue;
			}

			// 技能命令与提示词模板由 session.PromptAsync 展开，交互模式不再重复展开。
			await RunAndPrintAsync(session, trimmed, cancellationToken).ConfigureAwait(false);
		}

		return 0;
	}

	/// <summary>
	/// 把另一个会话文件当前分支的消息追加进当前会话，并重建代理上下文。
	/// 与 pi 的 <c>/import</c> 语义一致：导入的是消息内容，不带走模型/思考级别等元数据条目。
	/// </summary>
	internal static int ImportSession(CodingAgentSession session, string sourcePath)
	{
		if (session.IsStreaming)
		{
			throw new InvalidOperationException("会话正在生成回复，稍后再导入");
		}

		if (!File.Exists(sourcePath))
		{
			throw new InvalidOperationException($"Session file not found: {sourcePath}");
		}

		// 只读打开源会话：导入是复制条目，不动源文件，无需释放。
		var source = SessionManager.Open(sourcePath, session.Session.SessionDir);
		var messages = source.BuildProjectedMessages();
		var count = 0;
		foreach (var message in messages)
		{
			session.Session.AppendMessage(message);
			count++;
		}

		if (count > 0)
		{
			// 同一实例换进去只为触发 SetMessages 重建上下文。
			session.SwitchSession(session.Session);
		}

		return count;
	}

	// ------------------------------------------------------------ 交互命令

	/// <summary>交互模式可用的斜杠命令一览（/help）。</summary>
	private const string InteractiveHelpText = """
		命令：
		  /model                    查看当前 provider/model/thinking
		  /thinking [级别]          查看/设置思考级别（off…max）
		  /compact                  压缩上下文
		  /plan                     切换计划模式（只读）
		  /reload                   重新发现技能/提示词模板/主题
		  /new                      开始新会话
		  /resume                   选择并恢复历史会话
		  /session                  查看会话文件、消息数与用量
		  /name <名称>              给当前会话命名
		  /tree                     查看分支树
		  /fork                     从某条用户消息分叉
		  /clone                    把当前分支复制为新会话
		  /export [路径]            导出 HTML（JSONL 是会话文件本身）
		  /scoped-models            查看 --models 轮换范围
		  /mcp                      查看已连接的 MCP 服务器与工具
		  /trust                    查看当前项目信任状态
		  /settings                 查看设置文件路径与生效值
		  /login [provider]         OAuth 登录（默认 openai-codex）
		  /logout [provider]        移除已存凭据
		  /copy                     复制最近一条助手回复
		  /import <路径>            导入另一个会话的当前分支
		  /export、/bug、/exit     导出 / 反馈 / 退出
		  !<命令>                   执行 shell 并把输出加入对话
		  !!<命令>                  执行 shell，输出不进模型上下文
		未实现的 pi 命令：/share、/llama、/hotkeys（行式 REPL 无键位绑定）。
		""";

	/// <summary>交互模式的内联 shell：`!cmd` 把输出并入对话，`!!cmd` 只执行不入上下文。</summary>
	private async Task RunInlineShellAsync(CodingAgentSession session, string line, CancellationToken cancellationToken)
	{
		var excludeFromContext = line.StartsWith("!!");
		var command = (excludeFromContext ? line[2..] : line[1..]).Trim();
		if (command.Length == 0)
		{
			error.WriteLine("用法：!<命令>（输出进入对话）或 !!<命令>（输出不进模型上下文）");
			return;
		}

		var result = await session
			.RunBashAsync(command, excludeFromContext, onOutput: null, cancellationToken)
			.ConfigureAwait(false);

		if (result.Output.Length > 0)
		{
			output.WriteLine(result.Output.TrimEnd());
		}

		if (result.ExitCode is { } exitCode && exitCode != 0)
		{
			error.WriteLine($"退出码 {exitCode}");
		}

		if (excludeFromContext)
		{
			error.WriteLine("（输出未加入模型上下文）");
		}
	}

	/// <summary>
	/// pi 内置斜杠命令的行式实现。返回 true 表示已消费；未识别的 <c>/…</c> 继续走
	/// 技能命令与提示词模板展开（与既有行为一致）。
	/// </summary>
	private async Task<bool> HandleSlashCommandAsync(
		CodingAgentSession session,
		CliArguments arguments,
		string line,
		TextReader input,
		CancellationToken cancellationToken)
	{
		var space = line.IndexOf(' ');
		var command = space < 0 ? line : line[..space];
		var rest = space < 0 ? string.Empty : line[(space + 1)..].Trim();

		switch (command)
		{
			case "/help":
				error.WriteLine(InteractiveHelpText);
				return true;

			case "/thinking":
			{
				if (rest.Length == 0)
				{
					error.WriteLine($"thinking: {session.ThinkingLevel}（可选 off/minimal/low/medium/high/xhigh/max）");
					return true;
				}

				try
				{
					session.SetThinkingLevel(rest);
					error.WriteLine($"thinking → {session.ThinkingLevel}");
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
				{
					error.WriteLine($"pi: {exception.Message}");
				}

				return true;
			}

			case "/name":
				if (rest.Length == 0)
				{
					error.WriteLine("用法：/name <名称>（再次 /name 新名称可修改）");
					return true;
				}

				session.SetSessionName(rest);
				error.WriteLine($"会话名称 → {rest}");
				return true;

			case "/session":
			{
				var stats = session.Statistics();
				error.WriteLine($"文件: {session.Session.SessionFile ?? "（未落盘）"}");
				error.WriteLine($"ID: {(session.Session.SessionId.Length > 0 ? session.Session.SessionId : "（未生成）")}");
				error.WriteLine($"名称: {session.SessionName ?? "（未命名）"}");
				error.WriteLine(
					$"消息条目: {session.Session.Entries.Count(static entry => entry is SessionMessageEntry)}  " +
					$"令牌: {stats.Tokens:N0}  费用: ${stats.Cost:F4}");
				return true;
			}

			case "/new":
				if (session.IsStreaming)
				{
					error.WriteLine("pi: 正在生成回复，稍后再试");
					return true;
				}

				session.Session.StartNew();
				session.SwitchSession(session.Session);
				error.WriteLine($"已开始新会话：{session.Session.SessionFile ?? "（未落盘）"}");
				return true;

			case "/resume":
				await ResumeInteractiveAsync(session, input, cancellationToken).ConfigureAwait(false);
				return true;

			case "/tree":
				PrintSessionTree(session);
				return true;

			case "/fork":
				await ForkInteractiveAsync(session, input).ConfigureAwait(false);
				return true;

			case "/clone":
				if (session.IsStreaming)
				{
					error.WriteLine("pi: 正在生成回复，稍后再试");
					return true;
				}

				try
				{
					session.CloneAsync();
					error.WriteLine($"已克隆到 {session.Session.SessionFile}");
				}
				catch (InvalidOperationException exception)
				{
					error.WriteLine($"pi: {exception.Message}");
				}

				return true;

			case "/export":
				output.WriteLine($"Exported to: {ExportHtml(session, rest.Length > 0 ? rest : null)}");
				return true;

			case "/scoped-models":
				error.WriteLine(arguments.Models.Count == 0
					? "未设置 --models（启动时用 --models <模式列表> 限定轮换范围）。"
					: $"--models: {string.Join(", ", arguments.Models)}\n当前: {session.Model.Provider}/{session.Model.Id}");
				return true;

			case "/mcp":
				PrintMcpServers();
				return true;

			case "/trust":
				error.WriteLine(session.ProjectTrusted
					? "当前项目：受信任（项目级设置、MCP 与资源会加载）。"
					: "当前项目：不受信任（项目级设置、MCP 与资源未加载）。");
				error.WriteLine("覆盖：启动时 -a / -na，或全局 settings.defaultProjectTrust（always/never/ask）。");
				return true;

			case "/settings":
				error.WriteLine($"全局设置: {CodingAgentSettings.GlobalPath()}");
				error.WriteLine($"项目设置: {CodingAgentSettings.ProjectPath(session.Cwd)}");
				error.WriteLine(
					$"默认模型: {session.Settings.DefaultProvider ?? "*"}/{session.Settings.DefaultModel ?? "*"}  " +
					$"thinking: {session.Settings.DefaultThinkingLevel ?? "(默认)"}");
				return true;

			case "/login":
				await AuthCommands.RunAsync("login", rest.Length == 0 ? [] : [rest], output, error, cancellationToken)
					.ConfigureAwait(false);
				return true;

			case "/logout":
				await AuthCommands.RunAsync("logout", rest.Length == 0 ? [] : [rest], output, error, cancellationToken)
					.ConfigureAwait(false);
				return true;

			case "/hotkeys":
				error.WriteLine("行式 REPL 没有键位绑定；桌面端的快捷键在「设置 → 快捷键」里自定义。");
				return true;

			case "/changelog":
				error.WriteLine($"PiWeft（RPC 契约 {RpcContractVersion}）。上游变更：https://github.com/earendil-works/pi/blob/main/packages/coding-agent/CHANGELOG.md");
				return true;

			case "/share":
				error.WriteLine("pi: /share 未实现（会话上传与 Radius/Gist 集成不在本端口范围）。可用 /export 导出 HTML。");
				return true;

			case "/llama":
				error.WriteLine("pi: /llama 未实现（内置 llama.cpp 集成不在本端口范围）。");
				return true;

			default:
				return false;
		}
	}

	private async Task ResumeInteractiveAsync(CodingAgentSession session, TextReader input, CancellationToken cancellationToken)
	{
		if (session.IsStreaming)
		{
			error.WriteLine("pi: 正在生成回复，稍后再试");
			return;
		}

		var sessions = SessionManager.ListSessions(session.Session.SessionDir, session.Cwd);
		if (sessions.Count == 0)
		{
			error.WriteLine("当前目录没有已保存的会话。");
			return;
		}

		for (var index = 0; index < sessions.Count; index++)
		{
			var info = sessions[index];
			var title = string.IsNullOrEmpty(info.Name) ? info.FirstMessage : info.Name;
			error.WriteLine($"{index + 1,3}. {FirstLine(title ?? string.Empty)}  [{info.Modified:yyyy-MM-dd HH:mm}]");
		}

		error.Write("输入序号选择会话（回车取消）: ");
		error.Flush();
		var picked = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
		if (!int.TryParse(picked?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var choice) ||
			choice < 1 || choice > sessions.Count)
		{
			return;
		}

		var opened = SessionManager.Open(sessions[choice - 1].Path, session.Session.SessionDir);
		session.SwitchSession(opened);
		error.WriteLine($"已切换到 {opened.SessionFile}");
	}

	private async Task ForkInteractiveAsync(CodingAgentSession session, TextReader input)
	{
		var candidates = session.Session.GetUserMessagesForForking();
		if (candidates.Count == 0)
		{
			error.WriteLine("当前分支没有用户消息可分叉。");
			return;
		}

		for (var index = 0; index < candidates.Count; index++)
		{
			error.WriteLine($"{index + 1,3}. {FirstLine(candidates[index].Text)}");
		}

		error.Write("输入序号，在该消息之前分叉（回车取消）: ");
		error.Flush();
		var picked = await input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
		if (!int.TryParse(picked?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var choice) ||
			choice < 1 || choice > candidates.Count)
		{
			return;
		}

		var text = session.ForkAt(candidates[choice - 1].EntryId, "before");
		if (text is { Length: > 0 })
		{
			// 分叉自用户消息之前时，pi 会把那条消息放回输入框；行式 REPL 直接重发它。
			await RunAndPrintAsync(session, session.PrepareInput(text), CancellationToken.None).ConfigureAwait(false);
		}
		else
		{
			error.WriteLine($"已切到条目 {candidates[choice - 1].EntryId}。");
		}
	}

	private void PrintSessionTree(CodingAgentSession session)
	{
		foreach (var entry in session.Session.Entries)
		{
			var marker = entry.Id == session.Session.LeafId ? "*" : " ";
			var parent = entry.ParentId is { Length: > 0 } parentId ? parentId[..Math.Min(8, parentId.Length)] : "--------";
			var label = entry switch
			{
				SessionMessageEntry message => DescribeMessage(message.Message),
				CompactionEntry compaction => $"compaction（保留自 {compaction.FirstKeptEntryId[..Math.Min(8, compaction.FirstKeptEntryId.Length)]}）",
				BranchSummaryEntry summary => $"branch_summary {FirstLine(summary.Summary, 48)}",
				ModelChangeEntry model => $"model {model.Provider}/{model.ModelId}",
				ThinkingLevelChangeEntry thinking => $"thinking {thinking.ThinkingLevel}",
				SessionInfoEntry info => $"name {info.Name ?? ""}",
				ContextEditEntry => "context_edit",
				UsageEntry usage => $"usage {usage.Kind} {usage.Model}",
				CustomEntry custom => $"custom {custom.CustomType}",
				CustomMessageEntry customMessage => $"custom_message {customMessage.CustomType}",
				LabelEntry => "label",
				_ => entry.Type,
			};

			error.WriteLine($"{marker} {entry.Id[..Math.Min(8, entry.Id.Length)]}  {parent}  {label}");
		}
	}

	/// <summary>按 <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> 前缀分组，展示每个服务器的工具数。</summary>
	private void PrintMcpServers()
	{
		var any = false;
		foreach (var group in _extraTools
			.Select(static tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal)
				? tool.Name["mcp__".Length..].Split("__")
				: null)
			.Where(static parts => parts is { Length: >= 2 })
			.GroupBy(static parts => parts![0], StringComparer.Ordinal)
			.OrderBy(static group => group.Key, StringComparer.Ordinal))
		{
			any = true;
			error.WriteLine($"{group.Key}: {group.Count()} 个工具");
		}

		if (!any)
		{
			error.WriteLine("没有已连接的 MCP 服务器（用 `pi mcp add` 添加，或 `pi mcp list` 查看）。");
		}
	}

	private static string FirstLine(string text, int max = 64)
	{
		var line = text.Split('\n')[0].Trim();
		return line.Length <= max ? line : $"{line[..max]}…";
	}

	/// <summary>分支树里一条消息的单行描述。</summary>
	private static string DescribeMessage(Message message) => message switch
	{
		UserMessage user => $"user {FirstLine(user.Content.Text ?? string.Empty, 48)}",
		AssistantMessage assistant => $"assistant {FirstLine(
			assistant.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty, 48)}",
		ToolResultMessage => "toolResult",
		SystemMessage => "system",
		CustomMessage custom => $"{custom.CustomRole} {FirstLine(custom.Content.Text ?? string.Empty, 40)}",
		_ => message.Role,
	};

	private void WriteRecord(string record)
	{
		lock (_outputGate)
		{
			output.Write(record);
			output.Write('\n');
			output.Flush();
		}
	}

	private static string SerializeMessage(Message message) => PiWeft.Ai.Json.PiJson.SerializeMessage(message);
}
