namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string ConfigTitle = "配置管理";
	public const string SystemSettings = "系统设置";
	public const string ConfigModels = "模型";

	public static string ConfigModelMeta(long context, long maxTokens) => $"上下文 {context:N0} · 输出 {maxTokens:N0}";

	public const string ConfigNoProvidersHint = "添加供应商后可以拉取模型列表，或手动填写模型 ID。";
	public const string ConfigAuth = "认证";
	public const string ConfigSettings = "Agent 设置";
	public const string ConfigSource = "源文件";
	public const string ConfigScope = "保存到";
	public const string ConfigPickModel = "从可用模型选择";
	public const string ConfigAutoSelect = "自动选择";
	public const string ConfigGlobal = "全局";
	public const string ConfigProject = "当前项目";
	public const string ConfigProvider = "供应商";
	public const string ConfigAddProvider = "添加供应商";
	public const string ConfigAddModel = "添加模型";
	public const string ConfigEditProvider = "编辑供应商";
	public const string ConfigEditModel = "编辑模型";
	public const string ConfigDuplicate = "复制供应商";
	public const string ConfigProviderInfo = "供应商信息";

	public const string ConfigModelInfo = "模型信息";

	public const string ConfigModelInfoHint = "模型 ID 是发给供应商的值；名称只影响界面显示。";

	public const string ConfigModelNameHint = "可留空，列表里就显示模型 ID。";

	public const string ConfigContextHint = "该模型一次能接收的最大 token 数。";

	public const string ConfigMaxTokensHint = "单次回复的最大输出 token 数。";

	public const string ConfigReasoningHint = "支持思考或推理输出。";

	public const string ConfigVisionHint = "支持图片输入。";

	public const string ConfigPricing = "价格";

	public const string ConfigPricingHint = "每百万 token 的美元价格；0 表示未提供。";

	public const string ConfigProviderNamePlaceholder = "例如 openrouter";

	public const string ConfigBaseUrlPlaceholder = "https://api.example.com/v1";

	public const string ConfigKeyPlaceholder = "sk-…";

	public const string ConfigModelIdPlaceholder = "例如 gpt-4.1";

	public const string ConfigModelNamePlaceholder = "显示名称（可选）";

	public const string ConfigNewKeyHint = "写入 models.json；也可以稍后在认证页设置。";

	public const string ConfigName = "名称";
	public const string ConfigBaseUrl = "API 地址";
	public const string ConfigApi = "API 协议";
	public const string ConfigKey = "API Key";
	public const string ConfigKeepKey = "留空保留已有密钥";
	public const string ConfigModelId = "模型 ID";
	public const string ConfigContext = "上下文窗口";
	public const string ConfigMaxTokens = "最大输出";
	public const string ConfigReasoning = "推理";
	public const string ConfigVision = "图片输入";
	public const string ConfigSave = "保存";
	public const string ConfigReload = "重新加载";
	public const string ConfigImport = "导入 JSON";
	public const string ConfigExport = "导出模型（不含密钥）";
	public const string ConfigOpenFile = "在编辑器中打开";
	public const string ConfigNoProviders = "尚未配置自定义供应商";
	public const string ConfigNoAuth = "尚未保存凭据";
	public const string ConfigShowSource = "显示源文件（可能含密钥）";

	public const string ConfigShowSourceHint = "凭据以明文存在文件里，只在需要手改时打开。";

	public const string ConfigShowSourceLockedHint = "编辑中不能隐藏源文件；保存或取消后再切换。";

	public const string ConfigSourceContent = "文件内容";

	public const string ConfigSourceDescription = "直接编辑 JSON 原文；保存后按普通保存流程校验并写盘。";

	public const string ConfigSourceFile = "要查看的文件";

	public const string ConfigSourceEditorHint = "保存时会校验 JSON 格式；含密钥的行不会进日志。";

	public const string ConfigSourceHiddenTitle = "源文件已隐藏";

	public const string ConfigSourceHiddenHint = "打开上面的开关后显示并编辑 JSON 原文。";
	public const string ConfigDirty = "未保存";
	public const string ConfigSaved = "配置已保存";
	public const string ConfigApplied = "模型目录已更新；已打开的会话继续使用原设置，新会话使用新配置。";
	public const string ConfigDiscard = "放弃尚未保存的修改？";
	public const string ConfigConflict = "配置文件已被其他程序修改或正在写入，请重新加载后再保存。";
	public const string ConfigAuthGlobalOnly = "认证只保存在全局 auth.json。";
	public const string ConfigInvalidPath = "配置文件路径不匹配。";
	public const string ConfigTooLarge = "配置文件不能超过 2 MiB。";
	public const string ConfigObjectRequired = "JSON 顶层必须是对象。";
	public const string ConfigInvalidJson = "JSON 格式无效，请检查源文件。";
	public const string ConfigProvidersRequired = "providers 必须是供应商对象。";
	public const string ConfigModelsRequired = "models 必须是模型数组。";
	public const string ConfigModelIdRequired = "模型必须有非空且不重复的 ID。";
	public const string ConfigPositiveNumber = "上下文和输出上限必须是正整数，输出上限不能大于上下文。";
	public const string ConfigMcpRequired = "mcpServers 必须是对象。";
	public const string ConfigNameRequired = "名称不能为空或含控制字符。";
	public const string ConfigUnsupportedApi = "请选择已支持的 API 协议。";
	public const string ConfigUrlRequired = "请输入不含用户名密码的 HTTP 或 HTTPS API 地址。";
	public const string ConfigNameExists = "名称或模型 ID 已存在。";
	public const string ConfigKeyRequired = "请输入 API Key。";
	public const string ConfigOAuthProtected = "此供应商使用 OAuth，不能用 API Key 覆盖。";
	public const string ConfigFailed = "操作失败，请检查文件权限和 JSON 格式。";
	public const string ConfigSetKey = "保存 API Key";
	public const string ConfigAppearance = "外观";
	public const string ConfigOverview = "概览";
	public const string ConfigInherit = "继承 / 默认";
	public const string ConfigAutoCompaction = "自动压缩上下文";
	public const string ConfigAutoRetry = "自动重试";
	public const string ConfigShowCacheMissNotices = "提示被丢弃的思考块与缓存未命中";
	public const string ConfigShowCacheMissNoticesHint = "对应 settings.showCacheMissNotices：供应商剔除历史思考块、或本轮明显没命中提示词缓存时，在会话里留一行提示。完整清单仍在会话文件里。默认关闭。";
	public const string ConfigRetryMaxRetries = "最大重试次数";
	public const string ConfigRetryBaseDelay = "基础延迟 (ms)";
	public const string ConfigRetryHint = "只对网络抖动、限流与 5xx 生效，配额与账单错误不重试。退避按指数增长。";
	public const string ConfigRetryMaxRetriesInvalid = "最大重试次数必须是 0–20 的整数。";
	public const string ConfigRetryBaseDelayInvalid = "基础延迟必须是 0–600000 的整数（毫秒）。";
	public const string ConfigMcpGroup = "服务器";

	public const string ConfigAddMcp = "添加服务器";
	public const string ConfigToolsTitle = "工具与 Codemode";
	public const string ConfigToolsHint = "工具与 codemode 的设置，改动写入 settings.json。";

	public const string ConfigBehaviorModel = "模型与推理";
	public const string ConfigBehaviorAutomation = "自动化";
	public const string ConfigBehaviorNotices = "提示";
	public const string ConfigToolsDefaults = "默认工具";
	public const string ConfigToolsCodemode = "Codemode";
	public const string ConfigGoalBudget = "预算";
	public const string ConfigDefaultTools = "默认工具";
	public const string ConfigToolsGroupBuiltin = "内置工具";
	public const string ConfigToolsGroupExtension = "内置扩展工具";
	public const string ConfigDefaultToolsHint = "在 Pi 默认集上增减。codemode 与 tool_search 还要在「扩展」里打开对应的内置开关才可用。";
	public const string ConfigToolDisabledByExtension = "已在「扩展」中停用";
	public static string ConfigToolsBlockedNote(string names) => names + " 已在「扩展」中停用，运行时不会加载。";
	public const string ConfigToolsUsingDefault = "使用 Pi 默认集";
	public const string ConfigToolsReset = "恢复默认";
	public const string ConfigInheritGlobal = "继承全局";
	public const string ConfigToolsDisableAll = "全部禁用";
	public const string ConfigToolsAllDisabled = "已禁用全部内置工具：会话将没有任何工具可用。";
	public const string ConfigToolAdd = "添加工具";
	public const string ConfigCodemodeMode = "Codemode 模式";
	public const string ConfigCodemodeModeHint = "on：脚本与直调并存（默认）。only：模型只能经脚本调工具。扩展可用且工具已激活时生效。";
	public const string ConfigCodemodeBudget = "Codemode 声明预算";
	public const string ConfigCodemodeBudgetHint = "codemode 描述里工具声明的 token 预算，默认 3000；0 表示只列命名空间。留空用默认。";
	public const string ConfigCodemodeBudgetInvalid = "codemode 声明预算必须是非负整数。";
	public const string ConfigSubagentsTitle = "子代理";
	public const string ConfigSubagentsHint = "delegate 系列的配额，写入 settings.json 的 subagents。角色文件里的 timeoutMinutes / maxTokens 优先于这里的默认值。写者上限只作用于新启动的写子代理：同一工作目录已有 N 个在跑时，第二个直接拒绝。";
	public const string ConfigSubagentsMaxActive = "同时活跃上限";
	public const string ConfigSubagentsMaxSpawns = "每回合启动上限";

	public const string ConfigSubagentsMaxWriters = "同时写者上限（0 为不限）";

	public const string ConfigSubagentsConcurrency = "并发与写者";

	public const string ConfigSubagentsConcurrencyHint = "同时活跃上限、每回合启动上限与同一工作目录的写者上限。写者为 0 时不限；开严格模式后第二个写者会被拒绝，直到前一个结束。";

	public const string ConfigSubagentsRuntime = "运行预算";

	public const string ConfigSubagentsRuntimeHint = "单次运行的时限、token 上限与每条验证命令的超时。角色文件里的 timeoutMinutes / maxTokens 优先于这里的默认值。";

	public const string ConfigSubagentsUi = "界面与协作";
	public const string ConfigSubagentsRunTimeout = "单次运行时限（分钟）";
	public const string ConfigSubagentsMaxTokens = "单次运行 token 上限（0 为不限）";
	public const string ConfigSubagentsVerifyTimeout = "验证命令超时（秒）";
	public const string ConfigSubagentsOpenTabs = "启动子代理时打开标签";
	public const string ConfigSubagentsOpenTabsHint = "默认关闭：子代理在后台跑，卡片行按需打开活的转录，标签栏不会被并行委派占满。";

	public const string ConfigSubagentsTeam = "启用共享任务板";

	public const string ConfigSubagentsTeamHint = "默认关闭：开启后父会话与子代理拿到 team_task_* 工具，任务按 owner、依赖与 revision 协作；普通委派不需要它。";
	public static string ConfigSubagentsInvalid(string field) => $"{field}超出范围：并发 1–32、每回合 1–512、写者 0–16、时限 1–1440 分钟、token 0–10000000、验证超时 1–86400 秒。";

	public const string ConfigGoalTitle = "目标模式";
	public const string ConfigGoalHint = "写入 settings.json 的 goal 段，只影响新建目标；已存在的目标保留自己的上限。";
	public const string ConfigGoalMaxRounds = "默认轮次上限（1–512）";
	public const string ConfigGoalMaxRoundsHint = "达到上限时目标条显示「再加 10 轮」，恢复会先加预算再继续。";
	public const string ConfigGoalMaxRoundsInvalid = "默认轮次上限超出范围：1–512。";
	public const string ConfigGoalMaxTokens = "默认 token 预算（0 = 不限）";
	public const string ConfigGoalMaxTokensHint = "只影响新建目标：累计 token 达到预算后目标阻塞，恢复不会自动加预算。";
	public const string ConfigGoalMaxTokensInvalid = "默认 token 预算超出范围：0–10000000。";
	public static string ConfigToolsEnabledCount(int count) => $"{count} 个已启用";
	public static string ConfigRemoveTool(string name) => $"移除工具 {name}";
	public const string ConfigEditMcp = "编辑服务器";
	public const string ConfigMcpServerInfo = "服务器信息";

	public const string ConfigMcpConnection = "连接";

	public const string ConfigMcpConnectionHint = "stdio 填命令与参数；HTTP 填服务地址。";

	public const string ConfigCommandHint = "可执行文件或命令名，例如 npx。";

	public const string ConfigArgumentsHint = "JSON 字符串数组，例如 [\"-y\", \"server\"]。";

	public const string ConfigUrlHint = "http/https 端点；只用于展示与连接。";

	public const string ConfigMcpNamePlaceholder = "例如 filesystem";

	public const string ConfigCommandPlaceholder = "npx";

	public const string ConfigArgumentsPlaceholder = "[\"-y\", \"server\"]";

	public const string ConfigUrlPlaceholder = "https://example.com/mcp";

	public const string ConfigEnabled = "启用";
	public const string ConfigTransport = "传输";
	public const string ConfigCommand = "命令";
	public const string ConfigArguments = "参数（JSON 数组）";
	public const string ConfigMcpName = "MCP 名称只能包含字母、数字、下划线和连字符。";
	public const string ConfigCommandRequired = "stdio 服务器必须指定可执行命令。";
	public const string ConfigArgumentsRequired = "参数必须是字符串 JSON 数组。";
	public const string ConfigMcpRestart = "MCP 配置已保存，下次启动时生效。";
	public static string ConfigInvalidValue(string field) => $"字段 {field} 的类型无效。";
	public const string ConfigTheme = "主题";
	public static string ConfigDelete(string name) => $"删除「{name}」？";
	public static string ConfigProviderCount(int count) => $"{count} 个自定义供应商";
	public static string ConfigModelCount(int count) => $"{count} 个模型";
	public static string ConfigCredentialType(string name, string type) => $"{name} · {type}";
}
