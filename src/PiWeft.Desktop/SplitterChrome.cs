using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 分隔条的视觉：去掉 MewUI 默认的 30 DIP 居中短线，静止时只留悬停与拖动反馈。
/// 那条短线在 PiWeft 里像一截断掉的结构线（设计约定区域之间是贯通的 1px 发丝线，
/// 或干脆靠色阶分层），却出现在每个左右/上下分栏的中间。区域分隔由两侧表面的色差表达，
/// 分隔条本身只在指针靠近时给一块浅底、按下时给强调弱底。
/// 颜色写成控件本地值：优先级高于 SplitterThumb 默认样式的触发器，所以不会把短线画回来。
internal static class SplitterChrome
{
	public static SplitPanel FlatSplitter(this SplitPanel split)
	{
		foreach (var child in split.Children)
		{
			if (child is not Control thumb ||
				thumb.Cursor is not (CursorType.SizeWE or CursorType.SizeNS))
			{
				continue;
			}

			thumb.Background = AppTheme.Transparent;
			thumb.BorderBrush = AppTheme.Transparent;
			thumb.MouseEnter += () => thumb.Background = AppTheme.HoverBackground;
			thumb.MouseLeave += () => thumb.Background = AppTheme.Transparent;
			thumb.MouseDown += _ => thumb.Background = AppTheme.AccentSoft;
			thumb.MouseUp += _ => thumb.Background = thumb.IsMouseOver
				? AppTheme.HoverBackground
				: AppTheme.Transparent;
		}

		return split;
	}
}
