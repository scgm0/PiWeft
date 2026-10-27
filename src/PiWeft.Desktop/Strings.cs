namespace PiWeft.Desktop;

/// <summary>
/// 界面文案。中文是这里的第一方语言，其它语言按需增加；界面代码只引用这里的常量，不再出现字面量。
/// </summary>
public static partial class Strings
{
	// 窗口
	public static string WindowTitle(string cwd) => $"PiWeft Desktop — {cwd}";

	// 侧栏
	public const string OpenProject = "打开项目";
	public const string RecentProjects = "最近项目";
	public const string OpenProjectDirectory = "打开工作目录";
	public const string ProjectOpened = "已打开项目窗口。";
	public const string SelectModel = "选择模型";
	public const string ModelSearchPlaceholder = "搜索模型、供应商或能力…";
	public const string RefreshModels = "刷新可用模型";
	public const string ModelsCountUnit = "个模型";
	public const string ModelNoneAvailable = "没有可用模型。请在模型设置中配置供应商和凭据，再刷新列表。";
	public const string ModelNoMatch = "没有匹配的模型。";
	public const string ModelVision = "图片";
	public const string ModelReasoning = "推理";
	public const string ModelTextOnly = "文本";
	public const string CurrentModel = "当前";
	public const string RecentModel = "最近";
	public const string CancelEdit = "取消编辑";
	public const string CopyUserMessage = "复制这条提问";
	public const string CloseTabsToRight = "关闭右侧标签";
	public const string KeepCurrentTab = "保留此标签";
	public static string ModelResultCount(int shown, int total) => $"{shown} / {total} 个模型";

	/// <summary>会话搜索结果超过有界列表时提示截断，并给出继续缩小范围的建议。</summary>
	public static string SessionSearchTruncated(int shown, int total) => $"显示前 {shown} / {total} 个会话，继续输入以缩小范围";
	public static string ProjectOpenFailed(string reason) => $"打开项目失败：{reason}";
	public static string ModelCapabilities(PiWeft.Ai.Model model) =>
		$"{model.Provider} · {model.ContextWindow / 1000.0:0.#}k · {(model.SupportsImages ? ModelVision : ModelTextOnly)}{(model.Reasoning ? $" · {ModelReasoning}" : string.Empty)}";
	public const string NewSession = "新建";
	/// <summary>还没有内容也没有命名的会话在列表里的标题。</summary>
	public const string UntitledSession = "未命名会话";
	public const string Rename = "重命名";
	public const string TogglePin = "置顶";
	public static string Pinned(string name) => $"已置顶「{name}」。";
	public static string Unpinned(string name) => $"已取消置顶「{name}」。";
	public const string Delete = "删除";
	public const string SessionNamePlaceholder = "会话名称（可留空清除）";
	public const string SelectSessionFirst = "请先在左侧选择一个会话。";
	public static string DeleteConfirm(string name) => $"把会话「{name}」移到 .trash 目录？文件会被保留，可手动恢复。";
	public static string Deleted(string path) => $"已移动到 {path}";
	public const string NoSessions = "此目录下暂无会话，点「新建」开始一个。";
	public const string SessionsScanning = "正在读取会话列表…";
	/// <summary>侧栏入口与搜索弹窗的输入提示共用一个短名。</summary>
	public const string SessionSearch = "搜索会话";
	public static string EnvironmentFallback(string cwd, string reason) => $"目录 {cwd} 的环境不可用（{reason}），已回退到启动环境。";
	public const string NoMatches = "没有匹配的会话。";
	public const string ContentHit = "内容命中";

	// 输入区
	public const string ComposerPlaceholder = "输入消息，@ 引用文件，/ 运行命令，& 引用会话…";
	public const string Send = "发送";
	public const string Abort = "中断";
	public const string CopyLastReply = "复制回复";
	public const string CopyMessage = "复制这条回复";
	public const string Retry = "重试";
	public const string BranchHere = "在此分支";
	public const string EditAndResend = "编辑重发";
	public const string EditPending = "已载入该提问：修改后发送会在新分支上继续。";
	public static string Branched(string path) => $"已在该处开新分支：{path}";
	public static string BranchFailed(string reason) => $"分支失败：{reason}";
	public const string CloseCurrentTab = "关闭标签";
	public const string Copied = "已复制。";
	public const string CopyImage = "复制图片";
	public const string NothingToCopy = "还没有可复制的回复。";
	public const string ClipboardUnavailable = "剪贴板不可用。";
	public const string ClipboardImageUnavailable = "剪贴板里读不到可用的图片。";
	public const string BrokenImage = "（图片数据无法解码）";
	public const string PaletteButton = "命令";

	/// <summary>输入框工具条上的 @ 引用按钮。</summary>
	public const string ComposerReference = "引用文件（@）";
	public const string PinnedMark = "★";
	public const string RemoveAttachment = "移除";
	public static string AttachFailed(string reason) => $"附加图片失败：{reason}";
	public static string SendFailed(string reason) => $"发送失败：{reason}";

	// 顶栏与欢迎页
	public const string AppName = "PiWeft Desktop";

	/// <summary>标题栏上的短品牌名；完整名在关于与自检里。</summary>
	public const string BrandShort = "PiWeft";

	public const string MinimizeWindow = "最小化";
	public const string MaximizeWindow = "最大化";
	public const string RestoreWindow = "还原";
	public const string CloseWindow = "关闭窗口";
	public const string WelcomeTitle = "开始一次会话";
	public const string WelcomeBody = "左侧列出该目录下的历史会话；点「新建」开始一个，或用搜索框按名称、目录、编号查找。";

