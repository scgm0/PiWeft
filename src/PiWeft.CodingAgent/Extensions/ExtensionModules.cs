using Jint;

namespace PiWeft.CodingAgent.Extensions;

/// 扩展可以 <c>import</c> 的宿主模块。pi 的扩展生态按 ESM 写，最常见的是
/// <c>import { Type } from "typebox"</c>（工具参数）与
/// <c>import { isToolCallEventType } from "@earendil-works/pi-coding-agent"</c>。
/// 这里只提供声明与纯函数，没有任何文件、网络或进程能力：沙箱纪律不变，
/// 区别只是模块名不再解析失败。
internal static class ExtensionModules
{
	/// 与 pi 的别名保持一致，旧包名重定向到同一份实现。
	private static readonly string[] TypeBoxNames = ["typebox", "@sinclair/typebox"];

	private static readonly string[] CodingAgentNames =
	[
		"@earendil-works/pi-coding-agent",
		"@mariozechner/pi-coding-agent",
	];

	private static readonly string[] AiNames =
	[
		"@earendil-works/pi-ai",
		"@mariozechner/pi-ai",
	];

	/// 只声明类型的模块：注册空导出，避免一个纯类型 import 把整个扩展拦在解析阶段。
	private static readonly string[] EmptyNames =
	[
		"@earendil-works/pi-agent-core",
		"@mariozechner/pi-agent-core",
		"@earendil-works/pi-tui",
		"@mariozechner/pi-tui",
	];

	public static void Register(Engine engine)
	{
		foreach (var name in TypeBoxNames)
		{
			engine.Modules.Add(name, builder => builder.AddSource(TypeBoxSource));
		}

		foreach (var name in CodingAgentNames)
		{
			engine.Modules.Add(name, builder => builder.AddSource(CodingAgentSource));
		}

		foreach (var name in AiNames)
		{
			engine.Modules.Add(name, builder => builder.AddSource(AiSource));
		}

		foreach (var name in EmptyNames)
		{
			engine.Modules.Add(name, builder => builder.AddSource("export {};"));
		}
	}

