using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>工具行只构造摘要；首次展开才解析补丁、创建可选择输出，并复用展开内容。</summary>
internal sealed class ToolMessageView
{
	public ToolMessageView(ChatEntry entry, TextFocusScrollGuard focus, Action<ImageSource>? preview = null)
	{
		// 紧凑工具行：Expander 的单个箭头 + 工具名 + 状态 + 耗时 + 等宽行动摘要。
		// 过程组里不再套状态徽章 pill：一排 pill 会把行高拉高、也会把单行预览挤短。
		var name = new TextBlock().Text(entry.ToolName ?? "工具")
			.FontSize(AppTheme.FontSizeCaption).SubtleText();
		var stateText = new TextBlock()
			.Text(entry.IsStreaming ? Strings.ToolRunning : entry.IsStopped ? Strings.ToolStopped : entry.IsError ? Strings.ToolFailed : Strings.ToolCompleted)
			.FontSize(AppTheme.FontSizeMicro).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, text) =>
			{
				var palette = AppTheme.For(theme);
				text.Foreground = entry.IsStreaming ? palette.Info : entry.IsError ? palette.Error : palette.TextMuted;
			});
		var timing = new TextBlock()
			.Text(entry.DurationMs is { } ms ? Strings.FormatDuration(ms) : string.Empty)
			.FontSize(AppTheme.FontSizeMicro).SubtleText();
		var detailSource = DetailSource(entry);
		var detail = new TextBlock().Text(ExecutionProcess.ToolAction(entry, entry.IsStreaming))
			.FontSize(AppTheme.FontSizeMicro).MutedText().FontFamily(AppTheme.MonoFont)
			.TextTrimming(TextTrimming.CharacterEllipsis);
		var header = new Grid().Columns("Auto,Auto,Auto,*")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				name.Column(0).VerticalAlignment(VerticalAlignment.Center),
				stateText.Column(1).Margin(new Thickness(6, 0, 0, 0)),
				timing.Column(2).Margin(new Thickness(6, 0, 0, 0)).VerticalAlignment(VerticalAlignment.Center),
				detail.Column(3).Margin(new Thickness(6, 0, 0, 0)).VerticalAlignment(VerticalAlignment.Center));
		if (timing.Text.Length == 0) timing.IsVisible(false);
		if (detail.Text.Length == 0) detail.IsVisible(false);

		var expander = new Expander().Header(header).IsExpanded(false).HoverTip(Strings.ToolExpandHint);
		// 组体内的步骤由组体自己的预览行锚点负责；再让外层会话追随会改变会话滚动进度。
		focus.TrackFold(expander, preserveAnchor: false);
		expander.ExpandedChanged += expanded =>
		{
			if (!expanded || expander.Content is not null) return;
			var body = new StackPanel().Spacing(AppTheme.Space2);
			if (entry.ToolArguments is { Length: > 0 } && detailSource != entry.Text)
			{
				body.Children(SectionLabel(Strings.ToolCommandLabel));
				var input = MessageText.Create(detailSource, muted: true, mono: true, fontSize: AppTheme.FontSizeCaption);
				// 代码表面随主题在深浅之间切换，文字颜色必须一起跟着换，不能停在创建时的快照上。
				input.WithTheme((theme, text) =>
				{
					text.Foreground = AppTheme.For(theme).CodeText;
					AppStyles.RefreshTextView(text);
				});
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
			text.WithTheme((theme, block) =>
			{
				block.Foreground = AppTheme.For(theme).CodeText;
				AppStyles.RefreshTextView(block);
			});
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