	// 设置面板
	public const string Settings = "设置";
	public const string ToggleTheme = "主题";
	public const string PalettePlaceholder = "输入命令名或关键字…";
	public const string PaletteNoMatch = "没有匹配的命令。";
	public const string ThemeLight = "浅色";
	public const string ThemeDark = "深色";
	public const string ThemeSystem = "跟随系统";
	public static string ThemeChanged(string theme) => $"已切换到{theme}主题。";
	public static string ThemeChangeFailed(string reason) => $"主题偏好写入失败：{reason}";
	public const string SettingsTitle = "设置与环境";
	public const string SettingsEnvironment = "运行环境";
	public const string SettingsLogs = "日志";

	// 性能诊断（对齐 PiDeck 设置里的同名开关）
	public const string SettingsDiagnostics = "性能诊断";
	public const string DiagnosticsDesc =
		"三档采样：帧档（每帧约 0.1 µs、零分配，给帧间隔 p50/p95、掉帧/长帧/停顿、每帧分配、GC 占比、输入→绘制延迟与绘制量）、" +
		"计数档（行构建/回收、重建轮次等 churn 次数）、明细档（打开会话、投影、慢帧、Markdown 定稿等关键耗时，逐条落盘）。" +
		"数据写到应用数据目录的 diagnostics 文件夹，并每秒追加一行帧档摘要，用来做优化前后对照与追查卡顿；" +
		"默认关闭，关闭时调用点只剩一次布尔判断。";
	public const string DiagnosticsRefresh = "刷新采样";
	public const string DiagnosticsOpenFolder = "打开诊断目录";
	public const string DiagnosticsReset = "清空统计";
	public const string DiagnosticsMemory = "内存";
	public const string DiagnosticsQueueLag = "UI 队列延迟";
	public const string DiagnosticsOff = "采样未开启：打开开关后再操作一次，就能看到各阶段的耗时。";
	public const string DiagnosticsNoData = "还没有记录到耗时；开关打开后的操作才会被记下来。";
	public const string DiagnosticsStatsHeader = "按名称汇总（次数 · 总耗时 · 最大 · 最近）";
	public const string DiagnosticsRecentHeader = "最近耗时";
	public const string DiagnosticsPath = "耗时文件";
	public const string DiagnosticsStarted = "性能诊断已开始采样";
	public const string DiagnosticsStopped = "性能诊断已停止";
	public const string DiagnosticsFrameHeader = "帧档（每帧约 0.1 µs、零分配）";
	public const string DiagnosticsOverview = "概览";
	public const string DiagnosticsMemoryHeader = "内存与 GC";
	public const string DiagnosticsMemoryHint =
		"托管堆只会在 GC 需要时才回收：gen2 次数几乎不动而堆持续变大，是「还没收」而不是「漏了」。" +
		"要判定泄漏，点「压一次堆」看剩余；同一个操作重复做，剩余一路抬高才是泄漏。";
	public const string DiagnosticsLoopHeader = "渲染循环";
	public const string DiagnosticsLoopHint =
		"FPS 由帧间隔中位数换算：开局是背靠背的连续重绘，稳定后只剩按请求重绘的那几帧，两者不可直接比。";
	public const string DiagnosticsCompact = "压一次堆";
	public const string DiagnosticsCompactHint = "完整回收并显示剩余托管堆";
	public static string DiagnosticsCompactResult(long before, long after, double pauseMs) =>
		$"回收前 {FormatBytes(before)} → 剩余 {FormatBytes(after)}（GC 用了 {pauseMs:0} ms）";
	public const string LabelWorkingSet = "工作集";
	public const string LabelHeap = "托管堆";
	public const string LabelLoh = "大对象堆";
	public const string LabelAllocated = "累计分配";
	public const string LabelCommitted = "提交内存";
	public const string LabelFragmented = "堆碎片";
	public const string LabelGcCounts = "GC 次数";
	public const string LabelGcPause = "GC 暂停占比";
	public const string LabelFinalizers = "待终结对象";
	public const string LabelPinned = "固定对象";
	public const string LabelCpu = "CPU 占比";
	public const string LabelPool = "线程池";
	public const string LabelQueueLag = "UI 队列延迟";
	public const string LabelInputLatency = "输入→绘制";
	public const string LabelFrameGap = "帧间隔";
	public const string LabelFrameBudget = "预算 16.7 ms";
	public const string LabelFrameCounts = "掉帧 / 长帧 / 停顿";
	public const string LabelAllocPerFrame = "每帧分配";
	public const string LabelDrawCalls = "绘制调用";
	public const string DiagnosticsCountersHeader = "事件计数（帧档与 churn 档）";
	public static string FormatBytes(long bytes) => bytes switch
	{
		>= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
		>= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
		>= 1024 => $"{bytes / 1024.0:0} KiB",
		_ => $"{bytes} B",
	};

