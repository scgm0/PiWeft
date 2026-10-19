using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.JavaScript;
using JsCodeModeTool = PiWeft.JavaScript.CodeModeTool;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// codemode 需要的会话能力：脚本能调用哪些工具、怎么执行（走完整工具管线）、
/// <c>store()</c> 从哪里读、成功后写到哪里。
/// </summary>
public sealed record CodeModeHost
{
	/// <summary>当前可被脚本调用的工具（可以包含 codemode 自身，工具会自行排除）。</summary>
	public required Func<IReadOnlyList<AgentTool>> Tools { get; init; }

	/// <summary>
	/// 按名称执行嵌套调用；必须走参数校验、钩子与中断，而不是直接调用 <see cref="AgentTool.Execute"/>。
	/// 最后一个参数是本次 codemode 执行的消息快照（供钩子上下文复用），没有会话时为 <c>null</c>。
	/// </summary>
	public required Func<string, JsonObject, string, CancellationToken, IReadOnlyList<Message>?, Task<AgentToolCallOutcome>> ExecuteTool { get; init; }

	/// <summary>
	/// 嵌套调用钩子上下文用的消息快照；每次 codemode 执行取一份并由所有嵌套调用复用，
	/// 避免每次工具调用都拷贝整段会话记录。
	/// </summary>
	public Func<IReadOnlyList<Message>>? Messages { get; init; }

	/// <summary><c>load()</c> 读到的最新值（按会话分支）。</summary>
	public Func<IReadOnlyDictionary<string, JsonNode?>> LoadStore { get; init; } = static () => new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

	/// <summary>成功执行后提交 <c>store()</c> 的改动。</summary>
	public Action<CodeModeStoreWrites> SaveStore { get; init; } = static _ => { };

	/// <summary><c>models.*</c> 的目录查询；为 <c>null</c> 时脚本调用目录查询会报错。</summary>
	public Func<CodeModeModelsRequest, JsonNode?>? Models { get; init; }

	/// <summary><c>models.classify()</c> / <c>models.generateImages()</c> 的宿主桥。</summary>
	public Func<CodeModeModelCall, CancellationToken, Task<CodeModeModelCallResult>>? ModelCall { get; init; }

	/// <summary>按工具名取 prompt guidelines；与 pi 一样附在声明与 <c>ALL_TOOLS</c> 的描述后。</summary>
	public Func<string, IReadOnlyList<string>>? Guidelines { get; init; }
}

/// <summary>
/// 原生 <c>codemode</c> 工具：模型写 JavaScript，在 Jint 沙箱里调用其它工具。
/// 嵌套调用的结果不进入 LLM 上下文，只有脚本的输出与返回值进入。
/// </summary>
public static class CodeModeTool
{
	/// <summary>工具名。</summary>
	public const string Name = "codemode";

	/// <summary>会话里保存 <c>store()</c> 改动的自定义条目类型。</summary>
	public const string EntryType = "codemode-store";

	public const string Snippet = "Run JavaScript that calls other tools";

	public static readonly IReadOnlyList<string> Guidelines =
	[
		"Use codemode to batch independent tool calls (Promise.allSettled), chain them, or filter large output, instead of many separate calls.",
	];

	/// <summary>默认输出预算（token），与 pi 一致。</summary>
	private const int DefaultMaxOutputTokens = 10_000;

	/// <summary>估算 token 时的字符数。</summary>
	private const int CharsPerToken = 4;

	/// <summary>工具声明的默认描述预算（估算 token），可由 <c>settings.codemode.inlineBudget</c> 覆盖。</summary>
	public const int DefaultInlineBudget = 3000;

	/// <summary>脚本没有 <c>@options.timeout_ms</c> 时的默认时限：与 pi 一样不设上限，靠 abort 与任务停滞检测。</summary>
	public static readonly TimeSpan DefaultTimeout = Timeout.InfiniteTimeSpan;

