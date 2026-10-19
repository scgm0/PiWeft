using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>工具行只构造摘要；首次展开才解析补丁、创建可选择输出，并复用展开内容。</summary>
internal sealed class ToolMessageView
{
	public ToolMessageView(ChatEntry entry, TextFocusScrollGuard focus, Action<ImageSource>? preview = null)
	{
		var name = new TextBlock().Text(entry.ToolName ?? "工具")
			.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium).PrimaryText();
		var state = new TextBlock()
			.Text(entry.IsStreaming ? Strings.ToolRunning : entry.IsError ? Strings.ToolFailed : Strings.ToolCompleted)
			.FontSize(AppTheme.FontSizeMicro)
			.WithTheme((theme, text) => text.Foreground = entry.IsStreaming
				? AppTheme.For(theme).Info
				: entry.IsError ? AppTheme.For(theme).Error : AppTheme.For(theme).Success);
		var timing = new TextBlock()
			.Text(entry.DurationMs is { } ms ? Strings.FormatDuration(ms) : string.Empty)
			.FontSize(AppTheme.FontSizeMicro).SubtleText();
		var detailSource = DetailSource(entry);
		var detail = new TextBlock().Text(Strings.ToolDetail(detailSource))
			.FontSize(AppTheme.FontSizeCaption).MutedText().TextTrimming(TextTrimming.CharacterEllipsis);
		var header = new Grid().Columns("Auto,Auto,Auto,*")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				name.Column(0),
				state.Column(1).Margin(new Thickness(6, 0, 0, 0)),
				timing.Column(2).Margin(new Thickness(6, 0, 0, 0)),
				detail.Column(3).Margin(new Thickness(6, 0, 0, 0)));
		if (timing.Text.Length == 0) timing.IsVisible(false);
		if (detail.Text.Length == 0) detail.IsVisible(false);

		var expander = new Expander().Header(header).IsExpanded(false).HoverTip(Strings.ToolExpandHint);
		focus.TrackFold(expander);
		expander.ExpandedChanged += expanded =>
		{
			if (!expanded || expander.Content is not null) return;
			var body = new StackPanel().Spacing(AppTheme.Space2);
			if (entry.ToolArguments is { Length: > 0 } && detailSource != entry.Text)
			{
				body.Children(SectionLabel(Strings.ToolCommandLabel));
				var input = MessageText.Create(detailSource, muted: true, mono: true, fontSize: AppTheme.FontSizeCaption);
				input.Foreground = AppTheme.CodeText;
				input.Tag = null;
				focus.Track(input);
				body.Children(input);
			}
			if (entry.Patch is { Length: > 0 } patch)
			{
				body.Children(SectionLabel(Strings.ToolDiffLabel));
				var host = new Border();
				var unified = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffUnified)).OnClick(() => host.Child(DiffView.Render(patch, focus)));
				var sideBySide = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffSideBySide)).OnClick(() => host.Child(DiffView.RenderSideBySide(patch, focus)));
				host.Child(DiffView.Render(patch, focus));
				body.Children(new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(unified, sideBySide), host);
			}
			if (entry.Text.Length > 0)
			{
				body.Children(SectionLabel(Strings.ToolResultLabel));
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

		View = new Border().CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(0, 2, 0, 2))
			.MaxWidth(AppTheme.MessageMaxWidth).Child(expander);
		if (entry.IsError)
		{
			View.BorderThickness(1).Padding(new Thickness(6, 3, 6, 3)).WithTheme((theme, border) =>
			{
				var error = AppTheme.For(theme).Error;
				border.Background = error.WithAlpha(0x10);
				border.BorderBrush = error.WithAlpha(0x33);
			});
		}
	}

	public Border View { get; }

	private static string DetailSource(ChatEntry entry)
	{
		if (entry.ToolArguments is not { Length: > 0 } arguments) return entry.Text;
		if (entry.ToolName is not ("bash" or "terminal")) return arguments;
		try
		{
			if (JsonNode.Parse(arguments) is JsonObject root && root["command"]?.GetValue<string>() is { Length: > 0 } command)
				return command;
		}
		catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
		{
		}
		return arguments;
	}

	private static TextBlock SectionLabel(string text) => new TextBlock()
		.Text(text)
		.FontSize(AppTheme.FontSizeMicro)
		.FontWeight(FontWeight.Medium)
		.SubtleText();

	private static TextBlock CodeLabel(string text) => new TextBlock()
		.Text(text)
		.FontSize(AppTheme.FontSizeMicro)
		.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).CodeText);
}