	public const string OpenLogDirectory = "打开日志目录";
	public const string CopyLogDirectory = "复制日志目录";
	public const string LogLevel = "日志级别";
	public const string SettingsAgentDir = "agent 目录";
	public const string SettingsCwd = "工作目录";
	public const string SelfCheckUiFont = "内嵌正文字体";
	public const string SelfCheckMonoFont = "内嵌等宽字体";
	public const string SelfCheckLocalization = "框架文案";
	public const string SettingsModels = "可用模型";
	public const string SettingsTools = "当前工具";
	public const string SettingsSkills = "技能";
	public const string SettingsMcp = "MCP 服务器";
	public const string SettingsMcpNone = "没有配置 MCP 服务器。在 <agentDir>/mcp.json 或 <项目>/.pi/mcp.json 中添加。";
	public const string ShortcutUnbound = "未绑定";
	public const string ShortcutsRestart = "快捷键已保存，重启后生效。";
	public const string ShortcutsConflict = "有快捷键冲突：";
	public const string ShortcutsInvalid = "这个组合无法识别，示例：Ctrl+Shift+K、F2、Alt+Enter。";
	public const string ShortcutsEdit = "改键";
	public const string ShortcutsReset = "恢复默认";
	public const string ShortcutsPress = "请按下新的组合键…";
	public const string ShortcutsNeedsModifier = "这个键不能单独做快捷键，请加 Ctrl 或 Alt，或改用 F1–F24。";
	public const string Save = "保存";
	public const string Cancel = "取消";
	public static string ShortcutsIgnored(string ids) => $"配置里有无法识别的快捷键项，已忽略：{ids}";
	public const string ShortcutsEditHint = "点「改键」后直接按新的组合键即可绑定，也可以输入组合串再回车；留空或点「恢复默认」回到默认值。";
	public static string ShortcutsConflictLine(string first, string second, string binding) =>
		$"{binding} 同时绑给了「{first}」和「{second}」";
	public const string MakeDefaultModel = "把当前模型设为默认";
	public static string DefaultModelSaved(string provider, string model) => $"已把 {provider}/{model} 写进全局设置。";
	public const string SettingsShortcuts = "快捷键";
	public const string SettingsAbout = "关于";
	public const string AboutVersion = "版本";
	public const string AboutUiFont = "界面字体";
	public const string AboutMonoFont = "等宽字体";
	public const string AboutFramework = "界面框架";
	public const string AboutIcons = "图标";
	public const string AboutRuntime = "运行时";
	public const string ShortcutPalette = "打开命令面板";
	public const string ShortcutSend = "发送输入框内容";
	public const string ShortcutSearch = "聚焦会话搜索";
	public const string ShortcutSettingsPanel = "打开设置";
	public const string ShortcutGitPanel = "打开 Git 状态";
	public const string ShortcutNew = "新建会话";
	public const string ShortcutClose = "关闭当前标签";
	public const string ShortcutSwitch = "切换到第 N 个标签";
	public const string ShortcutClosePalette = "关闭命令面板或搜索框";
	public const string ShortcutMoveTabLeft = "把当前标签左移一格";
	public const string ShortcutMoveTabRight = "把当前标签右移一格";
	public const string MoveTabLeft = "标签左移";
	public const string MoveTabRight = "标签右移";
	public const string SettingsArchive = "归档（已删除）";
	public const string ArchiveEmpty = "归档为空。删除的会话会移动到这里，可随时恢复。";
	public const string ArchiveRestore = "恢复";
	public const string ArchivePurge = "清空归档";
	public const string ArchivePurgeConfirm = "永久删除归档里的所有会话？此操作不可撤销。";
	public static string ArchiveCount(int count) => $"归档中有 {count} 个会话。";
	public const string SettingsSession = "当前会话";
	public const string SettingsSessionFile = "会话文件";
	public const string SettingsSessionId = "会话 id";
	public const string SettingsModel = "模型";
	public const string SettingsEntries = "条目数";
	public const string SettingsNone = "（无）";
	public static string SettingsMcpFailed(string reason) => $"连接失败：{reason}";
	public static string SettingsMcpTools(int count) => $"已连接，{count} 个工具";

	/// <summary>MCP 重连按钮与提示。</summary>
	public const string SettingsMcpReconnect = "重连 MCP";

	public const string SettingsMcpReconnecting = "正在重连 MCP…";

	public const string SettingsMcpReconnected = "MCP 已重连";

	/// <summary>资源管理（技能 / 提示词模板 / 主题 / pi 包）。</summary>
	public const string SettingsGit = "Git 提交摘要";
	public const string SettingsResources = "资源";
	public const string ResourcesPrompts = "提示词模板";
	public const string ResourcesThemes = "主题";
	public const string ResourcesExtensions = "扩展";

	public const string ResourcesExtensionHint = "按 pi 约定发现的全局与项目扩展。这里只列举，不执行；加载失败或需要转译的扩展会写出原因。";

	public const string ResourcesPackages = "pi 包";

	public const string ResourcesSkillHint = "技能按描述在任务匹配时进入系统提示词；也可以用 /skill:name 显式触发。";
	public const string ResourcesPromptHint = "提示词模板以文件名作为斜杠命令，例如 review.md 对应 /review。";
	public const string ResourcesThemeHint = "主题在「外观」页选择；这里列出已发现的主题文件与解析结果。";
	public const string ResourcesPackageHint = "用 pi install <source> 添加包，或直接编辑 settings.json 的 packages。";
	public const string ResourcesEmpty = "没有发现资源。";
	public const string ResourcesReload = "重载资源";
	public const string ResourcesReloaded = "资源已重载";
	public const string ResourcesOpen = "打开文件";
	public const string ResourcesDiagnostics = "诊断";
	public const string ResourcesInvalid = "无法解析";
	public const string ResourcesLight = "浅色";
	public const string ResourcesDark = "深色";
	public const string ResourcesUnknown = "未知";

	/// <summary>D-07 跨会话用量统计。</summary>
	public const string UsageStatsTitle = "用量统计";

	public const string UsageStatsRefresh = "刷新统计";

	public const string UsageStatsLoading = "正在扫描会话…";

	public const string UsageStatsTimeout = "统计超时";

	public const string UsageStatsUnavailable = "无法读取会话目录。";

	public const string UsageStatsEmpty = "还没有可统计的会话用量。";

	public const string UsageStatsHeatmap = "每日热力图（26 周）";

	public const string UsageStatsHeatmapHint = "颜色越深表示当天令牌数越多。";

	public const string UsageStatsModels = "按模型";

	public const string UsageStatsProjects = "按项目";

	public static string UsageStatsSummary(int days, long messages, string tokens, double cost) =>
		$"{days} 天 · {messages} 条回复 · {tokens} 令牌 · {cost:0.####}";
	public static string McpConnected(int servers, int tools) => $"已连接 {servers} 个 MCP 服务器，共 {tools} 个工具。";