	private const string DescriptionIntro =
		"Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence), run as an async function body in a restricted sandbox: top-level `await` and `return` work. No Node, file system, network, or timers.\n" +
		"- `await tools.<name>({ ...args })` resolves to a string (JSON text for structured results), and rejects with an Error when the call fails. Calls still running when the script ends are cancelled.\n" +
		"- Optional first line: `// @options: {\"max_output_tokens\": 10000, \"timeout_ms\": 60000}`";

	private const string DescriptionGlobalsTools =
		"Globals:\n" +
		"- `text(value)`, `image(dataUrlOrImageBlock)`, `console.log(...)`, and top-level `return` add output; `exit()` ends the script. With several text items, each starts with a `==> text N/M <==` line, and `console` lines follow the other output in one `<console_output>` block.\n" +
		"- `store(key, value)` and `load(key)` keep JSON values across codemode calls.\n" +
		"- `ALL_TOOLS` lists every tool; `searchTools(query, { limit?, namespace? })` ranks all of them by topic, `describeTool(name)` returns one tool's declaration, and `describeNamespace(name)` lists a namespace's tools.";

	private const string DescriptionGlobalsModels =
		"- `models.getModelsOfType(type, provider?)`, `models.getAvailableOfType(type, provider?)`, and `models.getModelOfType(type, provider, id)` read the model catalog; type is `chat`, `image`, or `classifier`.";

	private const string DescriptionGlobalsModelCalls =
		"- `await models.classify(model, context)` and `await models.generateImages(model, context)` run a classifier or image model; `context` is the `state`/`images`/`questions` or `input` shape. Show generated images with `image(block)`.";

	/// <summary><c>models.*</c> 调用（分类/生图）在嵌套调用详情里的前缀。</summary>
	private const string ModelCallIdSuffix = "/models/";

	/// <summary>没有会话上下文时使用的占位工具：能执行纯脚本，但不能调用工具、也不持久化 store。</summary>
	public static BuiltinTool CreatePlaceholder() => Create(new CodeModeHost
	{
		Tools = static () => [],
		ExecuteTool = static (name, _, _, _, _) => throw new InvalidOperationException($"Tool \"{name}\" is not available in this script"),
	});

