using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

/// Markdown 视图的统一装配。助手回复与注入回报共用一套：Options/Theme 进高度缓存键，
/// 图片、链接、PRIMARY 选择、代码块复制这些接线漏一处，行看上去正常但交互是死的。
internal static class MarkdownViews
{
	public static MarkdownView Create(
		double fontSize,
		TextFocusScrollGuard? focus = null,
		Action<ImageSource>? preview = null,
		Action<string>? openLink = null,
		bool muted = false)
	{
		// 会话本身已经有纵向视口，每条消息只负责排版；块回收由外层视口驱动。
		var view = new MarkdownView
		{
			FontSize = fontSize,
			FontWeight = FontWeight.Normal,
			Options = AssistantMessageView.SharedMarkdownOptions,
			MarkdownTheme = AssistantMessageView.SharedMarkdownTheme,
			ImageResolver = MarkdownImageLoader.Shared,
		};

		view.ImageRequested += args =>
		{
			if (args.Image is { } source) preview?.Invoke(source);
		};
		// 丢渲染一旦发生就要在 timings 里留下现场（窗口/偏移/高度），否则只能靠猜：
		// 正常情况下这条分支永远不走，成本为零。
		view.NothingRealized += geometry =>
		{
			Diagnostics.Count("markdown.empty.realize");
			Diagnostics.Record("markdown.empty.realize", 0, geometry);
		};
		view.SelectionChanged += () => PrimarySelectionText.Service?.Publish(
			view.SelectedText.Length <= LinuxPrimarySelection.MaxCharacters ? view.SelectedText : string.Empty);
		if (focus is not null)
		{
			focus.Track(view);
			// 链接段落是自己的焦点目标：不加守卫时点一下会让组体与会话把该段滚进视野。
			view.FocusableElementCreated = paragraph => focus.Track(paragraph);
		}

		if (openLink is not null)
		{
			view.LinkRequested += link => openLink(link.Url);
		}

		if (focus is not null)
		{
			view.CodeTextCreated = (text, language) =>
				AssistantMessageView.ConfigureCodeText(text, language, focus, muted);
		}

		if (muted)
		{
			view.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextMuted);
		}

		return view.SmoothScrolling();
	}
}