	// 分支树
	public const string BranchPanelTitle = "会话分支";
	public const string BranchPanelHint = "每个条目一行；「跳到此处」把当前叶子移回该条目，之后的对话落在新分支上（不改写历史）。";
	public const string JumpHere = "跳到此处";
	public const string JumpWithSummary = "摘要后跳转";
	public const string BranchEntrySystem = "系统提示";
	public const string BranchEntryCompaction = "压缩检查点";
	public const string BranchEntryBranchSummary = "分支摘要";
	public const string BranchEntryModelChange = "切换模型 →";
	public const string BranchEntryThinkingChange = "切换思考级别 →";
	public const string ViewChat = "对话";
	public const string ViewBranches = "分支";
	public static string JumpFailed(string reason) => $"跳转失败：{reason}";

	// 命令面板的说明与快捷键
	public const string RefreshSessions = "刷新会话列表";
	public const string ShortcutNewSession = "Ctrl+T";
	public const string ShortcutSearchSessions = "Ctrl+K";
	public const string ShortcutSettings = "环境、技能与 MCP 状态";
	public const string ShortcutGit = "当前分支与工作区改动";
	public const string ShortcutRightPanel = "打开或收起当前会话的右侧边栏";
	public const string ShortcutTheme = "立即在深色与浅色之间切换";
	public const string ShortcutRefresh = "重新读取磁盘上的会话";
	public const string ShortcutCloseTab = "Ctrl+W";
	public const string CloseOtherTabs = "关闭其它标签";
	public const string CloseAllTabs = "关闭全部标签";
	public const string ShortcutCloseOtherTabs = "只留下当前会话";
	public const string ShortcutCloseAllTabs = "关闭所有会话标签（设置弹窗不受影响）";
	public const string ShortcutBranches = "查看当前会话的分支树";
	public const string ShortcutChat = "回到对话视图";
	public const string ShortcutSlashCommand = "斜杠命令：填入输入框";
	public static string ShortcutSwitchTo(int index) => $"切换到第 {index} 个标签";

	// 相对时间
	public const string TimeJustNow = "刚刚";
	public static string TimeMinutes(int minutes) => $"{minutes} 分钟前";
	public static string TimeHours(int hours) => $"{hours} 小时前";
	public static string TimeDays(int days) => $"{days} 天前";

	/// <summary>空状态里单独一行显示当前模型与目录。</summary>
	public static string EmptySessionContext(string provider, string model, string cwd) =>
		$"{provider}/{model}\n{cwd}";

	// 空状态与标签
	public const string Commands = "命令";
	public const string SearchInSession = "在对话中搜索";
	public const string ScrollToLatest = "移动到最新";
	public const string Git = "Git 状态";
	public const string ExportSession = "导出会话为 HTML";
	public const string ExportSessionAndOpen = "导出会话并打开";
	public const string ShortcutExportSession = "导出到会话目录，便于分享或归档";
	public const string ShortcutExportAndOpen = "导出后用默认程序打开";
	public static string Exported(string path) => $"已导出：{path}";
	public static string ExportFailed(string reason) => $"导出失败：{reason}";
	public const string GitNotRepository = "不是 Git 仓库";
	public const string GitUnavailable = "找不到 git 命令";
	public const string GitTimeout = "git status 超时";
	public const string GitClean = "工作区干净";
	public const string GitCleanHint = "工作区没有未提交的改动。";
	public const string GitNoDiff = "这个文件没有可显示的 diff。";
	public const string LinkUnsupported = "无法打开此链接：仅支持网页和本地文件路径。";
	public const string OpenInEditor = "在编辑器中打开";
	public static string OpenedInEditor(string path) => $"已用编辑器打开：{path}";
	public static string OpenFailed(string path) => $"打开失败（未配置 $EDITOR，也没有默认程序）：{path}";
	public static string FileMissing(string path) => $"文件不存在：{path}";
	public const string CopySessionPath = "复制会话文件路径";
	public const string CopySessionId = "复制会话 ID";
	public const string ShortcutCopyPath = "便于在终端里继续查看或分享";
	public const string ShortcutCopyId = "用于在别处引用这个会话";
	public static string GitNotRepositoryHint(string cwd) => $"{cwd} 不在 Git 仓库里；点下面的按钮初始化，或在终端运行 git init。";
	public const string RefreshGit = "刷新 Git 状态";

	public const string GitNoSelection = "请先选择要操作的改动。";

	public const string GitCommitMessageRequired = "请先填写提交信息。";

	public const string GitBranchNameInvalid = "分支名不能为空、不能以 - 开头，也不能含空白。";

	public const string GitDone = "已完成。";

	public const string GitStage = "暂存";

	public const string GitUnstage = "取消暂存";

	public const string GitCommit = "提交";

	public const string GitCommitPlaceholder = "提交信息";

	public const string GitNewBranch = "新建分支";

	public const string GitBranch = "分支";

	public const string GitBranchPlaceholder = "新分支名";

	public const string GitStageAll = "全部暂存";

	public const string GitUnstageAll = "全部取消暂存";

	// 外部会话导入（D-09）。
	public const string ImportTitle = "导入外部会话";

	public const string ImportAll = "全部导入";

	public const string ImportOne = "导入";

	public const string ImportAllSources = "全部来源";

	public const string ImportUntitled = "（无标题）";

	public const string ImportScanning = "正在扫描本机的外部会话…";

	public const string ImportHint = "导入只读取源文件，不会修改原工具的会话库。重复导入会再留一份产物，不覆盖上一次的转换结果。";

	public const string ImportEmptyHint = "本机没有找到可导入的外部会话。Claude Code 的会话在 ~/.claude/projects，Codex CLI 的在 ~/.codex/sessions。";

	public static string ImportDone(string label, int messages) => $"已导入 {label} 会话（{messages} 条消息）。";

	public static string ImportFailed(string reason) => $"导入失败：{reason}";

	public static string ImportAllDone(int count) => $"已导入 {count} 条会话。";

	public static string ImportImporting(int count) => $"正在导入 {count} 条会话…";

	public static string ImportAllPartial(int imported, int failed) => $"导入 {imported} 条成功、{failed} 条失败。";