	/// <summary>
	/// 构建绑定到会话的工具。<paramref name="declaredTools"/> 是写进描述的工具有集（默认取
	/// <see cref="CodeModeHost.Tools"/>）；执行时始终以宿主当前的工具集为准。
	/// </summary>
	public static BuiltinTool Create(
		CodeModeHost host,
		IReadOnlyList<AgentTool>? declaredTools = null,
		int? inlineBudget = null)
	{
		ArgumentNullException.ThrowIfNull(host);

		return new BuiltinTool(
			new AgentTool
			{
				Name = Name,
				Label = Name,
				Description = CreateDescription(
					declaredTools ?? host.Tools(),
					host.Models is not null,
					inlineBudget,
					host.Guidelines,
					host.ModelCall is not null),
				Parameters = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["code"] = new JsonObject
						{
							["type"] = "string",
							["description"] = "Raw JavaScript source.",
						},
					},
					["required"] = new JsonArray("code"),
				},
				Execute = invocation => ExecuteAsync(invocation, host),
			},
			Snippet,
			Guidelines);
	}

	/// <summary>面向模型的工具描述：固定说明 + 每个可调用工具的声明。</summary>
	public static string CreateDescription(
		IReadOnlyList<AgentTool> tools,
		bool models = true,
		int? inlineBudget = null,
		Func<string, IReadOnlyList<string>>? guidelines = null,
		bool modelCalls = false)
	{
		var callable = CallableTools(tools);
		var globals = models ? $"{DescriptionGlobalsTools}\n{DescriptionGlobalsModels}" : DescriptionGlobalsTools;
		if (modelCalls) globals = $"{globals}\n{DescriptionGlobalsModelCalls}";
		var sections = new List<string> { DescriptionIntro, globals };
		if (callable.Count == 0) return string.Join("\n\n", sections);

		var budget = (inlineBudget ?? DefaultInlineBudget) * CharsPerToken;
		var entries = callable
			.Select(tool =>
			{
				var section = RenderToolSection(tool, guidelines);
				return new CatalogEntry(tool.Name, tool.Namespace, tool.NamespaceDescription, section, (section.Length + CharsPerToken - 1) / CharsPerToken);
			})
			.ToList();

		// 与 pi 一致的目录分组：无命名空间的工具在前，其余按名字；每个组只列预算内最便宜的工具。
		var groups = new List<CatalogGroup>();
		var byNamespace = new Dictionary<string, CatalogGroup>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			var key = entry.Namespace ?? string.Empty;
			if (!byNamespace.TryGetValue(key, out var group))
			{
				group = new CatalogGroup(entry.Namespace, entry.NamespaceDescription);
				byNamespace[key] = group;
				groups.Add(group);
			}

			group.Entries.Add(entry);
		}

		groups = [.. groups.OrderBy(static group => group.Namespace is null ? 0 : 1).ThenBy(static group => group.Namespace, StringComparer.Ordinal)];
		var shown = SelectCatalog(groups, budget);

		var toolSections = new List<string> { "Nested tools:" };
		foreach (var group in groups)
		{
			var visible = group.Entries.Where(entry => shown.Contains(entry.Name)).ToList();
			if (group.Namespace is not null)
			{
				var listing = visible.Count == group.Entries.Count
					? string.Empty
					: visible.Count == 0 ? " (tools not listed)" : " (some tools not listed)";
				var heading = $"## {group.Namespace}{listing}";
				if (!string.IsNullOrWhiteSpace(group.Description))
				{
					heading += $"\n{group.Description.Trim()}";
				}

				toolSections.Add(heading);
			}

			foreach (var entry in visible)
			{
				toolSections.Add(entry.Section);
			}
		}

		sections.Add(string.Join("\n\n", toolSections));
		return string.Join("\n\n", sections);
	}

	/// <summary>目录里的一条工具声明，成本以估算 token 计（字符数 / 4，向上取整）。</summary>
	private sealed record CatalogEntry(string Name, string? Namespace, string? NamespaceDescription, string Section, int Cost);

	private sealed record CatalogGroup(string? Namespace, string? Description)
	{
		public List<CatalogEntry> Entries { get; } = [];
	}

	/// <summary>
	/// 预算选择（pi 的 selectCatalog）：每一轮让每个组放入它最便宜且放得下的工具；
	/// 放不下的组本轮退出，其它组继续。这样每个命名空间都先被代表，再逐步补全。
	/// </summary>
	private static HashSet<string> SelectCatalog(IReadOnlyList<CatalogGroup> groups, int budget)
	{
		var queues = groups
			.Select(group => new Queue<CatalogEntry>(group.Entries.OrderBy(static entry => entry.Cost)))
			.ToList();
		var shown = new HashSet<string>(StringComparer.Ordinal);
		var remaining = budget;
		var active = queues.Where(static queue => queue.Count > 0).ToList();
		while (active.Count > 0)
		{
			var next = new List<Queue<CatalogEntry>>();
			foreach (var queue in active)
			{
				var entry = queue.Peek();
				if (entry.Cost > remaining) continue;

				remaining -= entry.Cost;
				shown.Add(entry.Name);
				queue.Dequeue();
				if (queue.Count > 0) next.Add(queue);
			}

			active = next;
		}

		return shown;
	}

	private static string RenderToolSection(AgentTool tool, Func<string, IReadOnlyList<string>>? guidelines)
	{
		var identifier = CodeModeIdentifier.ToIdentifier(tool.Name);
		var heading = identifier == tool.Name
			? $"### `{identifier}`"
			: $"### `{identifier}` (`{tool.Name}`)";

		var declaration = new CodeModeToolDeclaration(
			tool.Name,
			AppendGuidelines(tool.Description, guidelines?.Invoke(tool.Name)),
			tool.Parameters,
			tool.OutputSchema);
		return $"{heading}\n{declaration.RenderSample().Trim()}";
	}

	/// <summary>单个工具的 TypeScript 声明，供 <c>tool_search</c> 返回加载结果。</summary>
	public static string RenderDeclaration(AgentTool tool, IReadOnlyList<string>? guidelines = null) =>
		new CodeModeToolDeclaration(
			tool.Name,
			AppendGuidelines(tool.Description, guidelines),
			tool.Parameters,
			tool.OutputSchema).RenderSample();

	private static string AppendGuidelines(string description, IReadOnlyList<string>? guidelines)
	{
		if (guidelines is not { Count: > 0 }) return description;
		var bullets = string.Join(
			"\n",
			guidelines
				.Where(static guideline => !string.IsNullOrWhiteSpace(guideline))
				.Select(static guideline => $"- {guideline.Trim()}"));
		return bullets.Length == 0 ? description : $"{description.Trim()}\n\n{bullets}";
	}

	private static IReadOnlyList<AgentTool> CallableTools(IReadOnlyList<AgentTool> tools) =>
		[.. tools.Where(static tool => !string.Equals(tool.Name, Name, StringComparison.Ordinal))];

	private static async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, CodeModeHost host)
	{
		var source = invocation.Arguments["code"]?.GetValue<string>() ?? string.Empty;
		CodeModeSourceOptions sourceOptions;
		ParsedCodeModeSource parsed;
		try
		{
			parsed = CodeModeSource.Parse(source);
			sourceOptions = parsed.Options;
		}
		catch (CodeModeSourceException ex)
		{
			return AgentToolResult.Error(ex.Message);
		}

		var startedAt = Stopwatch.GetTimestamp();
		var calls = new CallLog();
		// 脚本运行中把嵌套调用的快照推给界面；最终结果仍会带完整 details。
		var publish = invocation.OnUpdate is { } onUpdate
			? () => onUpdate(new AgentToolResult { Details = calls.ToDetails() })
			: (Action?)null;
		publish?.Invoke();

		var callable = CallableTools(host.Tools());
		// 消息快照每次执行取一份，全部嵌套调用复用（pi 把同一段会话记录传给所有嵌套上下文）。
		var messages = host.Messages?.Invoke();
		var sandboxTools = new List<JsCodeModeTool>(callable.Count);
		foreach (var tool in callable)
		{
			sandboxTools.Add(ToSandboxTool(tool, host, calls, invocation.ToolCallId, publish, messages));
		}

		var timeout = sourceOptions.TimeoutMs is { } timeoutMs
			? TimeSpan.FromMilliseconds(timeoutMs)
			: DefaultTimeout;

		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Tools = sandboxTools,
			Timeout = timeout,
			Models = host.Models,
			ModelCall = host.ModelCall,
		});

		CodeModeResult result;
		try
		{
			result = await sandbox.ExecuteAsync(parsed.Code, new CodeModeExecuteOptions
			{
				CancellationToken = invocation.CancellationToken,
				Store = host.LoadStore(),
				Timeout = timeout,
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return AgentToolResult.Error("Script aborted");
		}

		if (result.Ok)
		{
			var writes = result.StoreWrites;
			if (writes.Set.Count > 0 || writes.Delete.Count > 0)
			{
				host.SaveStore(writes);
			}
		}

		// 脚本结束/超时/中止时仍在运行的调用按 pi 的约定标成 cancelled。
		var output = result.Output;
		if (result.Ok && result.Value is not null)
		{
			// 返回值与 text() 一样参与输出编号，console 永远在最后。
			output = [.. output, new CodeModeTextItem { Text = ValueText(result.Value) }];
		}

		var items = FormatOutput(output);
		if (!result.Ok && result.Error is { } error)
		{
			items.Add(new TextContent { Text = $"Script error:\n{FormatError(error, calls.Snapshot())}" });
		}

		var maxChars = (sourceOptions.MaxOutputTokens ?? DefaultMaxOutputTokens) * CharsPerToken;
		var truncated = Truncate(items, maxChars, out var fullOutputPath);
		// 图片在文本之后落盘，标注路径不会被截断；模型也能通过 read 拿到字节。
		var withImages = SaveImages(truncated);
		if (result.GeneratedImages > 0 && !withImages.OfType<ImageContent>().Any())
		{
			withImages.Add(new TextContent
			{
				Text = $"Note: models.generateImages() returned {result.GeneratedImages} image{(result.GeneratedImages == 1 ? "" : "s")} that the script did not show. Show each image block of result.output with image(block).",
			});
		}

		var wallTime = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
		var header = $"{(result.Ok ? "Script completed" : "Script failed")}\nWall time {wallTime:0.0} seconds\nOutput:\n";

		var details = calls.ToDetails();
		AppendModelCalls(details, result.ModelCalls, invocation.ToolCallId);
		if (fullOutputPath is not null) details["fullOutputPath"] = fullOutputPath;

		Usage? usage = result.TotalTokens > 0 || result.Cost > 0
			? new Usage
			{
				Input = result.InputTokens,
				Output = result.OutputTokens,
				TotalTokens = result.TotalTokens,
				Cost = new UsageCost { Total = result.Cost },
			}
			: null;

		return new AgentToolResult
		{
			Content = [new TextContent { Text = header }, .. withImages],
			Details = details,
			Usage = usage,
			IsError = !result.Ok,
		};
	}

	/// <summary>把 <c>models.*</c> 调用并入 details 的 <c>calls</c>（与工具调用同一个列表，pi 的渲染形状）。</summary>
	private static void AppendModelCalls(
		JsonObject details,
		IReadOnlyList<CodeModeModelCallRecord> modelCalls,
		string toolCallId)
	{
		if (modelCalls.Count == 0) return;

		var calls = details["calls"] as JsonArray ?? new JsonArray();
		for (var index = 0; index < modelCalls.Count; index++)
		{
			var call = modelCalls[index];
			calls.Add((JsonNode)new JsonObject
			{
				["id"] = $"{toolCallId}{ModelCallIdSuffix}{index + 1}",
				["name"] = call.Name,
				["args"] = call.Model,
				["status"] = call.Status,
				["durationMs"] = call.DurationMs,
				["cost"] = call.Cost is { } cost ? JsonValue.Create(cost) : null,
				["error"] = call.Error,
			});
		}

		details["calls"] = calls;
	}

	private static JsCodeModeTool ToSandboxTool(
		AgentTool tool,
		CodeModeHost host,
		CallLog calls,
		string parentToolCallId,
		Action? publish,
		IReadOnlyList<Message>? messages)
	{
		// 样例只渲染一次：Description（ALL_TOOLS/describeTool 返回的声明）与
		// SearchDescription（检索语料用的原始描述）分开，避免样例套样例。
		var declaration = new JsCodeModeTool
		{
			Name = tool.Name,
			Description = DescribeWithGuidelines(tool, host),
			InputSchema = tool.Parameters,
			OutputSchema = tool.OutputSchema,
			SearchTerms = tool.Label,
			Namespace = tool.Namespace,
			NamespaceAliases = tool.NamespaceAliases,
			NamespaceDescription = tool.NamespaceDescription,
			NamespaceInstructions = tool.NamespaceInstructions,
			Execute = static (_, _) => Task.FromResult<JsonNode?>(null),
		};
		return declaration with
		{
			Description = CodeModeDeclarations.RenderSample(declaration),
			SearchDescription = tool.Description,
			Execute = async (arguments, cancellationToken) =>
			{
				var record = new NestedCall
				{
					Id = calls.NextId(parentToolCallId),
					Name = tool.Name,
					Arguments = Shorten(arguments?.ToJsonString() ?? "null", 200),
				};
				calls.Add(record);
				publish?.Invoke();
				var startedAt = Stopwatch.GetTimestamp();
				try
				{
					if (arguments is not null and not JsonObject)
					{
						throw new InvalidOperationException($"tools.{tool.Name} expects an object argument");
					}

					var args = arguments as JsonObject ?? new JsonObject();
					var outcome = await host.ExecuteTool(tool.Name, args, record.Id, cancellationToken, messages).ConfigureAwait(false);

					// 声明了 outputSchema 的工具把结构化结果交给脚本，错误结果也如此（与 pi 一致）。
					if (tool.OutputSchema is not null && outcome.Result.StructuredContent is { } structured)
					{
						calls.Mutate(record, static call => call.Status = "ok");
						return structured.DeepClone();
					}

					var text = TextOf(outcome.Result);
					if (outcome.IsError)
					{
						// 管线把取消也折成错误结果；被整体取消的调用按 pi 标成 cancelled。
						var cancelled = cancellationToken.IsCancellationRequested;
						calls.Mutate(record, call =>
						{
							call.Status = cancelled ? "cancelled" : "error";
							call.Error = Shorten(text.Length > 0 ? text : $"Tool \"{tool.Name}\" failed", 500);
						});
						throw new InvalidOperationException(text.Length > 0 ? text : $"Tool \"{tool.Name}\" failed");
					}

					calls.Mutate(record, static call => call.Status = "ok");
					return JsonValue.Create(text);
				}
				catch (OperationCanceledException)
				{
					calls.Mutate(record, static call => call.Status = "cancelled");
					throw;
				}
				catch (Exception ex) when (record.Status != "error")
				{
					// 管线抛出（钩子异常、工具异常）也要在详情里留下失败状态。
					calls.Mutate(record, call =>
					{
						call.Status = "error";
						call.Error = Shorten(ex.Message, 500);
					});
					throw;
				}
				finally
				{
					calls.Mutate(record, call => call.DurationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
					publish?.Invoke();
				}
			},
		};
	}

	/// <summary>工具描述 + prompt guidelines；pi 把 guidelines 附在声明后面。</summary>
	private static string DescribeWithGuidelines(AgentTool tool, CodeModeHost host) =>
		AppendGuidelines(tool.Description, host.Guidelines?.Invoke(tool.Name));

	private static string TextOf(AgentToolResult result) =>
		string.Join("\n", result.Content.Select(static block => block switch
		{
			TextContent text => text.Text,
			ImageContent image => $"[image: {image.MimeType}]",
			_ => string.Empty,
		}).Where(static text => text.Length > 0));

	/// <summary>与 pi 相同的排版：多条文本带序号，console 归入一个块。</summary>
	private static List<ContentBlock> FormatOutput(IReadOnlyList<CodeModeOutputItem> output)
	{
		var items = new List<ContentBlock>();
		var consoleLines = new List<string>();
		var total = output.Count(static item => item is CodeModeTextItem { Console: false });
		var index = 0;
		foreach (var item in output)
		{
			switch (item)
			{
				case CodeModeImageItem image:
					items.Add(new ImageContent { Data = image.Data, MimeType = image.MimeType });
					break;
				case CodeModeTextItem { Console: true } console:
					consoleLines.Add(console.Text);
					break;
				case CodeModeTextItem text:
					index++;
					items.Add(new TextContent
					{
						Text = total > 1 ? $"==> text {index}/{total} <==\n{text.Text}" : text.Text,
					});
					break;
			}
		}

		if (consoleLines.Count > 0)
		{
			items.Add(new TextContent { Text = $"<console_output>\n{string.Join("\n", consoleLines)}\n</console_output>" });
		}

		return items;
	}

	private static string ValueText(JsonNode value) =>
		value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();

	private static string FormatError(CodeModeError error, IReadOnlyList<NestedCall> calls)
	{
		var head = error.Kind switch
		{
			CodeModeErrorKind.Script => error.Stack ?? $"{error.Name ?? "Error"}: {error.Message}",
			CodeModeErrorKind.Timeout => $"Script timed out: {error.Message}",
			CodeModeErrorKind.Aborted => $"Script aborted: {error.Message}",
			_ => $"Script sandbox failed: {error.Message}",
		};

		var summary = calls.Count == 0
			? "No tool calls were made."
			: $"Tool calls made before the failure (they are not undone): {string.Join(", ", calls.Select(call => $"{call.Name} ({call.Status})"))}";
		return $"{head}\n\n{summary}";
	}

	/// <summary>按输出预算保留首尾；图片保持在文本之后。预算为 0 时只保留截断提示（与 pi 一致）。</summary>
	private static List<ContentBlock> Truncate(List<ContentBlock> items, int maxChars, out string? fullOutputPath)
	{
		fullOutputPath = null;
		var texts = items.OfType<TextContent>().Select(static text => text.Text).ToList();
		var images = items.OfType<ImageContent>().ToList();
		var combined = string.Join("\n", texts);
		if (combined.Length <= maxChars)
		{
			return items;
		}

		var budget = Math.Max(0, maxChars);
		var headChars = budget / 2;
		var tailChars = budget - headChars;
		var removed = combined.Length - headChars - tailChars;
		var head = combined[..headChars];
		var tail = tailChars > 0 ? combined[^tailChars..] : string.Empty;
		var builder = new StringBuilder();
		builder.AppendLine(
			$"Warning: truncated output (original token count: {(combined.Length + CharsPerToken - 1) / CharsPerToken})");
		// 与 pi 的 combined.split("\n").length 相同的行数，但不为百万级输出分配行数组。
		builder.AppendLine($"Total output lines: {combined.AsSpan().Count('\n') + 1}");
		builder.AppendLine();
		builder.Append(head);
		builder.Append('…');
		builder.Append((removed + CharsPerToken - 1) / CharsPerToken);
		builder.Append(" tokens truncated…");
		builder.Append(tail);

		// 与 pi 一样把完整文本落到临时文件，模型可以再用 read 取回。
		try
		{
			fullOutputPath = WriteFullOutput(combined);
			builder.Append($"\n\n[Full output: {fullOutputPath} (read with offset/limit)]");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			builder.Append($"\n\n[Could not save the full output: {ex.Message}]");
		}

		var result = new List<ContentBlock> { new TextContent { Text = builder.ToString() } };
		result.AddRange(images);
		return result;
	}

	/// <summary>把完整输出写到临时目录，文件名与 pi 的 <c>pi-codemode-*</c> 同形。</summary>
	private static string WriteFullOutput(string text)
	{
		var path = Path.Combine(
			Path.GetTempPath(),
			$"pi-codemode-{Guid.NewGuid().ToString("N")[..12]}.txt");
		File.WriteAllText(path, text, new UTF8Encoding(false));
		return path;
	}

	/// <summary>
	/// 每张图片保存到临时文件并在前面加一行路径说明（pi 的做法）：模型能看到图片，
	/// 但字节只有通过这个文件才可访问。同一张图重复出现只保存一次。
	/// </summary>
	private static List<ContentBlock> SaveImages(List<ContentBlock> items)
	{
		var labels = new Dictionary<string, string>(StringComparer.Ordinal);
		var result = new List<ContentBlock>(items.Count);
		foreach (var item in items)
		{
			if (item is not ImageContent image)
			{
				result.Add(item);
				continue;
			}

			if (!labels.TryGetValue(image.Data, out var label))
			{
				label = SaveImage(image);
				labels[image.Data] = label;
			}

			result.Add(new TextContent { Text = label });
			result.Add(image);
		}

		return result;
	}

	private static string SaveImage(ImageContent image)
	{
		var extension = image.MimeType switch
		{
			"image/png" => ".png",
			"image/jpeg" => ".jpg",
			"image/gif" => ".gif",
			"image/webp" => ".webp",
			_ => null,
		};
		if (extension is null)
		{
			return $"[Image ({image.MimeType}) could not be saved: unsupported image type]";
		}

		try
		{
			var bytes = Convert.FromBase64String(image.Data);
			var kind = $"{image.MimeType}, {PiWeft.CodingAgent.Tools.Truncate.FormatSize(bytes.LongLength)}";
			var path = Path.Combine(
				Path.GetTempPath(),
				$"pi-codemode-{Guid.NewGuid().ToString("N")[..12]}{extension}");
			File.WriteAllBytes(path, bytes);
			return $"[Image saved to {path} ({kind})]";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
		{
			return $"[Image ({image.MimeType}) could not be saved: {ex.Message}]";
		}
	}

	private static string Shorten(string text, int maxChars) =>
		text.Length > maxChars ? $"{text[..(maxChars - 3)]}..." : text;

	/// <summary>详情里的一次嵌套调用；只用于界面展示，不进入上下文。</summary>
	private sealed class NestedCall
	{
		public required string Id { get; init; }

		public required string Name { get; init; }

		public required string Arguments { get; init; }

		public string Status { get; set; } = "running";

		public double? DurationMs { get; set; }

		public string? Error { get; set; }

		public NestedCall Copy() => new()
		{
			Id = Id,
			Name = Name,
			Arguments = Arguments,
			Status = Status,
			DurationMs = DurationMs,
			Error = Error,
		};

		public JsonObject ToJson() => new()
		{
			["id"] = Id,
			["name"] = Name,
			["args"] = Arguments,
			["status"] = Status,
			["durationMs"] = DurationMs is { } duration ? JsonValue.Create(duration) : null,
			["error"] = Error,
		};
	}

	/// <summary>
	/// 嵌套调用记录：脚本线程写入，界面线程读取快照，因此所有访问都经过同一把锁；
	/// 快照会把脚本结束时仍在运行的调用归一化为 <c>cancelled</c>（与 pi 一致）。
	/// </summary>
	private sealed class CallLog
	{
		private readonly List<NestedCall> _calls = [];
		private readonly object _gate = new();

		public string NextId(string parentToolCallId)
		{
			lock (_gate)
			{
				return $"{parentToolCallId}/{_calls.Count + 1}";
			}
		}

		public void Add(NestedCall call)
		{
			lock (_gate)
			{
				_calls.Add(call);
			}
		}

		public void Mutate(NestedCall call, Action<NestedCall> change)
		{
			lock (_gate)
			{
				change(call);
			}
		}

		public IReadOnlyList<NestedCall> Snapshot()
		{
			lock (_gate)
			{
				foreach (var call in _calls)
				{
					if (call.Status == "running") call.Status = "cancelled";
				}

				return [.. _calls.Select(static call => call.Copy())];
			}
		}

		public JsonObject ToDetails()
		{
			var snapshot = Snapshot();
			var calls = new JsonArray();
			foreach (var call in snapshot) calls.Add((JsonNode)call.ToJson());
			return new JsonObject { ["calls"] = calls };
		}
	}

	/// <summary>把 <see cref="AgentTool"/> 投影成声明渲染器需要的形状。</summary>
	private sealed record CodeModeToolDeclaration(
		string ToolName,
		string? Description,
		JsonNode? InputSchema,
		JsonNode? OutputSchema)
	{
		public string RenderSample()
		{
			var tool = new JsCodeModeTool
			{
				Name = ToolName,
				Description = Description,
				InputSchema = InputSchema,
				OutputSchema = OutputSchema ?? new JsonObject { ["type"] = "string" },
				Execute = static (_, _) => Task.FromResult<JsonNode?>(null),
			};
			return CodeModeDeclarations.RenderSample(tool);
		}
	}
}
