using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>工具行只构造摘要；首次展开才解析补丁、创建可选择输出，并复用展开内容。</summary>
internal sealed class ToolMessageView
{
	public ToolMessageView(ChatEntry entry, TextFocusScrollGuard focus, Action<ImageSource>? preview = null)
	{
		var expander = new Expander()
			.Header(new TextBlock().Text(Strings.ToolSummary(entry.ToolName ?? string.Empty, entry.Text, entry.IsError, entry.DurationMs))
				.FontSize(AppTheme.FontSizeCaption).MutedText().TextTrimming(TextTrimming.CharacterEllipsis))
			.IsExpanded(false).HoverTip(Strings.ToolExpandHint);
		focus.TrackFold(expander);
		expander.ExpandedChanged += expanded =>
		{
			if (!expanded || expander.Content is not null) return;
			var body = new StackPanel().Spacing(AppTheme.Space2);
			if (entry.ToolArguments is { Length: > 0 } arguments && arguments != entry.Text)
			{
				var input = MessageText.Create(arguments, muted: true, mono: true, fontSize: AppTheme.FontSizeCaption);
				input.Foreground = AppTheme.CodeText;
				input.Tag = null;
				focus.Track(input);
				body.Children(input);
			}
			if (entry.Patch is { Length: > 0 } patch)
			{
				var host = new Border();
				var unified = new Button().Ghost().Content(Label(Strings.DiffUnified)).OnClick(() => host.Child(DiffView.Render(patch, focus)));
				var sideBySide = new Button().Ghost().Content(Label(Strings.DiffSideBySide)).OnClick(() => host.Child(DiffView.RenderSideBySide(patch, focus)));
				host.Child(DiffView.Render(patch, focus));
				body.Children(new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(unified, sideBySide), host);
			}
			if (entry.Text.Length > 0)
			{
				var text = MessageText.Create(entry.Text, muted: true, mono: true, fontSize: AppTheme.FontSizeCaption);
				text.Foreground = AppTheme.CodeText;
				text.Tag = null;
				focus.Track(text);
				body.Children(text);
			}
			foreach (var image in entry.Images) body.Children(SessionTab.ImageView(image, preview));
			expander.Content(new Border().StyleName(AppStyles.Code).CornerRadius(AppTheme.RadiusSm)
				.Padding(new Thickness(AppTheme.Space2)).Margin(new Thickness(0, AppTheme.Space2, 0, 0)).Child(body));
		};
		View = new Border().StyleName(entry.IsError ? AppStyles.ToolError : AppStyles.ToolMessage)
			.BorderThickness(entry.IsError ? 1 : 0)
			.Padding(new Thickness(4, 2, 4, 2))
			.CornerRadius(AppTheme.RadiusSm).MaxWidth(AppTheme.MessageMaxWidth).Child(expander);
	}

	public Border View { get; }

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro).PrimaryText();
}