	public static string ImportScanFailed(string reason) => $"扫描失败：{reason}";

	public const string GitStaged = "已暂存";

	public const string GitUnstaged = "未暂存";

	// 远端同步与 stash（D-04）。失败提示按「用户能照着做」来写，而不是转述 git 的英文报错。
	public const string GitPull = "拉取";

	public const string GitPush = "推送";

	public const string GitFetch = "获取远端";

	public const string GitStash = "暂存改动";

	public const string GitStashPop = "应用";

	public const string GitStashResolved = "丢弃";

	public const string GitRemotes = "远端同步";

	public const string GitStashes = "stash";

	public const string GitStashCreated = "工作区改动已收进 stash。";

	public const string GitStashPopped = "已应用最近一条 stash。";

	public const string GitStashNothing = "工作区没有可暂存的改动。";

	public const string GitStashEmpty = "没有 stash 条目。";

	public const string GitNoInitialCommit = "仓库还没有第一个提交，暂存改动需要一个基线提交。先提交一次。";

	public const string GitStashReferenceInvalid = "stash 引用不合法。";

	public const string GitNoRemote = "这个仓库没有配置远端，推送/拉取都用不了。先 git remote add origin <url>。";

	public const string GitNoUpstream = "当前分支没有上游分支。可以先推送一次建立跟踪，或 git branch --set-upstream-to。";

	public const string GitUnknownRemote = "远端名不存在：这个仓库没有对应的 remote，先用 git remote add 配置。";

	public const string GitAmbiguousRemote = "这个仓库有多个远端且没有 origin，无法自动选择推送目标。请先 git branch --set-upstream-to 指定。";

	public const string GitDetachedHead = "处于分离头指针状态，没有可推送的分支。先切回一个分支。";

	public const string GitUpToDate = "已经是最新的。";

	public const string GitPullDone = "已拉取到最新。";

	public const string GitPushDone = "已推送。";

	public const string GitFetchDone = "已获取远端更新。";

	public const string GitNothingToCommit = "没有可提交的改动。";

	public const string GitNothingStaged = "还没有暂存的改动。勾选要提交的文件，或用「全部暂存」。";

	public const string GitPushRejected = "远端有你本地没有的提交，推送被拒绝。先拉取再推送。";

	public const string GitAuthFailed = "认证失败：这台机器上没有可用的凭据。先在终端里 git push 一次完成登录，或配置 credential helper / SSH key。";

	public const string GitNetworkFailed = "连不上远端：检查网络、代理与远端地址。";

	public const string GitLocalChangesBlock = "本地改动会被覆盖，拉取中止。先把改动提交或「暂存改动」后再拉取。";

	public const string GitNotFastForward = "分支已分叉，不能快进合入。这一步需要人工决定合并还是变基，请在终端处理。";

	public const string GitConflict = "有冲突。请在终端解决后提交；stash 应用的冲突会让条目保留下来。";

	public const string GitFailed = "操作失败。";

	public const string GitPushUpstreamCreated = "已推送并建立上游跟踪。";

	// Git 面板（PiDeck 式三段布局）：紧凑分支栏、变更树、提交摘要、折叠更多区。
	public const string GitChanges = "更改";

	public const string GitMore = "更多";

	public const string GitDiff = "差异";

	public const string GitNoPendingChanges = "没有待处理的更改。";

	public const string GitMergeChanges = "合并冲突";

	public const string GitStageDirectory = "暂存此目录";

	public const string GitUnstageDirectory = "取消暂存此目录";

	public const string GitCloseDiff = "关闭差异";

	public const string GitBranchMenu = "切换分支";

	public const string GitNewBranchMenu = "新建分支…";

	public static string GitCommitPlaceholderTo(string branch) => $"消息（Ctrl+Enter 提交到“{branch}”）";

	public const string GitInit = "初始化 Git 仓库";

	public const string GitInitDone = "已初始化 Git 仓库。";

	public const string GitSummary = "提交摘要";

	public const string GitGenerateCommitSummary = "AI 生成提交摘要";

	public const string GitGeneratingCommitSummary = "正在生成提交摘要…";

	public const string GitStageBeforeGenerate = "先暂存要提交的更改，再生成提交摘要。";

	public const string GitSummaryEmpty = "模型没有返回提交摘要。";

	public const string GitSummaryBusy = "会话正在运行，完成后再生成提交摘要。";

	public const string GitLastFailure = "上次操作失败";

	public const string GitSummaryFailed = "生成提交摘要失败。";

	public const string GitSummaryGenerated = "已生成提交摘要。";

	// 源代码管理图 / 比较更改
	public const string GitGraph = "源代码管理图";

	public const string GitGraphFilterAll = "全部";

	public const string GitGraphFilter = "筛选引用";

	public const string GitGraphEmpty = "没有提交。";

	public const string GitGraphNoFilteredCommits = "当前筛选没有提交。";

	public const string GitGraphLoadMore = "加载更多";

	public const string GitCommitDetailUnavailable = "无法读取提交详情。";

	public const string GitNoCommitFiles = "没有文件变更。";

	public const string GitNoCommitMessage = "（无提交信息）";

	public const string GitCompare = "比较更改";

	public const string GitCompareBase = "基准";

	public const string GitCompareTarget = "目标";

	public const string GitCompareAction = "比较";

	public const string GitCompareNeedsBranches = "至少需要两个本地分支才能比较。";

	public const string GitCompareHint = "选择基准与目标分支后点比较。";

	public const string GitCompareEmpty = "两个分支之间没有差异。";

	public const string GitCompareFailed = "比较失败。";

	public static string GitCompareFailedReason(string reason) => $"比较失败：{reason}";

	public static string GitAheadBehind(int ahead, int behind) => $"领先 {ahead} · 落后 {behind}";

	public static string GitCommitFiles(int files, int insertions, int deletions) =>
		$"{files} 个文件 · +{insertions} −{deletions}";

