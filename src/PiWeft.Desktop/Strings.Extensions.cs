namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string SettingsExtensions = "扩展";
	public const string SettingsBuiltinExtensions = "Pi 内置扩展";
	public const string SettingsBuiltinExtensionsHint =
		"Pi 随 CLI 分发，默认加载的四个内置扩展。开关写入全局 settings.json 的 extensions（builtin: 条目），与命令行 pi config 等价；项目规则仍可覆盖，改动在下次新建会话或重启后生效。";
	public const string SettingsInstalledExtensions = "已安装扩展";
	public const string SettingsInstalledExtensionsHint =
		"按 pi 约定发现的全局与项目扩展。开关写入全局 settings.json 的 extensions（+/- 规则）；项目规则仍可覆盖。失败或需要转译的扩展会写出原因。";
	public const string SettingsExtensionsEnabled = "已启用";
	public const string SettingsExtensionsDisabled = "已停用";
	public const string SettingsExtensionsUnavailable = "不可用";
	public const string SettingsExtensionsEmpty = "没有发现扩展。把 .js 文件放进 ~/.pi/agent/extensions 或项目的 .pi/extensions。";
	public const string SettingsExtensionsRestart = "改动在下次新建会话或重启后生效。";
	public const string SettingsExtensionsRefresh = "刷新";
	public const string SettingsExtensionsSaved = "扩展开关已保存。";

	public const string SettingsBuiltinMcpHint = "内置 MCP：读取 mcp.json 并把服务器工具提供给模型。";
	public const string SettingsBuiltinLlamaHint = "本地 llama.cpp provider 支持。";
	public const string SettingsBuiltinCodemodeHint = "codemode 脚本工具；MCP 默认 exposure 依赖它。";
	public const string SettingsBuiltinToolSearchHint = "tool_search 工具；deferred exposure 的服务器依赖它。";

	public const string McpBuiltinDisabled = "内置扩展 mcp 已在 settings.extensions 中停用；未连接 MCP 服务器。";
	public const string McpDisabledByFlag = "命令行参数停用了 MCP；未连接服务器。";

	public static string SettingsExtensionsFile(string path) => "配置文件：" + path;

	public static string SettingsExtensionsFailed(string reason) => "扩展开关保存失败：" + reason;
}
