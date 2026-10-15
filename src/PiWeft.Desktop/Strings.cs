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
	public const string SearchPlaceholder = "搜索会话…";
	public const string CurrentDirOnly = "仅当前目录";
	public static string EnvironmentFallback(string cwd, string reason) => $"目录 {cwd} 的环境不可用（{reason}），已回退到启动环境。";
	public const string NoMatches = "没有匹配的会话。";
	public const string ContentHit = "内容命中";
	public static string ContentMatches(int count, string excerpt) =>
		$"{Strings.ContentHit} {count} 个会话，例如：{excerpt}";

	// 输入区
	public const string ComposerPlaceholder = "输入指令，Ctrl+Enter 发送";
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
	public const string NothingToCopy = "还没有可复制的回复。";
	public const string ClipboardUnavailable = "剪贴板不可用。";
	public const string BrokenImage = "（图片数据无法解码）";
	public const string PaletteButton = "命令";
	public const string PinnedMark = "★";
	public const string RemoveAttachment = "移除";
	public static string AttachFailed(string reason) => $"附加图片失败：{reason}";
	public static string SendFailed(string reason) => $"发送失败：{reason}";

	// 顶栏与欢迎页
	public const string AppName = "PiWeft Desktop";
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
	public const string ShortcutSettings = "环境、技能与 MCP 状态";
	public const string ShortcutGit = "当前分支与工作区改动";
	public const string ShortcutTheme = "立即在深色与浅色之间切换";
	public const string ShortcutRefresh = "重新读取磁盘上的会话";
	public const string ShortcutCloseTab = "Ctrl+W";
	public const string CloseOtherTabs = "关闭其它标签";
	public const string CloseAllTabs = "关闭全部标签";
	public const string ShortcutCloseOtherTabs = "只留下当前会话";
	public const string ShortcutCloseAllTabs = "关闭所有会话标签（设置面板保留）";
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
	public const string ScrollToLatest = "回到最新";
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
	public static string GitNotRepositoryHint(string cwd) => $"{cwd} 不在 Git 仓库里；在终端运行 git init 后这里会列出改动。";
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

	public const string GitStaged = "已暂存";

	public const string GitUnstaged = "未暂存";

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
	public static string FilterMatches(int matched, int total) => $"{matched} / {total} 条匹配";
	public const string CloseTab = "关闭";
	public const string NoOpenSession = "从左侧选择一个会话，或点「新建」开始。";

	// 聊天行
	public const string RoleUser = "你";
	public const string RoleAssistant = "pi";
	public const string RoleAssistantStreaming = "pi（正在回复…）";
	public const string RoleError = "错误";

	/// <summary>输入区上方的待办条。</summary>
	public const string TodoPlanTitle = "待办";

	public static string TodoPlanProgress(int completed, int total) => $"{TodoPlanTitle} {completed}/{total}";

	public static string TodoPlanMore(int hidden) => $"… 还有 {hidden} 项";

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

	public const string ShortcutFilesPanel = "打开文件浏览器";

	public static string FileBrowserOpened(string path) => $"已打开 {path}";

	public static string FileBrowserSaved(string path) => $"已保存 {path}";

	/// <summary>命令 Dock（非交互式终端）。</summary>
	public const string Terminal = "终端";

	public const string TerminalRun = "运行";

	public const string TerminalCancel = "取消";

	public const string TerminalPlaceholder = "输入命令，回车运行";

	public const string TerminalHint = "命令 Dock：在项目目录里运行单条命令并显示输出；不提供交互式终端或全屏 TUI。";

	public const string TerminalCancelled = "[已取消]";

	public const string ShortcutTerminal = "打开命令 Dock";

	public static string TerminalExit(int? exitCode) => $"[退出码 {exitCode?.ToString() ?? "?"}]";

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

	/// <summary>工具卡片的折叠标题：状态、名称、耗时与一行摘要。</summary>
	public static string ToolSummary(string toolName, string text, bool isError = false, double? durationMs = null)
	{
		// 状态用中文：内置字体不含 ✓/✗，符号回退会让标题与正文的笔画粗细不一致。
		var mark = isError ? "失败" : "完成";
		var timing = durationMs is { } ms ? $"  {FormatDuration(ms)}" : string.Empty;
		return $"{mark} {toolName}{timing} · {Summarize(text)}";
	}

	/// <summary>把毫秒格式化成便于扫读的时长。</summary>
	public static string FormatDuration(double milliseconds) => milliseconds switch
	{
		< 1000 => $"{milliseconds:0} ms",
		< 60_000 => $"{milliseconds / 1000:0.#} s",
		_ => $"{milliseconds / 60_000:0.#} min",
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
	public const string Context = "上下文";
	public const string Tokens = "令牌";
	public const string CacheHit = "缓存命中";
	public const string Cost = "费用";
	public const string Compacting = "正在压缩…";
	public const string Streaming = "正在生成…";
	public const string ThinkingActive = "正在思考…";
	public const string ThinkingComplete = "思考过程";
	public static string ResponseDuration(double milliseconds) => $"回复耗时 {FormatDuration(milliseconds)}";

	// 错误与提示
	public static string StartupFailed(string reason) => $"启动失败：{reason}";
	public static string OpenSessionFailed(string reason) => $"无法打开会话：{reason}";
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