	public const string GitDiffFailed = "读取差异失败。";

	public static string GitDiffFailedReason(string reason) => $"读取差异失败：{reason}";

	public const string GitJustNow = "刚刚";

	public static string GitMinutesAgo(int count) => $"{count} 分钟前";

	public static string GitHoursAgo(int count) => $"{count} 小时前";

	public static string GitDaysAgo(int count) => $"{count} 天前";

	public static string GitMonthsAgo(int count) => $"{count} 个月前";

	public static string GitYearsAgo(int count) => $"{count} 年前";

	public static string GitSummaryFailedReason(string reason) => $"生成提交摘要失败：{reason}";

	public const string GitSummaryModel = "模型";

	public const string GitSummaryThinking = "思考级别";

	public const string GitFollowSession = "跟随当前会话";

	public const string GitSummarySettings = "提交摘要默认配置";

	public const string GitSummarySettingsHint = "Git 面板的 AI 按钮默认使用当前会话模型与思考级别；在这里可以改成固定模型/级别。";

	public const string GitSummarySettingsOpen = "更改默认配置";

	public const string GitSummarySettingsSaved = "已保存提交摘要默认配置。";

	public static string GitSummaryCurrent(string model, string thinking) => $"{model} · 思考 {thinking}";

	public static string GitPushAhead(int count) => $"推送 {count} 个本地提交";

	public static string GitPullBehind(int count) => $"拉取 {count} 个远端提交";

	public const string GitBusy = "正在执行 Git 操作…";

	public const string GitCommitBusy = "正在提交…";

	public const string GitSummaryTimeout = "生成提交摘要超时。";

	public const string GitFetchBusy = "正在获取远端…";

	public const string GitPullBusy = "正在拉取…";

	public const string GitPushBusy = "正在推送…";

	public const string GitSummaryNoModel = "没有可用模型";

	// 安全门设置页（设置 → 运行 → 安全门）。文案对齐 PiDeck 的安全管理面板。
	public const string SettingsSecurity = "安全门";

	public const string SecurityScope = "改动立即写入全局 settings.json 的 security 段，并对后续工具调用生效；项目级设置里的 security 段被忽略（仓库不该能关掉本机防护）。";

	public const string SecurityGroup = "安全门";

	public const string SecurityEnabled = "启用安全门";

	public const string SecurityEnabledTip = "关闭时完全放行";

	public const string SecurityEnabledHint = "总开关。关闭时无论档位怎么设都完全放行。";

	public const string SecurityDefaultLevel = "默认档位";

	public const string SecurityLevels = "档位";

	public const string SecurityLevelDefault = "默认";

	public const string SecurityLevelBuiltin = "内建";

	public const string SecurityLevelCustom = "自定义";

	public const string SecurityLevelDefaultName = "自定义档";

	public const string SecurityLevelNamePlaceholder = "档位名";

	public const string SecurityMakeDefault = "设为默认";

	public const string SecurityDeleteLevel = "删除档位";

	public const string SecurityExpand = "编辑";

	public const string SecurityCollapse = "收起";

	public const string SecurityNewLevel = "新建自定义档位";

	public const string SecurityRename = "应用名称";

	public const string SecurityProtectSensitive = "保护敏感文件（.env、密钥、.git 元数据一律拒绝读写）";

	public const string SecurityPathPolicy = "路径边界";

	public const string SecurityPolicyUnrestricted = "不限制";

	public const string SecurityPolicyWorkspace = "仅限工作目录";

	public const string SecurityPolicyCustom = "工作目录 + 允许目录";

	public const string SecurityCustomAllowDirs = "额外允许的目录";

	public const string SecurityCustomAllowDirsHint = "一行一条，仅在路径边界为「工作目录 + 允许目录」时生效；支持 ~ 与相对会话工作目录的路径。";

	public const string SecurityDenyDirs = "禁地（无条件拒绝）";

	public const string SecurityDenyDirsHint = "一行一条。命中的路径读写都拒绝，且没有绕过出口；空行会被忽略。";

	public const string SecurityDenyBash = "危险命令模式";

	public const string SecurityDenyBashHint = "一行一条正则（忽略大小写），命中即拒绝执行。内建档自带一张「只含不可逆操作」的表，清空它就等于不拦任何命令。";

	public const string SecurityApplyList = "应用列表";

	public const string SecurityToolActions = "工具动作";

	public const string SecurityToolActionsHint = "放行 / 确认 / 拒绝。确认会弹框问用户；没有可用来确认的界面时按拒绝处理。路径边界与危险命令的检查独立于这里，任何动作下都会生效。";

	public const string SecurityDefaultAction = "其它工具（含 MCP 与自定义工具）";

	public const string SecurityActionAllow = "放行";

	public const string SecurityActionAsk = "确认";

	public const string SecurityActionDeny = "拒绝";

	public const string SecuritySessionLevels = "按会话覆盖";

	public const string SecuritySessionHint = "以下会话使用各自的档位（在会话输入框的「安全」按钮上切换），其余会话用默认档。";

	public const string SecurityClearOverride = "清除该会话的覆盖";

	public static string SecurityLoadFailedText(string reason) => $"读取安全配置失败，暂按默认档位显示：{reason}";

	public const string SecurityFollowDefault = "跟随默认档位";

	public const string SecurityTitle = "安全档位";

	public const string SecurityDisabledHint = "安全门总开关已关闭，所有工具调用直接放行。到设置 → 安全门里开启。";

	public static string SecurityLevelOption(string name, bool builtin) => builtin ? name : $"{name}（自定义）";

	public static string SecurityFollowHint(string name, string description) =>
		$"安全档位：跟随默认「{name}」\n{description}\n点击按本会话切换档位";

	public static string SecurityOverrideHint(string name, string description) =>
		$"安全档位：本会话「{name}」\n{description}\n点击按本会话切换档位";

