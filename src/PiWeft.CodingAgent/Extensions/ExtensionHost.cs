using System.Text.Json.Nodes;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;
using PiWeft.Agent;

namespace PiWeft.CodingAgent.Extensions;

/// <summary>一个已加载扩展的状态。</summary>
public sealed record ExtensionState
{
	required public ExtensionDescriptor Descriptor { get; init; }

	/// <summary>加载与运行期间的错误，逐条给人看。</summary>
	public List<string> Diagnostics { get; } = [];

	public bool Loaded { get; set; }

	internal ExtensionWorker? Worker { get; set; }

	internal Engine? Engine { get; set; }

	internal ExtensionRegistration Registration { get; } = new();

}

/// <summary>
/// 进程内 JS 扩展宿主：发现 → 沙箱执行 → 收集注册项（工具、命令、事件钩子）→ 派发事件。
/// <para>
/// 沙箱与 codemode 同一套纪律：Jint 解释器不给扩展任何宿主能力（无 <c>fs</c>、无 <c>net</c>、无 <c>require</c>），
/// 扩展唯一能碰到外面的通道是显式注册的 <c>__pi*</c> 宿主函数，<c>pi</c> 对象本身由这些函数在 JS 侧组装
/// （与 <c>CodeModePrelude</c> 同一手法）。所有桥用 <see cref="ClrFunction"/> 注册：不走反射包装，
/// 因而在 NativeAOT/裁剪下不产生 IL2026/IL2111。
/// </para>
/// <para>
/// 两条明确的限制，都在界面里可见、不静默降级：
/// <list type="bullet">
/// <item><description><strong>只执行 JavaScript</strong>：TypeScript 扩展需要构建期转译（本端口不内置转译器），
/// 发现到时逐条说明原因，而不是悄悄不加载。</description></item>
/// <item><description><strong>处理器必须同步</strong>：返回 Promise 会被拒绝并说明原因（异步要等事件循环泵
/// 接进宿主）。把 <c>[object Promise]</c> 当成结果交给模型，比直接报错难查得多。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ExtensionHost : IDisposable
{
	/// <summary>单次进入扩展代码的时限。卡死的扩展不该拖住会话。</summary>
	private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// 在 JS 侧组装 <c>pi</c>：每个方法都转调宿主函数，注册结果通过 <c>__piRegister*</c> 交回宿主。
	/// 用 <c>globalThis.pi</c> 而不是 let/const：扩展文件与这段前缀同处一个脚本作用域。
	/// </summary>
	private const string Prelude = """
		globalThis.pi = (function () {
			function callable(value) { return typeof value === "function"; }
			function text(value) { return value === undefined || value === null ? "" : String(value); }
			// 命令定义有两种写法，都支持：
			//   pi.registerCommand("x", { description, handler })   ← pi 的文档形态
			//   pi.registerCommand("x", "描述", handler)             ← 位置参数形态
			//   pi.registerCommand("x", handler)                    ← 只有处理器
			// 只认第一种的话，第二种的 handler 会被当成「不是函数」而静默丢掉命令。
			function commandDefinition(second, third) {
				if (callable(third)) { return { description: text(second), handler: third }; }
				if (callable(second)) { return { description: "", handler: second }; }
				return second === undefined || second === null ? {} : second;
			}
			var ui = {
				hasUI: __piHasUI === true,
				notify: function (message, kind) { __piNotify(text(message), callable(__piNotify) ? text(kind || "info") : "info"); },
				setStatus: function (key, value) { __piSetStatus(text(key), value === undefined || value === null ? "" : text(value)); },
				setWidget: function (key, value) { __piSetWidget(text(key), value === undefined || value === null ? "" : (typeof value === "string" ? value : text(value.text || ""))); },
				setTitle: function (title) { __piSetTitle(text(title)); },
				confirm: function (title, message) { return __piConfirm(text(title), message === undefined || message === null ? "" : text(message)); },
				input: function (title) { return __piInput(text(title)); },
				select: function (title, choices) {
					var list = Array.isArray(choices) ? choices.map(function (item) { return text(item); }) : [];
					return __piSelect(text(title), JSON.stringify(list));
				},
			};
			return Object.freeze({
				on: function (name, handler) {
					if (!callable(handler)) { __piDiagnose("pi.on(\"" + text(name) + "\") 的第二个参数不是函数。"); return; }
					__piOn(text(name), handler);
				},
				registerTool: function (definition) {
					if (!definition || typeof definition !== "object") { __piDiagnose("pi.registerTool(...) 需要一个定义对象。"); return; }
					if (!callable(definition.execute)) { __piDiagnose("工具 \"" + text(definition.name) + "\" 没有 execute 函数。"); return; }
					__piRegisterTool(
						text(definition.name),
						text(definition.label || definition.name),
						text(definition.description || ""),
						definition.parameters && typeof definition.parameters === "object" ? JSON.stringify(definition.parameters) : "{}",
						definition.execute);
				},
				registerCommand: function (name, second, third) {
					var definition = commandDefinition(second, third);
					if (!callable(definition.handler)) { __piDiagnose("命令 \"" + text(name) + "\" 没有 handler 函数。"); return; }
					__piRegisterCommand(text(name), text(definition.description || ""), definition.handler);
				},
				ui: Object.freeze(ui),
			});
		})();
		""";

	private readonly List<ExtensionState> _states = [];
	private readonly IUiHost? _ui;
	private bool _disposed;

	public ExtensionHost(IUiHost? ui = null) => _ui = ui;

	/// <summary>已加载扩展的状态（含加载失败的），供资源页与诊断展示。</summary>
	public IReadOnlyList<ExtensionState> States => _states;

	public IReadOnlyList<AgentTool> Tools =>
		[.. _states.Where(static state => state.Loaded).SelectMany(state => state.Registration.Tools)];

	public IReadOnlyList<ExtensionCommand> Commands =>
		[.. _states.Where(static state => state.Loaded).SelectMany(state => state.Registration.Commands)];

	/// <summary>加载期发现的、宿主不派发的事件名。</summary>
	public IReadOnlyList<string> UnsupportedEvents =>
		[.. _states.SelectMany(state => state.Registration.UnsupportedEvents)];

	/// <summary>某个事件是否有任何扩展监听（宿主据此省掉构造 payload 的开销）。</summary>
	public bool ListensTo(string @event) =>
		!_disposed && _states.Any(state => state.Loaded && state.Registration.HasHandlers(@event));

	/// <summary>发现并加载扩展。单个扩展失败只影响它自己，其余照常加载。</summary>
	public void Load(string cwd, bool trusted = true, string? agentDir = null)
	{
		if (_disposed)
		{
			return;
		}

		foreach (var descriptor in ExtensionDescriptor.Discover(cwd, trusted, agentDir))
		{
			Load(descriptor);
		}
	}

	public ExtensionState Load(ExtensionDescriptor descriptor)
	{
		var state = new ExtensionState { Descriptor = descriptor };
		_states.Add(state);

		if (!descriptor.Supported)
		{
			state.Diagnostics.Add(descriptor.UnsupportedReason ?? "不支持的扩展文件");
			return state;
		}

		string source;
		try
		{
			source = File.ReadAllText(descriptor.FilePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			state.Diagnostics.Add($"读取扩展失败：{exception.Message}");
			return state;
		}

		// ESM 语法在这里还是不可用。与其让解析器抛一个看不懂的语法错，不如直说。
		if (HasModuleSyntax(source))
		{
			state.Diagnostics.Add("扩展用了 ESM 的 import/export 语句；宿主按脚本执行扩展文件，请改用 `module.exports = function (pi) { ... }`。");
			return state;
		}

		var worker = new ExtensionWorker(Path.GetFileNameWithoutExtension(descriptor.FilePath));
		state.Worker = worker;

		try
		{
			// 注册期在扩展线程上跑完：引擎此后只被这个线程碰（事件与工具调用由宿主投递进来）。
			worker.RunAsync(() =>
			{
				Register(state, source);
				return true;
			}, InvokeTimeout).GetAwaiter().GetResult();
			state.Loaded = true;
		}
		catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException
			or InvalidOperationException or NotSupportedException or Jint.Runtime.JavaScriptException)
		{
			state.Diagnostics.Add(Describe(exception));
		}

		if (!state.Loaded)
		{
			DisposeState(state);
		}

		return state;
	}

	/// <summary>
	/// 把事件派发给所有监听它的扩展，返回合并后的 payload（扩展可就地改写字段）。
	/// 单个扩展抛错只记在它自己的诊断里，不影响其它扩展与本次工具调用。
	/// </summary>
	public JsonObject Dispatch(string @event, JsonObject payload)
	{
		if (_disposed)
		{
			return payload;
		}

		foreach (var state in _states)
		{
			if (!state.Loaded || !state.Registration.HasHandlers(@event))
			{
				continue;
			}

			var worker = state.Worker!;
			var json = payload.ToJsonString();
			try
			{
				if (worker.OnWorkerThread)
				{
					InvokeEvent(state, @event, json);
				}
				else
				{
					worker.RunAsync<bool>(() => { InvokeEvent(state, @event, json); return true; }, InvokeTimeout).GetAwaiter().GetResult();
				}

				// 处理器可能就地改写了 payload；JS 侧留了一份，按同样事件名取回来。
				var updated = state.Worker!.OnWorkerThread
					? LatestPayload(state, @event)
					: state.Worker.RunAsync(() => LatestPayload(state, @event), InvokeTimeout).GetAwaiter().GetResult();
				if (JsonNode.Parse(updated) is JsonObject parsed)
				{
					payload = parsed;
				}
			}
			catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException
				or InvalidOperationException or NotSupportedException or Jint.Runtime.JavaScriptException or AggregateException)
			{
				state.Diagnostics.Add($"{@event} 处理器失败：{Describe(exception)}");
			}
		}

		return payload;
	}

	private static string LatestPayload(ExtensionState state, string @event) =>
		state.Engine!.Invoke("piweftLatest", @event).AsString();

	private void InvokeEvent(ExtensionState state, string @event, string json)
	{
		// 用 Invoke(全局函数名, object[])：这个版本的 Jint 没有 JsValue.From/Create，
		// 字符串参数由 Invoke 自己转换。
		state.Engine!.Invoke("piweftCallHandler", @event, json);
	}

	/// <summary>在扩展线程上调用某个扩展工具的实现并取回 JSON 结果。</summary>
	internal Task<string> InvokeToolByNameAsync(ExtensionState state, string toolName, string argumentsJson)
	{
		var worker = state.Worker ?? throw new ObjectDisposedException(nameof(ExtensionHost));
		if (worker.OnWorkerThread)
		{
			return Task.FromResult(InvokeToolOnWorker(state, toolName, argumentsJson));
		}

		return worker.RunAsync(() => InvokeToolOnWorker(state, toolName, argumentsJson), InvokeTimeout);
	}

	private string InvokeToolOnWorker(ExtensionState state, string toolName, string argumentsJson)
	{
		var engine = state.Engine!;
		engine.Invoke("piweftTool", toolName, argumentsJson);
		return engine.Invoke("piweftToolResult").AsString();
	}

	/// <summary>运行一个扩展注册的斜杠命令。</summary>
	public void RunCommand(string name, string argument)
	{
		foreach (var state in _states)
		{
			var command = state.Registration.Commands.FirstOrDefault(candidate =>
				string.Equals(candidate.Name, name, StringComparison.Ordinal));
			if (command is null)
			{
				continue;
			}

			var worker = state.Worker!;
			try
			{
				if (worker.OnWorkerThread)
				{
					command.Invoke(argument);
				}
				else
				{
					worker.RunAsync<bool>(() => { command.Invoke(argument); return true; }, InvokeTimeout).GetAwaiter().GetResult();
				}
			}
			catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException
				or InvalidOperationException or Jint.Runtime.JavaScriptException or AggregateException)
			{
				state.Diagnostics.Add($"命令 {name} 失败：{Describe(exception)}");
			}

			return;
		}
	}

	private void Register(ExtensionState state, string source)
	{
		var engine = new Engine();
		state.Engine = engine;
		var registration = state.Registration;

		engine.SetValue("__piHasUI", _ui is { HasUi: true });
		engine.SetValue("__piDiagnose", new ClrFunction(engine, "__piDiagnose", (thisObject, args) =>
		{
			state.Diagnostics.Add(args.At(0).AsString());
			return JsValue.Undefined;
		}));

		engine.SetValue("__piOn", new ClrFunction(engine, "__piOn", (thisObject, args) =>
		{
			var @event = args.At(0).AsString();
			// 处理器函数留在 JS 侧（C# 无法跨线程持有可调用的 JS 函数），
			// C# 这边只记「谁监听什么事件」，用来决定要不要构造 payload。
			registration.NoteListener(@event);
			if (!registration.Supported(@event))
			{
				state.Diagnostics.Add(
					$"pi.on(\"{@event}\")：当前宿主不派发这个事件，处理器不会被调用。可派发：{string.Join(", ", ExtensionRegistration.SupportedEvents)}。");
				return JsValue.Undefined;
			}

			engine.Invoke("piweftOn", @event);
			engine.Invoke("piweftStoreHandler", @event, args.At(1));
			return JsValue.Undefined;
		}));

		engine.SetValue("__piRegisterTool", new ClrFunction(engine, "__piRegisterTool", (thisObject, args) =>
		{
			var name = args.At(0).AsString();
			if (name.Length == 0)
			{
				state.Diagnostics.Add("pi.registerTool：缺少 name。");
				return JsValue.Undefined;
			}

			var label = args.At(1).AsString();
			var description = args.At(2).AsString();
			var parameters = ParseObject(args.At(3).AsString());

			// execute 函数留在 JS 侧按名字取（C# 不跨线程持有 JS 函数）。
			engine.Invoke("piweftStoreTool", name, args.At(4));
			var tool = new AgentTool
			{
				Name = name,
				Label = label.Length > 0 ? label : name,
				Description = description,
				Parameters = parameters,
				Execute = invocation =>
				{
					// 代理线程把调用投到扩展线程上执行：Jint 引擎不是线程安全的。
					var json = InvokeToolByNameAsync(state, name, invocation.Arguments.ToJsonString())
						.WaitAsync(InvokeTimeout)
						.GetAwaiter()
						.GetResult();
					return Task.FromResult(ToToolResult(json));
				},
			};
			registration.AddTool(tool);
			return JsValue.Undefined;
		}));

		engine.SetValue("__piRegisterCommand", new ClrFunction(engine, "__piRegisterCommand", (thisObject, args) =>
		{
			var name = args.At(0).AsString();
			var description = args.At(1).AsString();
			// 处理器函数留在 JS 侧：C# 只记名字与描述，执行时按名字回调。
			engine.Invoke("piweftCommand", name, args.At(2));
			registration.AddCommand(new ExtensionCommand(name, description, argument =>
			{
				var result = engine.Invoke("piweftCallCommand", name, argument);
				RejectPromise(result, $"命令 {name}", state);
			}));
			return JsValue.Undefined;
		}));

		InstallUi(engine, state);

		// 处理器与命令函数留在 JS 侧的注册表里，宿主只通过下面这几个 JS 函数调它们。
		engine.Evaluate(HandlerRegistry);
		engine.Evaluate(Prelude);
		engine.Evaluate(source);

		// pi 的扩展是 `export default function (pi) {...}`；脚本形态下约定 module.exports。
		// 用户代码可能改写 module.exports（整体赋值）或 module.exports.default；两种都认。
		engine.Evaluate("""
			(function () {
				var exported = (typeof module === "object" && module) ? module.exports : undefined;
				if (exported && exported.default) { exported = exported.default; }
				else if (exported === globalThis.exports && typeof globalThis.exports === "object" && Object.keys(globalThis.exports).length) {
					exported = globalThis.exports.default || globalThis.exports;
				}
				if (typeof exported === "function") { exported(globalThis.pi); }
				else if (exported !== undefined && exported !== null && typeof exported === "object") {
					// 也支持 { default: fn } 之外的对象形态：有 handler 字段就用它
					__piDiagnose("扩展导出的不是函数：应为 module.exports = function (pi) { ... }。");
				}
			})();
			""");
	}

	/// <summary>宿主侧的 JS 注册表：处理器与命令函数存在这里，宿主按名字回调。</summary>
	private const string HandlerRegistry = """
		// 扩展按 pi 的约定写 `module.exports = function (pi) {...}`；宿主不提供 require/imports，
		// 但必须提供 module/exports 这两个名字，否则扩展第一行就 "module is not defined"。
		globalThis.module = { exports: {} };
		globalThis.exports = module.exports;
		globalThis.piweftHandlers = Object.create(null);
		globalThis.piweftCommands = Object.create(null);
		globalThis.piweftPending = { event: "", result: "" };
		globalThis.piweftOn = function (name) {
			if (!piweftHandlers[name]) { piweftHandlers[name] = []; }
		};
		globalThis.piweftStoreHandler = function (name, handler) { piweftOn(name); piweftHandlers[name].push(handler); };
		globalThis.piweftCallHandler = function (name, payloadJson) {
			var list = piweftHandlers[name] || [];
			var payload = JSON.parse(payloadJson);
			for (var i = 0; i < list.length; i++) {
				var outcome = list[i](payload);
				// async 函数返回 Promise：只有 JS 侧能准确认出它（C# 看到的是已返回的 undefined）。
				// 不支持异步就明说，而不是把 [object Promise] 当结果继续用。
				if (outcome && typeof outcome.then === "function") {
					__piDiagnose(name + " 处理器返回了 Promise：当前扩展宿主只支持同步处理器，返回值已丢弃。");
				}
			}
			// 宿主读不回 JS 对象的引用，改过的字段必须显式回传。
			piweftPending.event = name;
			piweftPending.result = JSON.stringify(payload);
			return undefined;
		};
		globalThis.piweftLatest = function (name) {
			return piweftPending.event === name ? piweftPending.result : "";
		};
		globalThis.piweftCommand = function (name, handler) { piweftCommands[name] = handler; };
		globalThis.piweftCallCommand = function (name, argument) {
			var handler = piweftCommands[name];
			if (!handler) {
				__piDiagnose("命令 \"" + name + "\" 没有已注册的处理器。");
				return undefined;
			}
			var outcome = handler(argument);
			if (outcome && typeof outcome.then === "function") {
				__piDiagnose("命令 \"" + name + "\" 返回了 Promise：当前扩展宿主只支持同步处理器。");
			}
			return undefined;
		};
		globalThis.piweftTools = Object.create(null);
		globalThis.piweftStoreTool = function (name, handler) { piweftTools[name] = handler; };
		globalThis.piweftTool = function (name, argumentsJson) {
			var tool = piweftTools[name];
			piweftTool.last = tool ? tool(JSON.parse(argumentsJson)) : { isError: true, error: "未注册的工具 " + name };
			return undefined;
		};
		globalThis.piweftToolResult = function () {
			var value = piweftTool.last;
			piweftTool.last = JSON.stringify({ text: "工具没有返回值" });
			return typeof value === "string" ? JSON.stringify({ text: value }) : JSON.stringify(value === undefined ? {} : value);
		};
		""";

	private void InstallUi(Engine engine, ExtensionState state)
	{
		var host = _ui;

		string? Ask(Func<IUiHost, CancellationToken, Task<string?>> ask)
		{
			if (host is not { HasUi: true })
			{
				state.Diagnostics.Add("当前环境没有宿主界面，交互调用按取消处理。");
				return null;
			}

			using var timeout = new CancellationTokenSource(InvokeTimeout);
			try
			{
				return ask(host, timeout.Token).WaitAsync(InvokeTimeout).GetAwaiter().GetResult();
			}
			catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
			{
				state.Diagnostics.Add("交互调用超时，按取消处理。");
				return null;
			}
		}

		/// <summary>三态确认：null = 宿主不支持或取消，与「用户答否」必须区分。</summary>
		bool? AskBool(Func<IUiHost, CancellationToken, Task<bool?>> ask)
		{
			if (host is not { HasUi: true })
			{
				state.Diagnostics.Add("当前环境没有宿主界面，交互调用按取消处理。");
				return null;
			}

			using var timeout = new CancellationTokenSource(InvokeTimeout);
			try
			{
				return ask(host, timeout.Token).WaitAsync(InvokeTimeout).GetAwaiter().GetResult();
			}
			catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
			{
				state.Diagnostics.Add("交互调用超时，按取消处理。");
				return null;
			}
		}

		// 接收已判过空的 host：调用方因此不必写 host! 去骗过可空分析。
		void Fire(Action<IUiHost> action)
		{
			if (host is not { HasUi: true })
			{
				state.Diagnostics.Add("当前环境没有宿主界面。");
				return;
			}

			action(host);
		}

		engine.SetValue("__piNotify", new ClrFunction(engine, "__piNotify", (thisObject, args) =>
		{
			Fire(ui => ui.Notify(new UiNotifyRequest(args.At(0).AsString(), args.At(1).AsString())));
			return JsValue.Undefined;
		}));

		engine.SetValue("__piSetStatus", new ClrFunction(engine, "__piSetStatus", (thisObject, args) =>
		{
			var key = args.At(0).AsString();
			var text = args.At(1).AsString();
			Fire(ui => ui.SetStatus(new UiStatusRequest(key, text.Length == 0 ? null : text)));
			return JsValue.Undefined;
		}));

		engine.SetValue("__piSetWidget", new ClrFunction(engine, "__piSetWidget", (thisObject, args) =>
		{
			var key = args.At(0).AsString();
			var text = args.At(1).AsString();
			IReadOnlyList<string>? lines = text.Length == 0
				? null
				: [.. text.Split('\n').Select(static line => line.TrimEnd('\r'))];
			Fire(ui => ui.SetWidget(new UiWidgetRequest(key, lines, null)));
			return JsValue.Undefined;
		}));

		engine.SetValue("__piSetTitle", new ClrFunction(engine, "__piSetTitle", (thisObject, args) =>
		{
			Fire(ui => ui.SetTitle(new UiTitleRequest(args.At(0).AsString())));
			return JsValue.Undefined;
		}));

		engine.SetValue("__piConfirm", new ClrFunction(engine, "__piConfirm", (thisObject, args) =>
		{
			var title = args.At(0).AsString();
			var message = args.At(1).AsString();
			// ConfirmAsync 是 bool?；JsValue 有 bool 的隐式转换，null（宿主不支持/取消）单独映射成 JS 的 null。
			bool? answer = AskBool((h, token) => h.ConfirmAsync(new UiConfirmRequest(title, message.Length == 0 ? null : message, null), token));
			return answer is null ? JsValue.Null : (JsValue)answer.Value;
		}));

		engine.SetValue("__piInput", new ClrFunction(engine, "__piInput", (thisObject, args) =>
		{
			var title = args.At(0).AsString();
			var answer = Ask((h, token) => h.InputAsync(new UiInputRequest(title, null), token));
			return answer is null ? JsValue.Null : (JsValue)answer;
		}));

		engine.SetValue("__piSelect", new ClrFunction(engine, "__piSelect", (thisObject, args) =>
		{
			var title = args.At(0).AsString();
			var options = ParseStringArray(args.At(1).AsString());
			var answer = Ask((h, token) => h.SelectAsync(new UiSelectRequest(title, options, null), token));
			return answer is null ? JsValue.Null : (JsValue)answer;
		}));
	}

	/// <summary>
	/// 处理器返回 Promise 时明确报告。异步支持要等事件循环泵接进宿主；在那之前把
	/// <c>[object Promise]</c> 当成结果交给模型，是最难排查的一种静默错误。
	/// <para>Jint 直接给了 <c>IsPromise()</c>，不用靠探测 <c>then</c> 属性猜。</para>
	/// </summary>
	private static void RejectPromise(JsValue? value, string what, ExtensionState state)
	{
		if (value is not null && value.IsPromise())
		{
			state.Diagnostics.Add($"{what} 返回了 Promise：当前扩展宿主只支持同步处理器，返回值已丢弃。");
		}
	}

	private static IReadOnlyList<string> ParseStringArray(string json)
	{
		if (JsonNode.Parse(json) is not JsonArray array)
		{
			return [];
		}

		var values = new List<string>(array.Count);
		foreach (var node in array)
		{
			values.Add(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? string.Empty);
		}

		return values;
	}

	private static JsonObject ParseObject(string json)
	{
		try
		{
			return JsonNode.Parse(json) as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
		}
		catch (JsonException)
		{
			return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
		}
	}

	/// <summary>扩展工具结果的形状：<c>{ text }</c> / <c>{ content: [...] }</c> / <c>{ isError, error }</c>，或直接一个字符串。</summary>
	private static AgentToolResult ToToolResult(string json)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException)
		{
			return AgentToolResult.Error("扩展工具返回了不合法的结果");
		}

		if (node is JsonValue scalar && scalar.TryGetValue<string>(out var plain))
		{
			return AgentToolResult.Text(plain);
		}

		if (node is not JsonObject obj)
		{
			return AgentToolResult.Text(string.Empty);
		}

		if (IsError(obj, out var error))
		{
			return AgentToolResult.Error(error);
		}

		if (obj["text"] is JsonValue text && text.TryGetValue<string>(out var value))
		{
			return AgentToolResult.Text(value);
		}

		if (obj["content"] is JsonArray content)
		{
			var builder = new System.Text.StringBuilder();
			foreach (var block in content)
			{
				if (block is JsonValue item && item.TryGetValue<string>(out var itemText))
				{
					builder.Append(itemText);
				}
				else if (block?["text"] is JsonValue part && part.TryGetValue<string>(out var partText))
				{
					builder.Append(partText);
				}
			}

			return AgentToolResult.Text(builder.ToString());
		}

		return AgentToolResult.Text(obj.ToJsonString());
	}

	private static bool IsError(JsonObject obj, out string message)
	{
		message = "扩展工具返回错误";
		var flagged = obj["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
		if (!flagged && obj["error"] is null)
		{
			return false;
		}

		if (obj["error"] is JsonValue error && error.TryGetValue<string>(out var text))
		{
			message = text;
			return true;
		}

		if (obj["content"] is JsonArray { Count: > 0 } content
			&& content[0] is JsonValue first && first.TryGetValue<string>(out var firstText))
		{
			message = firstText;
		}

		return true;
	}

	private static bool HasModuleSyntax(string source)
	{
		foreach (var raw in source.Split('\n'))
		{
			var line = raw.TrimStart();
			if (line.StartsWith("import ", StringComparison.Ordinal)
				|| line.StartsWith("import{", StringComparison.Ordinal)
				|| line.StartsWith("export ", StringComparison.Ordinal)
				|| line.StartsWith("export{", StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static string Describe(Exception exception) =>
		exception switch
		{
			TimeoutException => "超时：扩展在一次调用里没有返回（已跳过，会话继续）。",
			Jint.Runtime.JavaScriptException script => script.Message,
			ObjectDisposedException => "扩展已释放。",
			AggregateException aggregate when aggregate.InnerException is not null => Describe(aggregate.InnerException),
			_ => exception.Message,
		};

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		foreach (var state in _states)
		{
			DisposeState(state);
		}
	}

	private static void DisposeState(ExtensionState state)
	{
		state.Worker?.Dispose();
		state.Worker = null;
		state.Engine?.Dispose();
		state.Engine = null;
	}
}
