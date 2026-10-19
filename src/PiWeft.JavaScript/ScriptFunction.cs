using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace PiWeft.JavaScript;

/// <summary>一次受限 JS 函数调用的预算。</summary>
public sealed record ScriptFunctionOptions
{
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

	public long MemoryLimitBytes { get; init; } = 64L * 1024 * 1024;

	public int MaxStatements { get; init; } = 500_000;

	public int MaxSourceLength { get; init; } = 256 * 1024;
}

/// <summary>
/// 运行一段 async 函数体：<c>return ...</c> 的值会作为 JSON 返回。
/// 脚本只拿到两个 JSON 值（<c>request</c> 与 <c>context</c>），没有 CLR、文件、网络或模块入口。
/// 用于 <c>models.json</c> 的虚拟模型 <c>route</c> 等宿主内钩子；每次调用新建解释器。
/// </summary>
public static class ScriptFunction
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static Task<JsonNode?> InvokeAsync(
		string body,
		JsonNode? request,
		JsonNode? context,
		ScriptFunctionOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(body);
		options ??= new ScriptFunctionOptions();
		return Task.Run(() => InvokeCore(body, request, context, options, cancellationToken), cancellationToken);
	}

	private static JsonNode? InvokeCore(
		string body,
		JsonNode? request,
		JsonNode? context,
		ScriptFunctionOptions options,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(options.Timeout);

		var engineOptions = new Options
		{
			Strict = false,
		};
		engineOptions.LimitExecutionTime(options.Timeout);
		engineOptions.LimitStatements(options.MaxStatements);
		engineOptions.LimitMemory(options.MemoryLimitBytes);
		engineOptions.Constraints.MaxRecursionDepth = 64;
		engineOptions.Constraints.MaxArraySize = 100_000;
		engineOptions.Constraints.RegexTimeout = TimeSpan.FromSeconds(1);
		engineOptions.Constraints.PromiseTimeout = options.Timeout;
		engineOptions.Constraints.StackOverflowGuard = true;
		engineOptions.Parsing.MaxSourceLength = options.MaxSourceLength;
		engineOptions.Parsing.MaxNodeCount = 100_000;
		engineOptions.Host.StringCompilationAllowed = true;
		engineOptions.Interop.AllowGetType = false;
		engineOptions.Interop.AllowSystemReflection = false;
		engineOptions.Interop.AllowWrite = false;
		engineOptions.Interop.AllowOperatorOverloading = false;

		using var engine = new Engine(engineOptions);
		var requestJson = request?.ToJsonString(JsonOptions) ?? "null";
		var contextJson = context?.ToJsonString(JsonOptions) ?? "null";

		// async 箭头函数：AOT 下比 async function 更安全（与 codemode 的包装一致）。
		var factory = engine.Evaluate(
			"(function (requestJson, contextJson) {" +
			"const request = JSON.parse(requestJson);" +
			"const context = JSON.parse(contextJson);" +
			"return (async (request, ctx) => { const context = ctx;" + body + "\n})(request, context);" +
			"})",
			"pi-script-function.js");

		var promise = engine.Call(
			factory,
			JsValue.Undefined,
			[JsValue.FromObject(engine, requestJson), JsValue.FromObject(engine, contextJson)]);
		var value = promise.UnwrapIfPromise(timeout.Token);
		if (value.IsUndefined() || value.IsNull())
		{
			return null;
		}

		var stringify = engine.Evaluate("(function (value) { return JSON.stringify(value); })", "pi-script-function.js");
		var text = engine.Call(stringify, JsValue.Undefined, [value]);
		if (text.IsUndefined() || text.IsNull())
		{
			return null;
		}

		return JsonNode.Parse(text.AsString());
	}
}
