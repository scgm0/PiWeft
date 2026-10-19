using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace PiWeft.JavaScript;

/// <summary>
/// 在受限的 Jint 解释器里运行模型写的 JavaScript。每次 <see cref="ExecuteAsync"/> 新建并丢弃引擎，
/// 脚本唯一能碰到宿主的地方是 <see cref="CodeModeTool"/> 暴露的函数；工具结果不进入 LLM 上下文，
/// 进入上下文的只有脚本的输出与返回值。
/// </summary>
public sealed class CodeModeSandbox
{
	/// <summary><c>timeout</c> 为无限时使用的实际值：Jint 只接受有限的正时限。</summary>
	private static readonly TimeSpan MaxEffectiveTimeout = TimeSpan.FromDays(1);

	private static readonly string PreludeSource = CodeModePrelude.Source
		.Replace("__MAX_STORE_VALUE_CHARS__", CodeModePrelude.MaxStoreValueChars.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
		.Replace("__MAX_STORE_TOTAL_CHARS__", CodeModePrelude.MaxStoreTotalChars.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

	private static readonly ResultLimits ConvertLimits = new()
	{
		MaxDepth = 256,
		MaxPropertyCount = 1_000_000,
		MaxStringLength = 64 * 1024 * 1024,
		MaxOutputCharacters = 128L * 1024 * 1024,
		MaxOutputBytes = 256L * 1024 * 1024,
	};

	private readonly CodeModeSandboxOptions _options;

	public CodeModeSandbox(CodeModeSandboxOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	/// <summary>本次执行暴露给脚本的工具。</summary>
	public IReadOnlyList<CodeModeTool> Tools => _options.Tools;

	/// <summary>
	/// 运行一段脚本。脚本失败不会抛异常，而是通过 <see cref="CodeModeResult.Error"/> 报告。
	/// 解释器跑在独立的后台线程上（不占用调用线程与线程池），工具落定后脚本的续体也回到该线程。
	/// </summary>
	public Task<CodeModeResult> ExecuteAsync(string code, CodeModeExecuteOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(code);
		options ??= new CodeModeExecuteOptions();

		// 与 pi 相同的进程级并发上限：允许一个替补在被取消的 worker 退出期间启动。
		if (Interlocked.Increment(ref _activeWorkers) > MaxConcurrentWorkers)
		{
			Interlocked.Decrement(ref _activeWorkers);
			throw new InvalidOperationException(
				$"Codemode worker limit reached ({MaxConcurrentWorkers} workers are still running or shutting down). Retry after they exit.");
		}

		var worker = new CodeModeWorker();
		worker.Start(() =>
		{
			try
			{
				return ExecuteCore(worker, code, options);
			}
			finally
			{
				Interlocked.Decrement(ref _activeWorkers);
			}
		});
		return worker.Completion;
	}

	/// <summary>同时运行的 codemode 解释器上限，与 pi 的 <c>MAX_CONCURRENT_CODEMODE_WORKERS</c> 一致。</summary>
	private const int MaxConcurrentWorkers = 2;

	private static int _activeWorkers;

	private CodeModeResult ExecuteCore(CodeModeWorker worker, string code, CodeModeExecuteOptions options)
	{
		var output = new OutputCollector(_options.MaxOutputChars, _options.MaxOutputItems);
		var calls = new List<CallRecord>();
		var toolsByName = new Dictionary<string, CodeModeTool>(StringComparer.Ordinal);
		var toolsByIdentifier = new Dictionary<string, CodeModeTool>(StringComparer.Ordinal);
		foreach (var tool in _options.Tools)
		{
			toolsByName.TryAdd(tool.Name, tool);
			toolsByIdentifier.TryAdd(CodeModeIdentifier.ToIdentifier(tool.Name), tool);
		}

		var timeout = options.Timeout ?? _options.Timeout;
		// Jint 只接受 (0, TimeSpan.MaxValue) 内的时限：无限的与过小的值都夹到可用区间。
		var effectiveTimeout = timeout == Timeout.InfiniteTimeSpan || timeout > MaxEffectiveTimeout
			? MaxEffectiveTimeout
			: timeout < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : timeout;

		using var deadline = new CancellationTokenSource();
		if (timeout != Timeout.InfiniteTimeSpan)
		{
			deadline.CancelAfter(effectiveTimeout);
		}

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, deadline.Token);
		var token = linked.Token;
		string? outputLimitError = null;

		var engineOptions = CreateEngineOptions(effectiveTimeout);
		using var engine = new Engine(engineOptions);
		string? finishedJson = null;
		var pendingCalls = 0;
		engine.SetValue("__cmFinish", new Action<string>(json => finishedJson = json));

		engine.SetValue("__cmOutput", new Action<string, string, JsValue>((kind, text, mimeType) =>
		{
			var mime = mimeType.IsUndefined() || mimeType.IsNull() ? null : mimeType.AsString();
			if (output.TryAdd(kind, text, mime)) return;

			// 超出输出预算后脚本整体失败：即使它 catch 了这里抛出的错误，结果也会被覆盖。
			outputLimitError = output.LimitMessage;
			throw new InvalidOperationException(output.LimitMessage!);
		}));

		engine.SetValue("__cmCall", new Func<string, string, JsValue>((name, argumentsJson) =>
		{
			var manual = engine.Tasks.RegisterPromise();
			if (!toolsByName.TryGetValue(name, out var tool))
			{
				manual.Reject($"Tool \"{name}\" is not available in this script");
				return manual.Promise;
			}

			var call = new CallRecord(name);
			calls.Add(call);
			Interlocked.Increment(ref pendingCalls);
			_ = InvokeToolAsync(tool, argumentsJson, call, manual, token, worker, () => Interlocked.Decrement(ref pendingCalls));
			return manual.Promise;
		}));

		var modelCalls = new List<ModelCallRecord>();
		var modelSlots = new SemaphoreSlim(4);
		var modelUsageGate = new object();
		long inputTokens = 0, outputTokens = 0, totalTokens = 0;
		double modelCost = 0;
		var generatedImages = 0;

		engine.SetValue("__cmModelCall", new Func<string, string, string, string, JsValue>((operation, provider, id, contextJson) =>
		{
			var manual = engine.Tasks.RegisterPromise();
			if (_options.ModelCall is not { } modelCall)
			{
				manual.Reject($"{operation} is not available in this script");
				return manual.Promise;
			}

			var record = new ModelCallRecord
			{
				Name = operation == "classify" ? "models.classify" : "models.generateImages",
				Model = $"{provider}/{id}",
			};
			lock (modelCalls) modelCalls.Add(record);
			Interlocked.Increment(ref pendingCalls);
			_ = InvokeModelAsync(modelCall, operation, provider, id, contextJson, record, manual, token);
			return manual.Promise;
		}));

		async Task InvokeModelAsync(
			Func<CodeModeModelCall, CancellationToken, Task<CodeModeModelCallResult>> modelCall,
			string operation,
			string provider,
			string id,
			string contextJson,
			ModelCallRecord record,
			Jint.Native.Promise.ManualPromise manual,
			CancellationToken cancellationToken)
		{
			var startedAt = Stopwatch.GetTimestamp();
			try
			{
				await modelSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					var context = ParseContextJson(contextJson);
					var result = await modelCall(new CodeModeModelCall
					{
						Operation = operation,
						Provider = provider,
						Id = id,
						Context = context,
					}, cancellationToken).ConfigureAwait(false);

					record.Status = result.Status;
					record.Cost = result.Cost;
					record.Error = result.Error;
					lock (modelUsageGate)
					{
						inputTokens += result.InputTokens;
						outputTokens += result.OutputTokens;
						totalTokens += result.TotalTokens;
						modelCost += result.Cost;
					}

					if (result.GeneratedImages > 0) Interlocked.Add(ref generatedImages, result.GeneratedImages);

					var json = result.Output?.ToJsonString() ?? "null";
					worker.Post(() => TrySettle(() => manual.Resolve(json)));
				}
				finally
				{
					modelSlots.Release();
				}
			}
			catch (OperationCanceledException)
			{
				record.Status = "cancelled";
				if (!cancellationToken.IsCancellationRequested)
				{
					worker.Post(() => TrySettle(() => manual.Reject("The model call was cancelled")));
				}
			}
			catch (Exception ex)
			{
				record.Status = "error";
				record.Error = ex.Message;
				var message = ex.Message;
				worker.Post(() => TrySettle(() => manual.Reject(message)));
			}
			finally
			{
				record.DurationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
				Interlocked.Decrement(ref pendingCalls);
			}
		}

		(List<CodeModeModelCallRecord> Calls, long Input, long Output, long Total, double Cost, int Images) SnapshotModels()
		{
			List<CodeModeModelCallRecord> snapshot;
			lock (modelCalls)
			{
				foreach (var call in modelCalls)
				{
					if (call.Status == "running") call.Status = "cancelled";
				}

				snapshot = [.. modelCalls.Select(static call => call.ToRecord())];
			}

			lock (modelUsageGate)
			{
				return (snapshot, inputTokens, outputTokens, totalTokens, modelCost, Volatile.Read(ref generatedImages));
			}
		}

		// 检索语料按需构建：脚本没有调用 searchTools/describeNamespace 时，不做 schema 遍历与分词。
		List<SearchDocument>? searchDocuments = null;
		List<SearchDocument> SearchDocuments()
		{
			if (searchDocuments is not null) return searchDocuments;
			searchDocuments = new List<SearchDocument>(_options.Tools.Count);
			foreach (var tool in _options.Tools)
			{
				searchDocuments.Add(new SearchDocument(
					CodeModeIdentifier.ToIdentifier(tool.Name),
					tool.Name,
					CodeModeSearch.Tokenize(SearchText(tool)),
					tool.Namespace,
					tool.NamespaceAliases));
			}

			return searchDocuments;
		}

		engine.SetValue("__cmSearch", new Func<string, int, string?, string>((query, limit, namespaceName) =>
		{
			var documents = SearchDocuments();
			List<SearchDocument> filtered;
			if (string.IsNullOrEmpty(namespaceName))
			{
				filtered = documents;
			}
			else
			{
				filtered = new List<SearchDocument>(documents.Count);
				foreach (var document in documents)
				{
					if (NamespaceMatches(document.Namespace, document.NamespaceAliases, namespaceName)) filtered.Add(document);
				}
			}

			var ranked = CodeModeSearch.RankTerms(
				query,
				[.. filtered.Select(static document => (document.Name, document.Terms))],
				limit);
			var matches = new JsonArray();
			foreach (var name in ranked)
			{
				var document = filtered.First(candidate => candidate.Name == name);
				matches.Add((JsonNode)new JsonObject
				{
					["name"] = document.Name,
					["description"] = toolsByName.TryGetValue(document.RawName, out var tool) ? tool.Description : null,
				});
			}

			return matches.ToJsonString();
		}));

		engine.SetValue("__cmDescribeNamespace", new Func<string, string?>(name =>
		{
			CodeModeTool? first = null;
			var tools = new JsonArray();
			foreach (var tool in _options.Tools)
			{
				if (!NamespaceMatches(tool.Namespace, tool.NamespaceAliases, name)) continue;
				first ??= tool;
				tools.Add((JsonNode?)JsonValue.Create(CodeModeIdentifier.ToIdentifier(tool.Name)));
			}

			if (first is null) return null;

			var payload = new JsonObject { ["name"] = first.Namespace, ["tools"] = tools };
			if (first.NamespaceAliases is { Count: > 0 } aliases)
			{
				var aliasArray = new JsonArray();
				foreach (var alias in aliases) aliasArray.Add((JsonNode?)JsonValue.Create(alias));
				payload["aliases"] = aliasArray;
			}

			if (!string.IsNullOrWhiteSpace(first.NamespaceDescription))
			{
				payload["description"] = first.NamespaceDescription;
			}

			if (!string.IsNullOrWhiteSpace(first.NamespaceInstructions))
			{
				payload["instructions"] = first.NamespaceInstructions;
			}

			return payload.ToJsonString();
		}));

		engine.SetValue("__cmDescribe", new Func<string, JsValue>(name =>
		{
			if (!toolsByName.TryGetValue(name, out var tool)
				&& !toolsByIdentifier.TryGetValue(CodeModeIdentifier.ToIdentifier(name), out tool))
			{
				return JsValue.Undefined;
			}

			return JsValue.FromObject(engine, tool.Description);
		}));

		engine.SetValue("__cmModels", new Func<string, string?, string?, string?, JsValue>((operation, type, provider, id) =>
		{
			var query = new CodeModeModelsRequest
			{
				Operation = operation switch
				{
					"list" => CodeModeModelOperation.List,
					"available" => CodeModeModelOperation.Available,
					"get" => CodeModeModelOperation.Get,
					_ => throw new ArgumentException($"Unknown models operation \"{operation}\""),
				},
				Type = type ?? string.Empty,
				Provider = string.IsNullOrEmpty(provider) ? null : provider,
				Id = string.IsNullOrEmpty(id) ? null : id,
			};

			var models = _options.Models
				?? throw new InvalidOperationException("models.* is not available in this script");
			var result = models(query);
			return result is null ? JsValue.Undefined : JsValue.FromObject(engine, result.ToJsonString());
		}));

		try
		{
			engine.SetValue("__cmToolsJson", BuildToolsJson());
			engine.SetValue("__cmStoreJson", BuildStoreJson(options.Store));
			var runtime = engine.Evaluate(PreludeSource + "(__cmToolsJson, __cmStoreJson)", "codemode-prelude.js");
			engine.SetValue("__cmRuntime", runtime);

			// 包装前缀与脚本首行同一行，行号与输入完全一致（与 pi 相同）。
			// 用 async 箭头而不是 async function：箭头没有自己的 arguments 绑定，
			// Jint 就不会在含 eval 的函数里走 argumentsList.ToArray() —— 那条路在 NativeAOT 下会
			// 撞上 Jint 的泛型接口分派缺陷（RhpCidResolve 直接 FailFast）。
			var scriptFn = engine.Evaluate("(async () => {" + code + "\n})", "codemode.js");
			engine.SetValue("__cmScriptFn", scriptFn);

			// 不用 EvaluateAsync：它在每个 await 之后把续体交给线程池。这里只启动 async 函数，
			// 由 worker 线程自己泵引擎的任务队列，脚本的全部代码都在同一个线程上执行。
			engine.Evaluate("__cmRuntime.run(__cmScriptFn)", "codemode.js");
			// 取消（时限/调用方中止）时唤醒等待中的 worker：无需 1ms 轮询。
			using (token.Register(static state => ((CodeModeWorker)state!).Wake(), worker))
			{
				while (finishedJson is null)
				{
					token.ThrowIfCancellationRequested();
					worker.Drain();
					engine.Tasks.ProcessTasks();
					if (finishedJson is not null) break;

					// 没有挂起的工具/落定/定时任务：这个 Promise 永远不会落定（与 pi 的 stalled 检测一致）。
					if (Volatile.Read(ref pendingCalls) == 0
						&& !worker.HasPendingWork
						&& engine.Tasks.TimeUntilNextScheduledWork is null)
					{
						throw new CodeModeStalledException(
							"The script is waiting on a promise that can never settle: no tool call is pending, and timers do not exist here.");
					}

					worker.WaitForWork(Timeout.InfiniteTimeSpan);
				}
			}

			var result = ParseResult(finishedJson, calls, outputLimitError);
			var models = SnapshotModels();
			return result with
			{
				Output = output.Items,
				ModelCalls = models.Calls,
				InputTokens = models.Input,
				OutputTokens = models.Output,
				TotalTokens = models.Total,
				Cost = models.Cost,
				GeneratedImages = models.Images,
			};
		}
		catch (Exception ex)
		{
			var error = MapException(ex, deadline.Token, options.CancellationToken, outputLimitError);
			var models = SnapshotModels();
			return new CodeModeResult
			{
				Ok = false,
				Error = error,
				Output = output.Items,
				Calls = SnapshotCalls(calls),
				ModelCalls = models.Calls,
				InputTokens = models.Input,
				OutputTokens = models.Output,
				TotalTokens = models.Total,
				Cost = models.Cost,
				GeneratedImages = models.Images,
			};
		}
		finally
		{
			// 脚本结束时仍未完成的调用一律取消（与 pi 的 "cancelled" 语义一致）。
			linked.Cancel();
		}
	}

	/// <summary>脚本在等待一个永远不会落定的 Promise：没有挂起的工具，也没有定时器。</summary>
	private sealed class CodeModeStalledException(string message) : Exception(message);

	/// <summary>执行中的 <c>models.*</c> 调用；结束后转成只读的 <see cref="CodeModeModelCallRecord"/>。</summary>
	private sealed class ModelCallRecord
	{
		public required string Name { get; init; }

		public required string Model { get; init; }

		public string Status { get; set; } = "running";

		public double DurationMs { get; set; }

		public double? Cost { get; set; }

		public string? Error { get; set; }

		public CodeModeModelCallRecord ToRecord() => new()
		{
			Name = Name,
			Model = Model,
			Status = Status,
			DurationMs = DurationMs,
			Cost = Cost,
			Error = Error,
		};
	}

	private static JsonNode? ParseContextJson(string json)
	{
		if (string.IsNullOrEmpty(json) || json == "undefined" || json == "null") return null;
		return JsonNode.Parse(json);
	}

	/// <summary>
	/// 检索语料（与 pi 的 <c>createToolSearchDocument</c> 一致）：工具名、原名（<c>_</c> 换空格）、
	/// 原始描述、schema 里的说明与属性名，以及命名空间的名称/描述/instructions。
	/// </summary>
	private static string SearchText(CodeModeTool tool)
	{
		var text = CodeModeSearch.CreateDocument(tool.Name, tool.SearchDescription ?? tool.Description, tool.InputSchema);
		if (tool.Namespace is null) return text;

		var extra = string.Join(
			" ",
			new[] { tool.Namespace, tool.NamespaceDescription, tool.NamespaceInstructions }
				.Where(static part => !string.IsNullOrWhiteSpace(part)));
		return extra.Length == 0 ? text : $"{text} {extra}";
	}

	/// <summary>
	/// 命名空间匹配：名称或别名，按原名或脚本标识符形式比较
	/// （<c>dev-radius</c> 与 <c>dev_radius</c> 等价）。
	/// </summary>
	private static bool NamespaceMatches(string? namespaceName, IReadOnlyList<string>? aliases, string query)
	{
		if (namespaceName is not null && NamespaceNameMatches(namespaceName, query)) return true;
		if (aliases is null) return false;
		foreach (var alias in aliases)
		{
			if (NamespaceNameMatches(alias, query)) return true;
		}

		return false;
	}

	private static bool NamespaceNameMatches(string value, string query) =>
		string.Equals(value, query, StringComparison.Ordinal)
			|| string.Equals(
				CodeModeIdentifier.ToIdentifier(value),
				CodeModeIdentifier.ToIdentifier(query),
				StringComparison.Ordinal);

	/// <summary>检索文档：<see cref="Terms"/> 在首次检索时已分词，多次 <c>searchTools()</c> 复用。</summary>
	private sealed record SearchDocument(
		string Name,
		string RawName,
		IReadOnlyList<string> Terms,
		string? Namespace,
		IReadOnlyList<string>? NamespaceAliases);

	/// <summary>
	/// 与 <c>ForUntrustedCode</c> 相同的资源预算，但不启用它的「不可变配置快照」：那个配置会永久
	/// 关闭 eval/new Function，而 pi 的 QuickJS 允许它们。这里改成手动搭建：不注册 CLR 暴露、
	/// 模块加载或调试器，只关闭包装对象的反射/写入口，再加上同样的时限、语句与内存约束。
	/// eval 生成的源码同样受 MaxSourceLength/MaxNodeCount 与语句预算约束。
	/// </summary>
	private Options CreateEngineOptions(TimeSpan timeout)
	{
		var options = new Options
		{
			Strict = false,
			ResultLimits = ConvertLimits,
		};

		options.LimitExecutionTime(timeout);
		options.LimitStatements(_options.MaxStatements);
		options.LimitMemory(_options.MemoryLimitBytes);

		options.Constraints.MaxRecursionDepth = 128;
		options.Constraints.MaxArraySize = 1_000_000;
		options.Constraints.RegexTimeout = TimeSpan.FromSeconds(1);
		// PromiseTimeout 必须大于 0；脚本等待工具的时间由整体时限覆盖。
		options.Constraints.PromiseTimeout = timeout;
		options.Constraints.StackOverflowGuard = true;

		options.Parsing.MaxSourceLength = _options.MaxSourceLength;
		options.Parsing.MaxNodeCount = 250_000;

		options.Host.StringCompilationAllowed = true;

		options.Interop.AllowGetType = false;
		options.Interop.AllowSystemReflection = false;
		options.Interop.AllowWrite = false;
		options.Interop.AllowOperatorOverloading = false;
		return options;
	}

	private string BuildToolsJson()
	{
		var array = new JsonArray();
		foreach (var tool in _options.Tools)
		{
			array.Add((JsonNode)new JsonObject
			{
				["name"] = tool.Name,
				["jsName"] = CodeModeIdentifier.ToIdentifier(tool.Name),
				// Description 已经是完整样例（由工具工厂渲染一次），ALL_TOOLS 与 describeTool() 直接用。
				["description"] = tool.Description,
			});
		}

		return array.ToJsonString();
	}

	private static string BuildStoreJson(IReadOnlyDictionary<string, JsonNode?> store)
	{
		var values = new JsonObject();
		foreach (var (key, value) in store)
		{
			// store() 读到的必须是 JSON 文本，与脚本里的 store(key, value) 一致。
			values[key] = value?.ToJsonString() ?? "null";
		}

		return values.ToJsonString();
	}

	private static async Task InvokeToolAsync(
		CodeModeTool tool,
		string argumentsJson,
		CallRecord call,
		Jint.Native.Promise.ManualPromise manual,
		CancellationToken cancellationToken,
		CodeModeWorker worker,
		Action onSettled)
	{
		var startedAt = Stopwatch.GetTimestamp();
		try
		{
			var arguments = ParseArguments(argumentsJson);
			var value = await tool.Execute(arguments, cancellationToken).ConfigureAwait(false);
			call.Status = CodeModeCallStatus.Ok;
			// 落定回到 worker 线程，脚本的续体才与解释器同线程；引擎可能已销毁，失败不再抛出。
			var text = value is null ? "null" : value.ToJsonString();
			worker.Post(() => TrySettle(() => manual.Resolve(text)));
		}
		catch (OperationCanceledException)
		{
			call.Status = CodeModeCallStatus.Cancelled;
			// 整体取消时不落定：pump 循环会先看到 token，结果按 timeout/aborted 报告。
			if (!cancellationToken.IsCancellationRequested)
			{
				worker.Post(() => TrySettle(() => manual.Reject("The tool call was cancelled")));
			}
		}
		catch (Exception ex)
		{
			call.Status = CodeModeCallStatus.Error;
			var message = ex.Message;
			worker.Post(() => TrySettle(() => manual.Reject(message)));
		}
		finally
		{
			call.DurationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
			onSettled();
		}
	}

	private static void TrySettle(Action settle)
	{
		try
		{
			settle();
		}
		catch (Exception)
		{
			// 引擎已释放或调用已被取消：这次落定没有接收方。
		}
	}

	private static JsonNode? ParseArguments(string argumentsJson)
	{
		if (string.IsNullOrEmpty(argumentsJson) || argumentsJson == "undefined") return null;
		try
		{
			return JsonNode.Parse(argumentsJson);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static CodeModeResult ParseResult(
		string raw,
		List<CallRecord> calls,
		string? outputLimitError)
	{
		JsonObject? payload;
		try
		{
			payload = JsonNode.Parse(raw) as JsonObject;
		}
		catch (JsonException ex)
		{
			return Failure(new CodeModeError
			{
				Kind = CodeModeErrorKind.Sandbox,
				Message = $"The sandbox returned an unreadable result: {ex.Message}",
			}, calls);
		}

		var snapshot = SnapshotCalls(calls);
		if (outputLimitError is not null)
		{
			return new CodeModeResult
			{
				Ok = false,
				Error = new CodeModeError { Kind = CodeModeErrorKind.Script, Name = "RangeError", Message = outputLimitError },
				Calls = snapshot,
			};
		}

		if (payload?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var succeeded) && succeeded)
		{
			var valueJson = payload["valueJson"]?.GetValue<string>();
			return new CodeModeResult
			{
				Ok = true,
				Value = string.IsNullOrEmpty(valueJson) ? null : JsonNode.Parse(valueJson),
				Calls = snapshot,
				StoreWrites = ParseWrites(payload["writesJson"]?.GetValue<string>()),
			};
		}

		var error = ParseScriptError(payload?["errorJson"]?.GetValue<string>());
		return new CodeModeResult { Ok = false, Error = error, Calls = snapshot };
	}

	private static CodeModeError ParseScriptError(string? errorJson)
	{
		if (string.IsNullOrEmpty(errorJson))
		{
			return new CodeModeError { Kind = CodeModeErrorKind.Sandbox, Message = "The script failed without an error" };
		}

		try
		{
			var obj = JsonNode.Parse(errorJson) as JsonObject;
			return new CodeModeError
			{
				Kind = CodeModeErrorKind.Script,
				Name = obj?["name"]?.GetValue<string>() ?? "Error",
				Message = obj?["message"]?.GetValue<string>() ?? "The script failed",
				Stack = obj?["stack"]?.GetValue<string>(),
			};
		}
		catch (JsonException)
		{
			return new CodeModeError { Kind = CodeModeErrorKind.Script, Message = errorJson };
		}
	}

	private static CodeModeStoreWrites ParseWrites(string? writesJson)
	{
		if (string.IsNullOrEmpty(writesJson)) return CodeModeStoreWrites.Empty;

		var set = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
		var delete = new List<string>();
		try
		{
			if (JsonNode.Parse(writesJson) is not JsonArray entries) return CodeModeStoreWrites.Empty;
			foreach (var entry in entries)
			{
				if (entry is not JsonArray pair || pair.Count == 0) continue;
				var key = pair[0]?.GetValue<string>();
				if (key is null) continue;
				if (pair.Count == 1)
				{
					delete.Add(key);
					continue;
				}

				// 写入值是 JSON 文本，还原成节点。
				var json = pair[1]?.GetValue<string>();
				set[key] = string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
			}
		}
		catch (JsonException)
		{
			return CodeModeStoreWrites.Empty;
		}

		return new CodeModeStoreWrites { Set = set, Delete = delete };
	}

	private static IReadOnlyList<CodeModeCall> SnapshotCalls(List<CallRecord> calls) =>
		[.. calls.Select(call => new CodeModeCall
		{
			Name = call.Name,
			Status = call.Status,
			DurationMs = call.DurationMs,
		})];

	private static CodeModeResult Failure(CodeModeError error, List<CallRecord> calls) =>
		new() { Ok = false, Error = error, Calls = SnapshotCalls(calls) };

	private static CodeModeError MapException(
		Exception exception,
		CancellationToken deadlineToken,
		CancellationToken callerToken,
		string? outputLimitError)
	{
		if (outputLimitError is not null)
		{
			return new CodeModeError { Kind = CodeModeErrorKind.Script, Name = "RangeError", Message = outputLimitError };
		}

		if (callerToken.IsCancellationRequested)
		{
			return new CodeModeError { Kind = CodeModeErrorKind.Aborted, Message = "The script was aborted" };
		}

		if (deadlineToken.IsCancellationRequested)
		{
			return new CodeModeError { Kind = CodeModeErrorKind.Timeout, Message = "The script timed out" };
		}

		return exception switch
		{
			JavaScriptException js => new CodeModeError
			{
				Kind = CodeModeErrorKind.Script,
				Name = "Error",
				Message = js.Message,
				Stack = js.JavaScriptStackTrace,
			},
			StatementsCountOverflowException => new CodeModeError
			{
				Kind = CodeModeErrorKind.Timeout,
				Message = "The script exceeded the statement limit",
			},
			RecursionDepthOverflowException => new CodeModeError
			{
				Kind = CodeModeErrorKind.Script,
				Name = "RangeError",
				Message = "Maximum call stack size exceeded",
			},
			MemoryLimitExceededException => new CodeModeError
			{
				Kind = CodeModeErrorKind.Script,
				Name = "InternalError",
				Message = "out of memory",
			},
			CodeModeStalledException stalled => new CodeModeError
			{
				Kind = CodeModeErrorKind.Script,
				Name = "Error",
				Message = stalled.Message,
			},
			TimeoutException or ExecutionCanceledException or OperationCanceledException => new CodeModeError
			{
				Kind = CodeModeErrorKind.Timeout,
				Message = "The script timed out",
			},
			_ => new CodeModeError
			{
				Kind = CodeModeErrorKind.Sandbox,
				Message = $"{exception.GetType().Name}: {exception.Message}",
			},
		};
	}

	/// <summary>
	/// 承载一次执行的后台线程。同步解释、宿主回调与工具落定都在这个线程上排队执行，
	/// 调用方只等待 <see cref="Completion"/>；线程不取自线程池，避免 CPU 密集脚本饿死线程池。
	/// </summary>
	private sealed class CodeModeWorker
	{
		private readonly ConcurrentQueue<Action> _queue = new();
		// 托管信号量：不占内核句柄，也不需要随执行结束释放。
		private readonly SemaphoreSlim _signal = new(0);
		private readonly TaskCompletionSource<CodeModeResult> _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<CodeModeResult> Completion => _completion.Task;

		public void Start(Func<CodeModeResult> body)
		{
			var thread = new Thread(() =>
			{
				try
				{
					_completion.TrySetResult(body());
				}
				catch (Exception ex)
				{
					_completion.TrySetException(ex);
				}
			})
			{
				IsBackground = true,
				Name = "piweft-codemode",
			};
			thread.Start();
		}

		/// <summary>从工具续体投递一次落定；执行结束后投递的项无人处理，也不会抛错。</summary>
		public void Post(Action action)
		{
			_queue.Enqueue(action);
			ReleaseSignal();
		}

		/// <summary>取消等场景下唤醒等待中的 worker；重复唤醒只是多一次空转。</summary>
		public void Wake() => ReleaseSignal();

		private void ReleaseSignal()
		{
			try
			{
				_signal.Release();
			}
			catch (SemaphoreFullException)
			{
				// 已有足够唤醒信号在排队。
			}
		}

		/// <summary>队列里还有未处理的落定。</summary>
		public bool HasPendingWork => !_queue.IsEmpty;

		/// <summary>等待新的落定；<see cref="Post"/> 与取消注册都会唤醒。</summary>
		public void WaitForWork(TimeSpan timeout) => _signal.Wait(timeout);

		public void Drain()
		{
			while (_queue.TryDequeue(out var action)) action();
		}
	}

	private sealed class CallRecord(string name)
	{
		public string Name { get; } = name;

		public CodeModeCallStatus Status { get; set; } = CodeModeCallStatus.Cancelled;

		public double DurationMs { get; set; }
	}

	private sealed class OutputCollector(long maxChars, int maxItems)
	{
		private readonly List<CodeModeOutputItem> _items = [];
		private long _chars;

		public IReadOnlyList<CodeModeOutputItem> Items => _items;

		public string? LimitMessage { get; private set; }

		public bool TryAdd(string kind, string text, string? mimeType)
		{
			if (LimitMessage is not null) return false;

			_chars += text.Length;
			if (_chars > maxChars || _items.Count >= maxItems)
			{
				LimitMessage =
					$"script output exceeded the limit of {maxChars} characters or {maxItems} text(), image(), and console calls. " +
					"Print a summary instead, or write large data to a file with a tool.";
				return false;
			}

			_items.Add(kind == "image"
				? new CodeModeImageItem { Data = text, MimeType = mimeType ?? "image/png" }
				: new CodeModeTextItem { Text = text, Console = kind == "console" });
			return true;
		}
	}
}
