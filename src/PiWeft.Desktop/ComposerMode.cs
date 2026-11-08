namespace PiWeft.Desktop;

/// 输入区的工作模式。普通模式不显示任何标记；目标与计划互斥，
/// 入口在「+」菜单的模式分组，退出点输入区左侧的模式 chip。
internal enum ComposerMode
{
	Normal,
	Goal,
	Plan,
}
