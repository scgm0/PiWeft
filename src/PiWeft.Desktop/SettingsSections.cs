using System.Collections.Frozen;

namespace PiWeft.Desktop;

/// <summary>设置页的一个分区：导航轨、分区标题与搜索索引共用同一份元数据。</summary>
/// <param name="Id">分区标识，也是「配置管理」页签映射到配置文件时的键。</param>
/// <param name="Config">是否属于「配置管理」分段。</param>
/// <param name="Title">导航与标题栏显示的名称。</param>
/// <param name="Icon">Lucide 图标名。</param>
/// <param name="Keywords">搜索别名：英文名、俗称、相邻概念。</param>
internal sealed record SettingsSection(
	string Id,
	bool Config,
	string Title,
	string Icon,
	IReadOnlyList<string> Keywords);

/// <summary>设置页分区表。导航、分区标题和搜索都从这里取名称，三处不会漂移。</summary>
internal static class SettingsSections
{
	/// <summary>系统设置的分组与顺序。</summary>
	public static IReadOnlyList<(string Label, IReadOnlyList<SettingsSection> Sections)> Groups { get; } =
	[
		(Strings.SettingsGroupGeneral, [
			new("overview", false, Strings.ConfigOverview, "settings",
				["概览", "环境", "运行", "agent", "目录", "工作目录"]),
			new("appearance", false, Strings.ConfigAppearance, "contrast",
				["外观", "主题", "深色", "浅色", "字体", "字号", "阅读", "行宽", "托盘", "关闭", "后台"]),
			new("git", false, Strings.SettingsGit, "git-branch",
				["git", "提交", "摘要", "commit", "提示词"]),
			new("quickMessages", false, Strings.SettingsQuickMessages, "message-square",
				["快捷消息", "快捷", "常用语", "短语"]),
		]),
		(Strings.SettingsGroupRuntime, [
			new("extensions", false, Strings.SettingsExtensions, "package",
				["扩展", "插件", "内置", "extension"]),
			new("resources", false, Strings.SettingsResources, "book-open",
				["资源", "技能", "skill", "提示词", "主题", "包", "package"]),
			new("session", false, Strings.SettingsSession, "message-square",
				["当前会话", "会话", "session", "id", "模型"]),
			new("sounds", false, Strings.SettingsSounds, "bell",
				["提示音", "声音", "通知", "sound"]),
			new("security", false, Strings.SettingsSecurity, "shield",
				["安全门", "安全", "权限", "审批", "security"]),
			new("shortcuts", false, Strings.SettingsShortcuts, "terminal",
				["快捷键", "按键", "热键", "shortcut", "keybind"]),
		]),
		(Strings.SettingsGroupData, [
			new("usage", false, Strings.SettingsUsageStats, "chart-column",
				["使用统计", "用量", "token", "花费", "成本", "usage"]),
		]),
		(Strings.MaintenanceGroup, [
			new("archive", false, Strings.SettingsArchive, "trash",
				["归档", "删除", "恢复", "archive", "trash"]),
			new("catalog", false, Strings.SettingsModelCatalog, "package",
				["模型目录", "目录", "更新", "catalog", "models"]),
			new("logs", false, Strings.SettingsLogs, "folder-open",
				["日志", "log", "排障", "诊断包"]),
			new("diagnostics", false, Strings.SettingsDiagnostics, "list-checks",
				["性能诊断", "诊断", "性能", "耗时", "帧", "trace"]),
			new("about", false, Strings.SettingsAbout, "settings",
				["关于", "版本", "字体", "运行时", "about", "version"]),
		]),
	];

	/// <summary>配置管理的分区与顺序。</summary>
	public static IReadOnlyList<SettingsSection> Config { get; } =
	[
		new("models", true, Strings.ConfigModels, "settings",
			["模型", "供应商", "provider", "model", "API", "导入", "导出"]),
		new("auth", true, Strings.ConfigAuth, "key-round",
			["认证", "凭据", "API Key", "OAuth", "登录", "auth"]),
		new("agent", true, Strings.ConfigSettings, "wrench",
			["Agent 设置", "工具", "codemode", "子代理", "目标", "轮次", "token", "预算", "重试", "压缩", "agent", "settings", "goal"]),
		new("mcp", true, Strings.SettingsMcp, "terminal",
			["MCP", "服务器", "工具", "mcp"]),
		new("source", true, Strings.ConfigSource, "square-pen",
			["源文件", "JSON", "配置文件", "编辑", "source"]),
	];

	/// <summary>系统设置与配置管理的全部分区：先系统、后配置，搜索按这个顺序排同分结果。</summary>
	public static IReadOnlyList<SettingsSection> All { get; } =
		[.. Groups.SelectMany(static group => group.Sections), .. Config];

	private static readonly FrozenDictionary<string, SettingsSection> ById =
		All.ToFrozenDictionary(static section => section.Id, StringComparer.Ordinal);

	private static readonly FrozenDictionary<string, int> OrderById =
		All.Select(static (section, index) => (section.Id, Index: index))
			.ToFrozenDictionary(static pair => pair.Id, static pair => pair.Index, StringComparer.Ordinal);

	/// <summary>按标识查找分区；未知标识返回 <c>null</c>。</summary>
	public static SettingsSection? Find(string id) => ById.GetValueOrDefault(id);

	/// <summary>分区在导航里的顺序；未知标识排到最后。</summary>
	public static int OrderOf(string id) => OrderById.GetValueOrDefault(id, int.MaxValue);

	/// <summary>分区标题；未知标识回退到「概览」，与旧导航的默认一致。</summary>
	public static string Title(string id) => Find(id)?.Title ?? Strings.ConfigOverview;

	/// <summary>配置文件对应的分区：源文件编辑任意配置时都归到「源文件」。</summary>
	public static string SectionOf(ConfigFile file) => file switch
	{
		ConfigFile.Auth => "auth",
		ConfigFile.Settings => "agent",
		ConfigFile.Mcp => "mcp",
		ConfigFile.Usage => "source",
		_ => "models",
	};

	/// <summary>分区对应的配置文件；「源文件」默认打开模型文件。</summary>
	public static ConfigFile FileOf(string section) => section switch
	{
		"auth" => ConfigFile.Auth,
		"agent" => ConfigFile.Settings,
		"mcp" => ConfigFile.Mcp,
		_ => ConfigFile.Models,
	};
}