	/// TypeBox 的 JSON Schema 子集，覆盖工具参数用到的构造器。产物是纯 JSON Schema，
	/// 校验由宿主做（`Type.Refine`、codec 这类超出 JSON Schema 的语义按 pi 的规则忽略）。
	private const string TypeBoxSource = """
		const OPTIONAL = Symbol("piweft.typebox.optional");

		function isOptional(value) {
			return !!(value && typeof value === "object" && value[OPTIONAL] === true);
		}

		function withOptions(base, options) {
			if (!options || typeof options !== "object") { return base; }
			const result = Object.assign({}, options);
			for (const key of Object.keys(base)) { result[key] = base[key]; }
			return result;
		}

		function object(properties, options) {
			const props = {};
			const required = [];
			for (const key of Object.keys(properties || {})) {
				const value = properties[key];
				props[key] = value;
				if (!isOptional(value)) { required.push(key); }
			}
			const schema = withOptions({ type: "object", properties: props }, options);
			if (required.length > 0) { schema.required = required; }
			return schema;
		}

		function array(items, options) {
			return withOptions({ type: "array", items }, options);
		}

		function tuple(items, options) {
			const list = Array.isArray(items) ? items : [];
			return withOptions({ type: "array", prefixItems: list, minItems: list.length, maxItems: list.length }, options);
		}

		function record(key, value, options) {
			if (key && typeof key === "object") {
				if (typeof key.pattern === "string") {
					return withOptions({ type: "object", patternProperties: Object.fromEntries([[key.pattern, value]]) }, options);
				}
				if (key.type === "string") {
					return withOptions({ type: "object", additionalProperties: value }, options);
				}
			}
			return withOptions({ type: "object", additionalProperties: value }, options);
		}

		function union(schemas, options) {
			return withOptions({ anyOf: Array.isArray(schemas) ? schemas : [] }, options);
		}

		function intersect(schemas, options) {
			return withOptions({ allOf: Array.isArray(schemas) ? schemas : [] }, options);
		}

		function literal(value, options) {
			return withOptions(value === undefined ? { not: {} } : { const: value }, options);
		}

		function enumType(values, options) {
			if (Array.isArray(values)) { return withOptions({ enum: values }, options); }
			const entries = [];
			for (const key of Object.keys(values || {})) {
				if (isNaN(Number(key))) { entries.push(values[key]); }
			}
			return withOptions({ enum: entries }, options);
		}

		function partial(schema) {
			if (!schema || typeof schema !== "object") { return schema; }
			const copy = Object.assign({}, schema);
			delete copy.required;
			return copy;
		}

		function required(schema) {
			if (!schema || typeof schema !== "object" || !schema.properties) { return schema; }
			return Object.assign({}, schema, { required: Object.keys(schema.properties) });
		}

		function subset(schema, keys, keep) {
			const list = Array.isArray(keys) ? keys : [];
			const props = {};
			const req = [];
			for (const key of Object.keys((schema && schema.properties) || {})) {
				if ((list.indexOf(key) >= 0) !== keep) { continue; }
				props[key] = schema.properties[key];
				if (Array.isArray(schema.required) && schema.required.indexOf(key) >= 0) { req.push(key); }
			}
			const copy = Object.assign({}, schema, { properties: props });
			if (req.length > 0) { copy.required = req; } else { delete copy.required; }
			return copy;
		}

		const Type = {
			Any: (options) => withOptions({}, options),
			Unknown: (options) => withOptions({}, options),
			Never: (options) => withOptions({ not: {} }, options),
			Null: (options) => withOptions({ type: "null" }, options),
			Undefined: (options) => withOptions({ not: {} }, options),
			Void: (options) => withOptions({ not: {} }, options),
			Boolean: (options) => withOptions({ type: "boolean" }, options),
			Number: (options) => withOptions({ type: "number" }, options),
			Integer: (options) => withOptions({ type: "integer" }, options),
			String: (options) => withOptions({ type: "string" }, options),
			Date: (options) => withOptions({ type: "string", format: "date-time" }, options),
			Literal: literal,
			Object: object,
			Array: array,
			Tuple: tuple,
			Record: record,
			Union: union,
			Intersect: intersect,
			Enum: enumType,
			Optional: (schema) => {
				if (!schema || typeof schema !== "object") { return schema; }
				const copy = Object.assign({}, schema);
				copy[OPTIONAL] = true;
				return copy;
			},
			Partial: partial,
			Required: required,
			Pick: (schema, keys) => subset(schema, keys, true),
			Omit: (schema, keys) => subset(schema, keys, false),
			Ref: (id, options) => withOptions({ "$ref": "#/$defs/" + id }, options),
			Strict: (schema) => Object.assign({}, schema, { additionalProperties: false }),
			Clone: (schema) => Object.assign({}, schema),
			Unsafe: (schema) => schema,
			Refine: (schema) => schema,
			Codec: (schema) => schema,
			Transform: (schema) => schema,
			Awaited: (schema) => schema,
		};

		export { Type };
		export default { Type };
		""";

	private const string CodingAgentSource = """
		function resultOf(toolName) {
			return (event) => !!event && event.toolName === toolName;
		}

		export function isToolCallEventType(toolName, event) {
			return !!event && event.toolName === toolName;
		}

		export const isBashToolResult = resultOf("bash");
		export const isPowerShellToolResult = resultOf("powershell");
		export const isReadToolResult = resultOf("read");
		export const isEditToolResult = resultOf("edit");
		export const isWriteToolResult = resultOf("write");
		export const isGrepToolResult = resultOf("grep");
		export const isFindToolResult = resultOf("find");
		export const isLsToolResult = resultOf("ls");

		export function defineTool(tool) {
			return tool;
		}
		""";

	private const string AiSource = """
		import { Type } from "typebox";

		export { Type };

		export function StringEnum(values, options) {
			const schema = { type: "string", enum: Array.isArray(values) ? values.slice() : [] };
			if (options && options.description) { schema.description = options.description; }
			if (options && options.default) { schema.default = options.default; }
			return schema;
		}
		""";
}