	public static string SecuritySaveFailed(string reason) => $"保存安全配置失败：{reason}";

	/// <summary>远程模型目录拉取。</summary>
	public const string ConfigFetchModels = "拉取远程模型";

	public const string ConfigFetchingModels = "正在拉取远程模型…";

	/// <summary>模型价格表单。</summary>
	public const string ConfigCostInput = "输入价";

	public const string ConfigCostOutput = "输出价";

	public const string ConfigCostCacheRead = "缓存读价";

	public const string ConfigCostCacheWrite = "缓存写价";

	public const string ConfigCostInvalid = "价格必须是非负数字。";

	public const string ConfigFetchNoNew = "拉取到的模型都已存在，没有新增。";

	public static string ConfigFetchDone(int added, int total) => $"拉取到 {total} 个模型，新增 {added} 个（默认参数，可在表单里修改）。";

	public static string ConfigFetchUnsupported(string api) => $"该 API 不支持远程拉取：{api}";

	public static string ConfigFetchFailed(string reason) => $"拉取失败：{reason}";
	public static string GitChangeCount(int count) => $"{count} 个改动";

	/// <summary>diff 标题行的增删统计，与 PiDeck 的「+n −m」一致。</summary>
	public static string GitDiffStat(int added, int removed) => $"+{added} −{removed}";
	public static string FilterMatches(int matched, int total) => $"{matched} / {total} 条匹配";
	public const string CloseTab = "关闭";
	public const string NoOpenSession = "从左侧选择一个会话，或点「新建」开始。";

	// 聊天行
	public const string RoleUser = "你";
	public const string RoleAssistant = "pi";
	public const string RoleAssistantStreaming = "pi（正在回复…）";
	public const string RoleError = "错误";

	/// <summary>用户长消息的折叠开关。</summary>
	public const string MessageExpand = "展开全文";
	public const string MessageCollapse = "收起";

	/// <summary>输入区上方的待办卡。</summary>
	public const string TodoPlanTitle = "待办";

	/// <summary>待办进度：只显示非零分段，段间用宽空格 + 中点分隔（PiDeck 同款）。</summary>
	public static string TodoPlanProgress(int completed, int inProgress, int pending)
	{
		var parts = new List<string>(3);
		if (completed > 0) parts.Add($"{completed} 完成");
		if (inProgress > 0) parts.Add($"{inProgress} 进行中");
		if (pending > 0) parts.Add($"{pending} 待处理");
		return string.Join("\u2002·\u2002", parts);
	}

	/// <summary>输入区上方的扩展 widget 卡。</summary>
	public const string ExtensionWidgetsTitle = "扩展";

	public static string ExtensionWidgetsCount(int count) => $"{count} 项";

	/// <summary>ask_question 对话卡片。</summary>
	public const string AskTitle = "需要你的输入";

	public const string AskSubmit = "提交";

	public const string AskCancel = "取消";

	public const string AskYes = "是（取消勾选则为否）";

	public const string AskSelectPlaceholder = "（请选择）";

	public const string AskCustomPlaceholder = "也可直接输入自定义内容";

	/// <summary>计划模式开关。</summary>
	public const string PlanMode = "计划";

	public const string PlanModeOn = "计划中";

	public const string PlanModeHint = "只读探索并生成计划；开启期间不能修改文件，下一条普通消息自动退出。";

	/// <summary>供应商连接测试。</summary>
	public const string TestConnection = "测试连接";

	public const string TestingConnection = "正在测试连接…";

	public static string ConnectionOk(string provider, string model, long latencyMs) => $"连接正常：{provider}/{model} · {latencyMs} ms";

	public static string ConnectionFailed(string reason) => $"连接失败：{reason}";

	/// <summary>文件浏览器与内置编辑器。</summary>
	public const string FileBrowser = "文件";

	public const string FileBrowserRefresh = "刷新文件列表";

	public const string FileBrowserSave = "保存";

	public const string FileBrowserParent = "上一级";

	public const string FileBrowserEmpty = "这个目录没有可显示的文件。";

	public const string FileBrowserMissing = "文件不存在。";

	public const string FileBrowserTooLarge = "文件过大，无法在编辑器里打开。";

	public const string FileBrowserBinary = "二进制文件不能以文本方式编辑。";

	public const string FileBrowserInvalidPath = "路径不合法。";

	public const string FileBrowserConflict = "文件已被外部修改，请刷新后再保存。";

	public const string FileBrowserNoFile = "请先在左侧选择一个文件。";

	public const string ShortcutFilesPanel = "在右侧面板打开文件浏览器";

	public const string FileBrowserBack = "返回文件列表";

	public static string FileBrowserOpened(string path) => $"已打开 {path}";

	public static string FileBrowserSaved(string path) => $"已保存 {path}";

	/// <summary>会话底部的命令 Dock（MiniPty 伪终端）。</summary>
	public const string Terminal = "终端";

	public const string TerminalRestart = "重启 shell";

	public const string TerminalClear = "清屏";

	public const string TerminalCopy = "复制";

	/// <summary>Markdown 代码块复制按钮的悬浮提示（也用作无图标时的按钮文案来源）。</summary>
	public const string CopyCodeTooltip = "复制代码";

	public const string TerminalPaste = "粘贴";

	public static string TerminalPasteConfirm(int lines) => $"粘贴 {lines} 行内容？（可能直接执行）";

	public const string TerminalSelectAll = "全选";

	public const string TerminalShell = "选择 Shell";

	public const string TerminalShellAuto = "自动（系统默认）";

	public const string TerminalShellCustom = "自定义…";

	public const string TerminalShellPrompt = "Shell 可执行文件路径，例如 /bin/zsh";

	public const string TerminalShellInvalid = "找不到这个 Shell，仍使用当前设置。";

	public const string TerminalHint = "命令 Dock：在项目目录里启动交互式 shell（伪终端）。";

