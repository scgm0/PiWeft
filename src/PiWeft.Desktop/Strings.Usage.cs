namespace PiWeft.Desktop;

public static partial class Strings
{
	// ── 用量查询：标题 / 通用操作 ──────────────────────────────────────────
	public const string ProviderUsage = "用量查询";
	public const string UsageTitle = "用量查询";
	public const string UsageConfigure = "配置用量查询";
	public const string UsageRefresh = "刷新用量";
	public const string UsageLoading = "正在查询用量…";
	public const string UsageOpenFile = "打开配置文件";
	public const string UsageSave = "保存";
	public const string UsageCancel = "取消";
	public const string UsageClose = "关闭";
	public const string UsageSavedToast = "用量查询配置已保存";

	// ── 状态与错误 ────────────────────────────────────────────────────────
	public const string UsageDisabled = "用量查询未开启。";
	public const string UsageUnavailable = "用量暂时不可用，请检查接口与字段配置。";
	public const string UsageUnsupported = "该供应商暂不支持自动查询用量，可在弹窗里手动选择模板。";
	public const string UsageMissingKey = "缺少供应商凭据，请配置 API Key 或查询令牌。";
	public const string UsageResponseTooLarge = "用量接口响应超过大小上限。";
	public const string UsageTimeout = "用量查询超时。";
	public const string UsageHttpPrefix = "用量接口 HTTP ";
	public const string UsageInvalidConfig = "用量查询配置无效，请检查地址、字段路径与数值范围。";
	public const string UsageNoBaseUrl = "供应商未配置请求地址，无法查询用量。";
	public static string UsageHttpError(int status) => $"{UsageHttpPrefix}{status}";

	// ── 结果展示 ──────────────────────────────────────────────────────────
	public const string UsageBalanceShort = "余额";
	public const string UsageRemainingShort = "剩余";
	public const string UsageUsedShort = "已用";
	public const string UsageRemaining = "剩余";
	public const string UsageCreditsRemaining = "剩余";
	public const string UsageCreditsUsed = "已用";
	public const string UsageWindowFiveHour = "5 小时";
	public const string UsageWindowDaily = "今日";
	public const string UsageWindowWeekly = "本周";
	public const string UsageWindowMonthly = "本月";
	public const string UsageWindowMcpMonthly = "MCP 月额度";
	public const string UsageWindowIncluded = "套餐内";
	public const string UsageWindowOnDemand = "按需";
	public const string UsageWindowRolling = "滚动";
	public const string UsageWindowPrimary = "主窗口";
	public const string UsageWindowSecondary = "次窗口";
	public const string UsageBoosterBalance = "Boost 余额";
	public const string UsageBoosterMonthlyUsed = "本月已用";
	public const string UsageBoosterMonthlyLimit = "月限额";
	public const string UsageBoosterUnlimited = "不限额";
	public const string UsageResetAt = "重置";
	public const string UsageTimeJustNow = "刚刚";
	public static string UsageTimeMinutes(int value) => $"{value} 分钟前";
	public static string UsageTimeHours(int value) => $"{value} 小时前";
	public static string UsageTimeDays(int value) => $"{value} 天前";

