namespace PiWeft.Desktop;

/// 输入区「+」菜单与技能/提示词选择器的文案。措辞对齐 PiDeck：菜单项只写动作，
/// 模式分组单独一行标签；选择器标题与空态说清楚数据从哪来。
public static partial class Strings
{
	public const string ComposerAddTitle = "添加附件、技能、提示词或模式";

	public const string ComposerAddAttach = "上传附件";

	public const string ComposerAddSkill = "选择技能";

	public const string ComposerAddPrompt = "选择提示词";

	public const string ComposerAddMode = "模式";

	/// 选择器脚注：选中条目只插入命令，会不会发送由用户按回车决定。
	public const string ComposerInsertHint = "点击将命令插入输入框，回车即可发送执行";

	public const string ModeGoal = "目标";

	public const string ModeGoalHint = "围绕一条目标自动连续推进，直到完成、阻塞或暂停。";

	public const string ModePlan = "计划";

	public const string ModePlanHint = "只读分析并生成计划，确认后再执行；开启期间停用 edit / write。";

	public const string ModeGoalExit = "暂停目标并退出目标模式";

	public const string ModePlanExit = "退出计划模式";

	public const string SkillPickerTitle = "选择技能";

	public const string SkillPickerPlaceholder = "搜索技能名称或描述…";

	public const string SkillPickerEmpty = "暂无可用技能。可在 ~/.pi/agent/skills 或项目资源里添加。";

	public const string SkillPickerNoMatch = "未匹配到技能";

	public const string PromptPickerTitle = "提示词模板";

	public const string PromptPickerPlaceholder = "搜索模板名称或描述…";

	public const string PromptPickerEmpty = "暂无提示词模板。可在 ~/.pi/agent/prompts 或项目资源里添加。";

	public const string PromptPickerNoMatch = "未匹配到模板";

	/// 带图片但不带文字的发送：目标模式仍然需要一个 objective，和 PiDeck 同一条兜底指令。
	public const string GoalContinueInstruction = "请继续当前目标。";
}