	/// <summary>输入框里的终端开关键与底部面板的收起键。</summary>
	public const string CloseTerminal = "收起终端";

	public const string ShortcutTerminal = "在会话底部开关终端";

	public static string TerminalExited(int? exitCode) => $"[shell 已退出，退出码 {exitCode?.ToString() ?? "?"}]";

	public static string TerminalFailed(string reason) => $"[错误] {reason}";

	/// <summary>工作区检查点。</summary>
	public const string Checkpoints = "检查点";

	public const string CreateCheckpoint = "创建检查点";

	public const string RestoreCheckpoint = "恢复";

	public const string CheckpointMissing = "检查点不存在。";

	public const string CheckpointEmpty = "还没有检查点。";

	public const string CheckpointCreated = "已创建检查点。";

	public const string CheckpointRestored = "已恢复工作区文件（快照之后新增的未跟踪文件会保留）。";

	/// <summary>回答下方的本轮修改汇总。</summary>
	public static string ChangedFilesSummary(IReadOnlyList<string> files)
	{
		var names = files.Select(Path.GetFileName).ToList();
		var shown = string.Join("、", names.Take(4));
		return names.Count <= 4
			? $"本轮修改 {files.Count} 个文件：{shown}"
			: $"本轮修改 {files.Count} 个文件：{shown} 等";
	}

	/// <summary>diff 视图切换。</summary>
	public const string DiffUnified = "统一";

	public const string DiffSideBySide = "并排";

	public static string RoleTool(string? toolName) => $"工具 · {toolName}";

	// 工具卡片展开后的分区标题与状态文案。
	public const string ToolCommandLabel = "命令";
	public const string ToolResultLabel = "结果";
	public const string ToolDiffLabel = "差异";
	public const string ToolRunning = "运行中";
	public const string ToolCompleted = "已完成";
	public const string ToolFailed = "失败";

	public static string ToolDetail(string text) => Summarize(text);

	/// <summary>把毫秒格式化成便于扫读的时长。</summary>
	public static string FormatDuration(double milliseconds) => milliseconds switch
	{
		< 1000 => $"{milliseconds:0} ms",
		< 60_000 => $"{milliseconds / 1000:0.#} s",
		< 3_600_000 => $"{(int)(milliseconds / 60_000)}m{(int)(milliseconds % 60_000 / 1000):00}s",
		_ => $"{(int)(milliseconds / 3_600_000)}h{(int)(milliseconds % 3_600_000 / 60_000):00}m",
	};

	/// <summary>把多行文本压成一行摘要，过长时截断。</summary>
	private static string Summarize(string text)
	{
		Span<char> preview = stackalloc char[81];
		var length = 0;
		var whitespace = false;
		foreach (var character in text.AsSpan())
		{
			if (char.IsWhiteSpace(character))
			{
				whitespace = length > 0;
				continue;
			}

			if (whitespace && length < preview.Length)
			{
				preview[length++] = ' ';
			}

			whitespace = false;
			if (length >= 80)
			{
				return string.Concat(preview[..Math.Min(length, 80)], "…");
			}

			preview[length++] = character;
		}

		return new string(preview[..length]);
	}

	// 状态栏
	public const string NoSession = "尚未打开会话";
	public const string Thinking = "思考";
	public static string ThinkingLevelName(string level) => level switch
	{
		"off" => "关闭",
		"minimal" => "最少",
		"low" => "低",
		"medium" => "中",
		"high" => "高",
		"xhigh" => "极高",
		"max" => "最高",
		_ => level,
	};
	public static string ThinkingShort(string level) => $"思考 {ThinkingLevelName(level)}";
	public const string ThinkingLevelTitle = "思考强度";
	public const string Context = "上下文";
	public const string Tokens = "令牌";
	public const string CacheHit = "缓存命中";
	public const string Cost = "费用";
	public const string Compacting = "正在压缩…";
	public const string Streaming = "正在生成…";
	public const string ThinkingActive = "正在思考…";
	public const string ThinkingComplete = "思考过程";
	public static string ThinkingDuration(double milliseconds) => $"思考了 {FormatDuration(milliseconds)}";
	public static string ResponseDuration(double milliseconds) => $"回复耗时 {FormatDuration(milliseconds)}";

	// 错误与提示
	public static string StartupFailed(string reason) => $"启动失败：{reason}";
	public static string OpenSessionFailed(string reason) => $"无法打开会话：{reason}";

	// 打开长会话时的加载提示：解析整份 JSONL 要上百毫秒到数秒，必须让 UI 有话可说。
	// 提示与错误都显示在会话面板中央的加载卡片上，不进横幅。
	public static string OpeningSession(string name) => $"正在打开会话「{name}」…";
	public static string OpeningSessionProgress(string name, int percent, int lines) =>
		$"正在读取「{name}」{percent}%（{lines} 行）";
	public static string OpeningSessionLines(int lines) => $"正在读取 {lines} 行…";
	public const string PreparingSession = "正在准备会话…";
	public const string RenamingSession = "正在保存会话名称…";
	public const string OpenSessionFailedTitle = "打开会话失败";
	public const string RenameSessionFailedTitle = "保存会话名称失败";
	public const string AnotherInstance = "该目录已经打开了一个窗口。";
	public static string NoSuchDirectory(string path) => $"目录不存在：{path}";
	public static string WindowStartFailed(string reason) => $"无法打开窗口：{reason}";

	// 自检
	public const string SelfCheckTitle = "PiWeft Desktop 自检";
	public const string SelfCheckCwd = "工作目录";
	public const string SelfCheckAgentDir = "agent 目录";
	public const string SelfCheckModel = "模型";
	public const string SelfCheckTools = "工具";
	public const string SelfCheckModels = "可用模型";
	public const string SelfCheckSessions = "本目录会话";
	public const string SelfCheckOk = "结果：正常";
	public static string SelfCheckFailed(string reason) => $"环境装配失败：{reason}";
}