	// ── 配置弹窗：模板与字段 ──────────────────────────────────────────────
	public const string UsageEnable = "启用用量查询";
	public const string UsageTemplates = "预设模板";
	public const string UsageTemplateNone = "自动识别";
	public const string UsageTemplateGeneral = "通用";
	public const string UsageTemplateNewApi = "New API";
	public const string UsageTemplateCookie = "Cookie";
	public const string UsageTemplateSub2Api = "Sub2API";
	public static string UsageTemplateBuiltin(string category) => $"已内置 · {category}";
	public const string UsageCategoryBalance = "余额";
	public const string UsageCategoryPlan = "套餐";
	public const string UsageCategorySubscription = "订阅";
	public const string UsageHintNone = "不选模板：按供应商地址匹配内置适配，并合并 probes 里的手写探针。";
	public const string UsageHintBuiltin = "该供应商已内置适配，无需填写字段；启用后即可查询。";
	public const string UsageHintGeneral = "适用于实现 OpenAI 兼容 /usage（{ balance, unit }）的网关；凭据与地址留空时用供应商配置。";
	public const string UsageHintNewApi = "New API / OneAPI 中转站：访问令牌在「个人设置 → 安全」生成，积分换算为美元。";
	public const string UsageHintCookie = "自研网关的网页后台接口：需要登录 Cookie（F12 → Network → 请求头），不能用 API Key。";
	public const string UsageHintSub2Api = "Sub2API 自部署网关：钱包、订阅、Key 额度按响应分流，凭据沿用供应商 API Key。";
	public const string UsageApiKey = "API Key（留空使用供应商凭据）";
	public const string UsageApiKeyPlaceholder = "留空自动使用供应商配置";
	public const string UsageBaseUrl = "请求地址（留空使用供应商地址）";
	public const string UsageBaseUrlPlaceholder = "https://example.com/v1";
	public const string UsageAccessToken = "访问令牌";
	public const string UsageAccessTokenPlaceholder = "站点「个人设置 → 安全」生成的令牌";
	public const string UsageNewApiUserId = "用户 ID";
	public const string UsageNewApiUserIdPlaceholder = "站点个人设置里的数字 ID";
	public const string UsageCookieLabel = "Cookie";
	public const string UsageCookiePlaceholder = "F12 → Network → 请求头 Cookie";
	public const string UsageCookiePath = "接口路径";
	public const string UsageCookiePathPlaceholder = "/api/wallet/summary";
	public const string UsageValuePath = "余额字段路径";
	public const string UsageValuePathPlaceholder = "data.availableBalanceCny";
	public const string UsageCurrencyPath = "币种字段路径（可选）";
	public const string UsageCurrencyPathPlaceholder = "data.currency";
	public const string UsageTimeoutSeconds = "超时（秒）";
	public const string UsageIntervalMinutes = "自动查询间隔（分钟，0 = 不自动）";
	public const string UsageTest = "测试";
	public const string UsageTesting = "正在测试…";
	public const string UsageTestSuccess = "测试成功";
	public const string UsageTestFailed = "测试失败";
	public const string UsageLegacyNotice = "检测到旧版 usage-queries.json：自动识别模式下优先使用，选模板或删旧文件后停用。";
	public const string UsageNewApiNeeds = "New API 模板需要访问令牌和用户 ID。";
	public const string UsageCookieNeeds = "Cookie 模板需要 Cookie、接口路径和余额字段路径。";

	// ── 配置读取 / 旧探针校验 ─────────────────────────────────────────────
	public static string UsageConfigReadFailed(string error) => $"usage-probes.json 读取失败：{error}";
	public static string UsageConfigLegacyReadFailed(string error) => $"usage-queries.json 读取失败：{error}";
	public static string UsageConfigProviderInvalid(string name) => $"providers.{name}：配置无效，已忽略。";
	public static string UsageConfigProviderInvalid(string name, string error) => $"providers.{name}：{error}";
	public const string UsageConfigProvidersRequired = "providers 必须是对象。";
	public const string UsageConfigProbesRequired = "probes 必须是数组。";
	public static string UsageConfigProbeNotObject(int index) => $"第 {index} 条探针不是对象。";
	public static string UsageConfigProbeMatch(int index) => $"第 {index} 条探针缺少 match.baseUrlContains。";
	public static string UsageConfigProbePath(int index) => $"第 {index} 条探针缺少 request.path（必须以 / 开头）。";
	public static string UsageConfigProbeMethod(int index) => $"第 {index} 条探针的 request.method 只能是 GET 或 POST。";
	public static string UsageConfigProbeParse(int index) => $"第 {index} 条探针的 parse 配置非法（balance 需 valuePath；credits 至少一个路径或窗口）。";
	public static string UsageConfigProbeCustom(int index) => $"第 {index} 条探针使用了 kind:\"custom\"（仅限内置，不支持用户配置）。";
	public static string UsageConfigLegacyCrossOrigin(string provider) => $"providers.{provider}：旧版配置的异域地址缺少凭据，已忽略。";

	// ── 失败归因（尝试明细） ──────────────────────────────────────────────
	public const string UsageAttemptsTitle = "尝试明细";
	public const string UsageHintTitle = "排查提示：";
	public const string UsageHintAuth = "鉴权失败：检查 API Key、访问令牌或 Cookie 是否有效。";
	public const string UsageHintNotFound = "接口地址不存在：检查请求地址与接口路径。";
	public const string UsageHintShape = "接口返回 200 但结构不符：疑似接口变更，请重新抓包核对字段路径。";
	public const string UsageHintServer = "服务端错误：稍后重试或检查网关状态。";
	public const string UsageHintTimeout = "网络超时或不可达：检查网络、代理与超时设置。";
	public const string UsageHintMixed = "多次尝试均失败：对照下方状态码逐条排查地址与鉴权。";
	public const string UsageAttemptTimeout = "超时";
	public const string UsageAttemptNetwork = "网络错误";
	public const string UsageAttemptShape = "HTTP 200（响应结构与预期不符）";
}
