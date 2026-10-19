namespace PiWeft.JavaScript;

/// <summary>
/// 在脚本之前求值的 JavaScript。它把宿主桥接函数 <c>__cmCall</c>/<c>__cmOutput</c> 藏在闭包里，
/// 在它们之上搭出 <c>tools</c>、<c>ALL_TOOLS</c>、<c>searchTools</c>/<c>describeTool</c>、
/// <c>text</c>/<c>image</c>/<c>exit</c>/<c>console</c>、<c>store</c>/<c>load</c> 与 <c>models.*</c>。
/// <para>
/// 与 pi 的 prelude 相比，这里不做内置对象的锁定（lockdown）：每次执行都新建并丢弃解释器，
/// 脚本破坏原型只会影响它自己的这一次结果；换来的是不会和 Jint 的内建实现互相干扰。
/// </para>
/// </summary>
internal static class CodeModePrelude
{
	/// <summary>单值 JSON 文本上限（256 Ki 字符）。</summary>
	public const int MaxStoreValueChars = 256 * 1024;

	/// <summary>整个 store 的 JSON 文本上限（1 Mi 字符）。</summary>
	public const int MaxStoreTotalChars = 1024 * 1024;

	/// <summary>
	/// 求值结果为 <c>{ run }</c>：接受一个 async 函数，返回
	/// <c>{"ok":true,"valueJson":...,"writesJson":"[[key, json], ...]"}</c>
	/// 或 <c>{"ok":false,"errorJson":"{name,message,stack}"}</c>。
	/// </summary>
	public const string Source =
		"""
		(function (toolsJson, storeJson) {
			"use strict";

			const stringify = JSON.stringify;
			const parse = JSON.parse;
			const ErrorCtor = Error;
			const TypeErrorCtor = TypeError;
			const RangeErrorCtor = RangeError;
			const MAX_STORE_VALUE_CHARS = __MAX_STORE_VALUE_CHARS__;
			const MAX_STORE_TOTAL_CHARS = __MAX_STORE_TOTAL_CHARS__;
			const EXIT = Object.freeze({});
			let finished = false;

			// 宿主桥只留在 prelude 的闭包里：脚本拿不到它们，也就无法伪造输出或提前收尾。
			const hostOutput = __cmOutput;
			const hostCall = __cmCall;
			const hostSearch = __cmSearch;
			const hostDescribe = __cmDescribe;
			const hostDescribeNamespace = __cmDescribeNamespace;
			const hostModels = __cmModels;
			const hostModelCall = __cmModelCall;
			const finish = __cmFinish;
			[
				"__cmOutput", "__cmCall", "__cmSearch", "__cmDescribe", "__cmDescribeNamespace", "__cmModels", "__cmModelCall", "__cmFinish",
			].forEach(function (name) {
				try { delete globalThis[name]; } catch (error) { }
			});

			function errorText(error) {
				const head = error && error.message ? (error.name || "Error") + ": " + error.message : String(error);
				const frames = error && typeof error.stack === "string"
					? error.stack.split("\n").filter(function (line) { return line.trim() && line.indexOf("codemode-prelude.js") === -1; })
					: [];
				return [head].concat(frames).join("\n");
			}

			function format(value) {
				if (typeof value === "string") return value;
				if (value instanceof ErrorCtor) return errorText(value);
				try {
					const json = stringify(value);
					return json === undefined ? String(value) : json;
				} catch (error) {
					return String(value);
				}
			}

			function describeError(error) {
				try {
					if (error instanceof ErrorCtor) {
						return stringify({ name: String(error.name), message: String(error.message), stack: errorText(error) });
					}
					return stringify({ message: String(format(error)) });
				} catch (inner) {
					return stringify({ message: "The script threw a value that cannot be described" });
				}
			}

			// 宿主以 JSON 文本解析调用结果，拒绝值统一变成带 message 的 Error。
			function caller(name) {
				return function () {
					let json;
					try {
						json = arguments.length === 0 ? "null" : stringify(arguments[0]);
					} catch (error) {
						return Promise.reject(error);
					}
					return hostCall(name, json === undefined ? "null" : json).then(
						function (payload) {
							return payload === null || payload === undefined ? undefined : parse(payload);
						},
						function (error) {
							throw new ErrorCtor(typeof error === "string" ? error : format(error));
						});
				};
			}

			const tools = Object.create(null);
			const allTools = [];
			const listed = parse(toolsJson);
			for (let index = 0; index < listed.length; index++) {
				const entry = listed[index];
				const fn = caller(entry.name);
				// 两个工具名规范化到同一个标识符时，先出现的那个生效。
				if (!(entry.jsName in tools)) {
					tools[entry.jsName] = fn;
					allTools.push(Object.freeze({ name: entry.jsName, description: entry.description }));
				}
				if (!(entry.name in tools)) tools[entry.name] = fn;
			}
			Object.freeze(tools);
			Object.freeze(allTools);

			// 未列在描述里的工具（被预算裁掉或 MCP 提供的）用这些函数按需发现。
			function searchTools(query, options) {
				if (typeof query !== "string") throw new TypeErrorCtor("searchTools() expects a query string");
				const settings = options === undefined || options === null ? {} : options;
				const limit = settings.limit === undefined ? 8 : settings.limit;
				if (typeof limit !== "number" || !Number.isInteger(limit) || limit <= 0) {
					throw new TypeErrorCtor("searchTools() limit must be a positive integer");
				}
				const namespace = settings.namespace;
				if (namespace !== undefined && namespace !== null && typeof namespace !== "string") {
					throw new TypeErrorCtor("searchTools() namespace must be a string");
				}
				return parse(hostSearch(query, limit, namespace === undefined ? null : namespace));
			}

			function describeTool(name) {
				if (typeof name !== "string") throw new TypeErrorCtor("describeTool() expects a tool name");
				const description = hostDescribe(name);
				return description === undefined ? undefined : String(description);
			}

			function describeNamespace(name) {
				if (typeof name !== "string") throw new TypeErrorCtor("describeNamespace() expects a namespace name");
				const raw = hostDescribeNamespace(name);
				return raw === undefined || raw === null ? undefined : parse(raw);
			}

			// models.*：目录查询，与 pi 的 getModelsOfType/getAvailableOfType/getModelOfType 一致。
			const MODEL_TYPES = ["chat", "image", "classifier"];

			function modelTypeOf(value) {
				if (typeof value === "string" && MODEL_TYPES.indexOf(value) !== -1) return value;
				throw new TypeErrorCtor("Unknown model type " + stringify(value) + '. Use "chat", "image", or "classifier".');
			}

			function modelProviderOf(value) {
				if (value === undefined || value === null) return undefined;
				if (typeof value !== "string") throw new TypeErrorCtor("provider must be a string");
				return value;
			}

			function modelQuery(operation, type, provider, id) {
				const raw = hostModels(
					operation,
					type,
					provider === undefined ? null : provider,
					id === undefined ? null : id);
				return raw === undefined ? undefined : parse(raw);
			}

			// 分类与生图：模型对象来自上面的目录查询，context 形状由宿主校验。
			function modelCall(operation, model, context) {
				const label = "models." + operation;
				if (model === undefined || model === null || typeof model !== "object" || Array.isArray(model) ||
					typeof model.provider !== "string" || typeof model.id !== "string") {
					throw new TypeErrorCtor(label + "() expects a model object from models.getModelOfType() or models.getModelsOfType()");
				}
				let json;
				try {
					json = stringify(context === undefined ? null : context);
				} catch (error) {
					throw new TypeErrorCtor(label + "() context is not JSON-serializable: " + format(error));
				}
				return hostModelCall(operation, model.provider, model.id, json === undefined ? "null" : json).then(parse);
			}

			const models = Object.freeze({
				getModelsOfType: function (type, provider) {
					return modelQuery("list", modelTypeOf(type), modelProviderOf(provider), undefined);
				},
				getAvailableOfType: function (type, provider) {
					return modelQuery("available", modelTypeOf(type), modelProviderOf(provider), undefined);
				},
				getModelOfType: function (type, provider, id) {
					const resolved = modelTypeOf(type);
					if (typeof provider !== "string" || typeof id !== "string") {
						throw new TypeErrorCtor('models.getModelOfType(type, provider, id) expects three strings, for example ("chat", "anthropic", "claude-sonnet-4-5")');
					}
					return modelQuery("get", resolved, provider, id);
				},
				classify: function (model, context) {
					return modelCall("classify", model, context);
				},
				generateImages: function (model, context) {
					return modelCall("generateImages", model, context);
				},
			});

			// 读取不存在的成员时直接报错并提示相近的名字，而不是留到后面变成 "not a function"。
			function comparable(name) {
				return name.toLowerCase().replace(/[^a-z0-9]/g, "");
			}

			function guard(target, label, names) {
				return new Proxy(target, {
					get: function (object, property, receiver) {
						if (typeof property !== "string" || property in object || property in Object.prototype ||
							property === "then" || property === "toJSON") {
							return Reflect.get(object, property, receiver);
						}
						const wanted = comparable(property);
						const exact = names.filter(function (name) { return comparable(name) === wanted; });
						const close = exact.length > 0
							? exact
							: names.filter(function (name) {
								const other = comparable(name);
								return wanted && (other.indexOf(wanted) !== -1 || wanted.indexOf(other) !== -1);
							});
						let message = label + "." + property + " does not exist.";
						if (close.length > 0) message += " Did you mean " + close.slice(0, 5).map(function (name) { return label + "." + name; }).join(", ") + "?";
						else if (names.length <= 20) message += " Available: " + names.join(", ") + ".";
						message += " ALL_TOOLS lists every tool.";
						throw new TypeErrorCtor(message);
					},
				});
			}

			const toolsProxy = guard(tools, "tools", allTools.map(function (tool) { return tool.name; }));

			// key -> JSON 文本。大小按 key 与 JSON 的字符数计。
			const stored = new Map(Object.entries(parse(storeJson)));
			const writes = new Map();
			let storedChars = 0;
			stored.forEach(function (json, key) { storedChars += key.length + json.length; });

			const STORE_HINT = "store() is for small state such as IDs or summaries. Keep large data in variables, or write it to a file with a tool.";

			function checkKey(name, key) {
				if (typeof key !== "string") throw new TypeErrorCtor(name + "() key must be a string");
			}

			function store(key, value) {
				checkKey("store", key);
				const previous = stored.has(key) ? key.length + stored.get(key).length : 0;
				if (value === undefined) {
					stored.delete(key);
					storedChars -= previous;
					writes.set(key, undefined);
					return;
				}
				let json;
				try {
					json = stringify(value);
				} catch (error) {
					throw new TypeErrorCtor("store(" + stringify(key) + ") value is not JSON-serializable: " + format(error));
				}
				if (json === undefined) {
					throw new TypeErrorCtor("store(" + stringify(key) + ") value is not JSON-serializable");
				}
				if (json.length > MAX_STORE_VALUE_CHARS) {
					throw new RangeErrorCtor("store(" + stringify(key) + ") value has " + json.length +
						" characters of JSON, more than the limit of " + MAX_STORE_VALUE_CHARS + ". " + STORE_HINT);
				}
				const next = storedChars - previous + key.length + json.length;
				if (next > MAX_STORE_TOTAL_CHARS) {
					throw new RangeErrorCtor("store is full: stored values would exceed " + MAX_STORE_TOTAL_CHARS +
						" characters of JSON. Delete keys with store(key, undefined). " + STORE_HINT);
				}
				stored.set(key, json);
				storedChars = next;
				writes.set(key, json);
			}

			function load(key) {
				checkKey("load", key);
				const json = stored.get(key);
				return json === undefined ? undefined : parse(json);
			}

			function serializeWrites() {
				const entries = [];
				writes.forEach(function (json, key) {
					entries.push(json === undefined ? [key] : [key, json]);
				});
				return stringify(entries);
			}

			function output(kind, data, mimeType) {
				if (finished) return;
				hostOutput(kind, data, mimeType === undefined ? null : mimeType);
			}

			// 原始值取字符串形式，其余转成 JSON。
			function outputText(value) {
				if (value === undefined || value === null || (typeof value !== "object" && typeof value !== "function")) {
					return String(value);
				}
				const json = stringify(value);
				return json === undefined ? String(value) : json;
			}

			function text(value) {
				let rendered;
				try {
					rendered = outputText(value);
				} catch (error) {
					throw new TypeErrorCtor(error instanceof ErrorCtor ? error.message : String(error));
				}
				output("text", rendered, null);
			}

			const IMAGE_HELPER_EXPECTS = 'image expects a non-empty image URL string, an object with image_url, or an image block { type: "image", data, mimeType }';

			function imageUrl(value) {
				if (typeof value === "string") return value;
				if (typeof value !== "object" || value === null || Array.isArray(value)) {
					throw new TypeErrorCtor(IMAGE_HELPER_EXPECTS);
				}
				if (value.image_url !== undefined) {
					if (typeof value.image_url !== "string") throw new TypeErrorCtor(IMAGE_HELPER_EXPECTS);
					return value.image_url;
				}
				if (typeof value.type !== "string") throw new TypeErrorCtor(IMAGE_HELPER_EXPECTS);
				if (value.type !== "image") throw new TypeErrorCtor('image only accepts image blocks, got "' + value.type + '"');
				if (typeof value.data !== "string" || value.data === "") throw new TypeErrorCtor("image expected image block data");
				if (value.data.toLowerCase().indexOf("data:") === 0) return value.data;
				return "data:;base64," + value.data;
			}

			// provider 能内联接受的格式签名（PNG、JPEG、GIF、WebP）；base64 前缀即字节签名。
			const IMAGE_SIGNATURES = [
				["image/png", /^iVBORw0KGg/],
				["image/jpeg", /^[/]9j[/](?!9)/],
				["image/gif", /^R0lGOD[dl]h/],
				["image/webp", /^UklG.{8}RUJQ/],
			];

			function image(value) {
				const url = imageUrl(value);
				if (url === "") throw new TypeErrorCtor(IMAGE_HELPER_EXPECTS);
				const colon = url.indexOf(":");
				const scheme = colon === -1 ? "" : url.slice(0, colon).toLowerCase();
				if (scheme === "http" || scheme === "https") {
					throw new TypeErrorCtor("remote image URLs are not supported in tool outputs. Pass a base64 data URI instead");
				}
				const comma = url.indexOf(",");
				const header = comma === -1 ? [] : url.slice(colon + 1, comma).split(";");
				if (scheme !== "data" || comma === -1 || !header.slice(1).some(function (part) { return part.toLowerCase() === "base64"; })) {
					throw new TypeErrorCtor("invalid image output. Pass a base64 data URI instead");
				}
				const data = url.slice(comma + 1).replace(/\s+/g, "");
				if (data.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(data)) {
					throw new TypeErrorCtor("invalid image output. The image data is not valid base64 (truncated or corrupted?)");
				}
				const head = data.slice(0, 16);
				let signature = null;
				for (let index = 0; index < IMAGE_SIGNATURES.length; index++) {
					if (IMAGE_SIGNATURES[index][1].test(head)) {
						signature = IMAGE_SIGNATURES[index][0];
						break;
					}
				}
				if (signature === null) {
					throw new TypeErrorCtor("invalid image output. The image data is not a PNG, JPEG, GIF, or WebP image");
				}
				output("image", data, signature);
			}

			function exit() {
				finished = true;
				throw EXIT;
			}

			const console = Object.create(null);
			["log", "info", "warn", "error", "debug"].forEach(function (level) {
				console[level] = function () {
					if (finished) return;
					const parts = [];
					for (let index = 0; index < arguments.length; index++) parts.push(format(arguments[index]));
					output("console", parts.join(" "), null);
				};
			});
			Object.freeze(console);

			Object.defineProperty(globalThis, "tools", { value: toolsProxy, enumerable: true });
			Object.defineProperty(globalThis, "ALL_TOOLS", { value: allTools, enumerable: true });
			Object.defineProperty(globalThis, "console", { value: console, enumerable: true });
			Object.defineProperty(globalThis, "text", { value: text, enumerable: true });
			Object.defineProperty(globalThis, "image", { value: image, enumerable: true });
			Object.defineProperty(globalThis, "exit", { value: exit, enumerable: true });
			Object.defineProperty(globalThis, "store", { value: store, enumerable: true });
			Object.defineProperty(globalThis, "load", { value: load, enumerable: true });
			Object.defineProperty(globalThis, "searchTools", { value: searchTools, enumerable: true });
			Object.defineProperty(globalThis, "describeTool", { value: describeTool, enumerable: true });
			Object.defineProperty(globalThis, "describeNamespace", { value: describeNamespace, enumerable: true });
			Object.defineProperty(globalThis, "models", { value: models, enumerable: true });

			// 不返回 Promise：宿主只启动它，然后在本线程泵任务队列，直到 finish 收到结果。
			async function run(fn) {
				let payload;
				try {
					const value = await fn();
					payload = stringify({
						ok: true,
						valueJson: value === undefined ? null : stringify(value),
						writesJson: serializeWrites(),
					});
				} catch (error) {
					payload = finished
						? stringify({ ok: true, valueJson: null, writesJson: serializeWrites() })
						: stringify({ ok: false, errorJson: describeError(error) });
				}
				finish(payload);
			}

			return { run: run };
		})
		""";
}
