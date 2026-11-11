namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string SettingsExtensions = "扩展";
	public const string SettingsBuiltinExtensions = "Pi 内置扩展";
	public const string SettingsBuiltinExtensionsHint =
		"Pi 随 CLI 分发，默认加载的内置扩展。开关写入全局 settings.json 的 extensions（builtin: 条目），与命令行 pi config 等价；项目规则仍可覆盖，改动在下次新建会话或重启后生效。";
	public const string SettingsDesktopExtensions = "桌面内置扩展";
	public const string SettingsDesktopExtensionsHint =
		"PiDeck 随桌面分发的内置扩展，本端口由 C# 原生实现，随应用提供，不能单独关闭。";
	public const string SettingsDesktopExtensionBadge = "内置";
	public const string SettingsDesktopExtensionNative = "原生实现 · 随应用提供";
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
	public const string SettingsBuiltinSubagentHint = "delegate 系列工具：把任务交给子代理，最终回复回到父会话。";

	public const string DesktopExtensionRequestSizeRecovery =
		"请求体超限自动恢复：识别网关 413/请求体过大错误，允许临时切换大上限模型压缩，解除无法发送的死锁。";
	public const string DesktopExtensionAskQuestion =
		"统一提问卡片：单选、多选、确认与输入，多问题时以 Tab 展开，支持自定义输入。";
	public const string DesktopExtensionGoalMode =
		"目标模式：围绕一条目标自动续轮，直到完成、阻塞、暂停或到达轮次上限。";
	public const string DesktopExtensionLinkedSessions =
		"关联会话：开一个完整后台会话做独立任务，每轮结束回报父会话。";
	public const string DesktopExtensionNulRedirectFix =
		"NUL 重定向修复：把 Windows 的 > nul 改写为 /dev/null，避免 Git Bash 产生删不掉的 nul 文件。";
	public const string DesktopExtensionPlanMode =
		"计划模式：只读探索并产出编号计划，确认后恢复写工具，[DONE:n] 驱动进度。";
	public const string DesktopExtensionRetryNoBody =
		"瞬态错误重试：网关空响应、400/413 等落在 pi 默认名单外的错误按可重试处理。";
	public const string DesktopExtensionSecurityGate =
		"安全门：按策略在工具调用前拦截或确认危险操作。";
	public const string DesktopExtensionTodo =
		"分支级待办：工作计划的增删改查与 widget 展示。";
	public const string DesktopExtensionTrashGuard =
		"回收站守卫：删除前把字面量目标复制进系统回收站，删除照常放行。";

	public const string McpBuiltinDisabled = "内置扩展 mcp 已在 settings.extensions 中停用；未连接 MCP 服务器。";
	public const string McpDisabledByFlag = "命令行参数停用了 MCP；未连接服务器。";

	public static string SettingsExtensionsFile(string path) => "配置文件：" + path;

	public static string SettingsExtensionsFailed(string reason) => "扩展开关保存失败：" + reason;
}
