namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string ReadingSettings = "阅读与输入";
	public const string ChatFontSize = "正文与输入字号";
	public const string ReadingWidth = "阅读区域宽度";
	public const string ReadingCompact = "紧凑";
	public const string ReadingComfortable = "舒适";
	public const string ReadingWide = "宽屏";
	public const string ReadingSample = "清晰的正文与统计信息，让长时间阅读更轻松。";
	public const string StatisticsOverview = "概览";
	public const string StatisticsDetails = "明细";
	public const string StatisticsContext = "上下文";
	public const string ToggleSidebar = "展开 / 收起项目侧栏";
	public const string SessionList = "项目";
	public const string SettingsHint = "管理外观、快捷键和运行环境；模型与认证在配置管理中。";
	public const string AppearanceHint = "选择适合当前环境的明暗模式，立即生效。";
	public const string NavigationGroup = "工作区";
	public const string ConfigurationGroup = "配置文件";
	public const string MaintenanceGroup = "维护";
	public const string ConfigReady = "修改后点击保存";
	public const string ConfigDefaults = "默认供应商与模型";
	public const string ConfigBehavior = "通用行为";
	public const string ConfigDefaultsHint = "留空时自动选择；项目配置中留空则继承全局设置。";
	public const string ConfigBehaviorHint = "控制新会话的思考级别、自动压缩和失败重试。";
	public const string ConfigMcpEmpty = "还没有 MCP 服务器";
	public const string ConfigMcpHint = "添加 stdio 或 HTTP 服务，为 Agent 扩展工具。保存后重启应用生效。";
	public const string ConfigCredentialsHint = "API Key 保存在本机；已有凭据仅显示供应商和认证类型。";
	public const string ToolExpandHint = "展开查看参数、输出或差异";

	// 侧栏「对话 | 项目」双视图与行级菜单。
	public const string SidebarTabChats = "对话";
	public const string SidebarTabProjects = "项目";
	public const string AddProject = "添加项目";
	public const string SessionMenuRename = "重命名会话";
	public const string SessionMenuArchive = "归档会话";
	public const string ProjectMenuRemove = "移除";
	public const string ProjectMenuReveal = "查看文件";
	public const string ProjectMenuNewTask = "新建任务";
	/// <summary>页脚入口的短标签：完整说明在悬浮提示里，文字不参与撑宽页脚布局。</summary>
	public const string SidebarGit = "Git";
	public const string BackToWorkspace = "返回工作区";
	public const string MoreActions = "更多操作";
	public const string SessionArchived = "会话已归档（可在会话目录的 .trash 里找回）";
	public const string ProjectRemoved = "已从列表移除该项目（会话文件保留在磁盘上）";
}
