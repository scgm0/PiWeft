namespace PiWeft.Desktop;

/// 快捷消息浮层与设置分区的文案。措辞对齐 PiDeck：条目标题只说用途，
/// 两个动作的分工用一行提示讲清，不逐行加 tooltip。
public static partial class Strings
{
	public const string QuickMessagesTitle = "快捷消息";

	/// 浮层搜索框上方那行：插入与直发的分工只讲一次。
	public const string QuickMessagesHint = "点条目插入输入框，条目右侧按钮直接发送（回车插入首条）";

	public const string QuickMessagesSearch = "搜索快捷消息…";

	public const string QuickMessagesNoMatch = "没有匹配的条目";

	public const string QuickMessagesEmpty = "还没有快捷消息，点右上角齿轮到设置里添加。";

	public const string QuickMessagesSend = "直接发送";

	/// 直发置灰的原因：运行中按发送键是中断，草稿里的附件也不该被这条清单消息带走。
	public const string QuickMessagesSendUnavailable = "正在运行或还有未发送的附件，暂时不能直接发送";

	public const string QuickMessagesManage = "管理快捷消息";

	/// 底栏入口的 tooltip 顺带展示当前键位，快捷键才能被发现。
	public static string QuickMessagesTooltip(string keys) => $"{QuickMessagesTitle} ({keys})";

	public static string QuickMessagesCount(int count) => $"共 {count} 条";

	public const string SettingsQuickMessages = "快捷消息";

	public const string SettingsQuickMessagesDesc = "底栏「快捷消息」的条目；点条目插入输入框，行尾按钮直接发送。";

	public const string SettingsQuickMessagesEditor = "快捷消息条目";

	public static string SettingsQuickMessagesFormat(int maxItems, int maxLength) =>
		$"一行一条，最多 {maxItems} 条，单条最长 {maxLength} 字；空行与重复条目会被丢弃。";

	public static string SettingsQuickMessagesCount(int count, int maxItems) => $"共 {count} 条 / 上限 {maxItems}";

	public const string SettingsQuickMessagesReset = "恢复默认";

	public const string SettingsQuickMessagesOpenFile = "打开配置文件";

	public const string SettingsQuickMessagesSaved = "快捷消息已保存";

	public static string SettingsQuickMessagesFileHint(string path) => $"配置文件：{path}";
}
