using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口级浮层遮罩的压暗档位。档位从浅到深，按浮层的模态重量选。
internal enum ScrimStrength
{
	/// 不压暗，只吃指针（会话内搜索这类轻量浮层）。
	None,

	/// 最轻：Provider 用量/凭据/登录面板。
	Light,

	/// 常规：命令面板、模型与提示词选择器、设置、会话导入之外的浮层。
	Regular,

	/// 偏重：文件 diff 预览。
	Strong,

	/// 最重：图片预览、会话导入、问询浮层这类整屏内容。
	Heavy,
}

/// 浮层背后的压暗层：铺满浮层容器、吞掉指针、点空白关闭。
/// 遮罩仍由各浮层自带，不交给 TransientOverlay 托管：它要和卡片一起淡入淡出，
/// 也要跟着各自根元素的范围走；统一的是外观与指针行为。
internal static class OverlayScrim
{
	internal static Button Create(Action close, ScrimStrength strength = ScrimStrength.Regular)
	{
		var scrim = new Button()
			.Background(AppTheme.Scrim(strength))
			.BorderThickness(0)
			.CornerRadius(0)
			.Cursor(CursorType.Arrow)
			.Focusable(false)
			.OnClick(close);
		// 遮罩挡住滚轮：指针落在空白处时，后面的正文不该跟着滚。
		scrim.MouseWheel += args => args.Handled = true;
		return scrim;
	}
}
