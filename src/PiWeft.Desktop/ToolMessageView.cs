using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ToolMessageView
{
	private readonly TextBlock _stateText;
	private readonly TextBlock _detail;
	private readonly StickyExpander _expander;
	private readonly TextFocusScrollGuard _focus;
	private readonly Action<ImageSource>? _preview;
	// 就地更新会换掉这个引用（条目不可变，事件到达时整份换新）；主题回调和行体构建都读它。
	private ChatEntry _entry;

	public ToolMessageView(ChatEntry entry, TextFocusScrollGuard focus, Action<ImageSource>? preview = null)
	{
		_entry = entry;
		_focus = focus;
		_preview = preview;
		// 紧凑工具行：Expander 的单个箭头 + 工具名 + 状态 + 耗时 + 等宽行动摘要。
		// 过程组里不再套状态徽章 pill：一排 pill 会把行高拉高、也会把单行预览挤短。
		var name = new TextBlock().Text(entry.ToolName ?? "工具")
			.FontSize(AppTheme.FontSizeCaption).SubtleText();
		_stateText = new TextBlock()
			.Text(StateText(entry))
			.FontSize(AppTheme.FontSizeMicro).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, text) =>
			{
				var palette = AppTheme.For(theme);
				text.Foreground = _entry.IsStreaming ? palette.Info : _entry.IsError ? palette.Error : palette.TextMuted;
			});
		var timing = Timing(entry);
		_detail = new TextBlock().Text(ExecutionProcess.ToolAction(entry, entry.IsStreaming))
			.FontSize(AppTheme.FontSizeMicro).MutedText().FontFamily(AppTheme.MonoFont)
			.TextTrimming(TextTrimming.CharacterEllipsis);
		var header = new Grid().Columns("Auto,Auto,Auto,*")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				name.Column(0).VerticalAlignment(VerticalAlignment.Center),
				_stateText.Column(1).Margin(new Thickness(6, 0, 0, 0)),
				timing.Column(2).Margin(new Thickness(6, 0, 0, 0)).VerticalAlignment(VerticalAlignment.Center),
				_detail.Column(3).Margin(new Thickness(6, 0, 0, 0)).VerticalAlignment(VerticalAlignment.Center));
		if (timing.Text.Length == 0) timing.IsVisible(false);
		if (_detail.Text.Length == 0) _detail.IsVisible(false);

		// 组体内吸顶用：没有挂组体视口时行为与普通 Expander 完全相同。
		_expander = new StickyExpander().Header(header).IsExpanded(false).HoverTip(Strings.ToolExpandHint);
		// 组体内的步骤由组体自己的预览行锚点负责；再让外层会话追随会改变会话滚动进度。
		focus.TrackFold(_expander, preserveAnchor: false);
		_expander.ExpandedChanged += expanded =>
		{
			if (!expanded || _expander.Content is not null) return;
			// 行体按「展开这一刻」的最新条目构建：流式输出期间 _entry 会被就地更新换掉，
			// 用构造时捕获的那份会少显示展开前的最后几段输出。
			_expander.Content(BuildBody(_entry));
		};

		View = new Border().CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(0, 2, 0, 2))
			.MaxWidth(AppTheme.MessageMaxWidth).Child(_expander);
		// ConversationTimeline 据此跳过 metadata-only 快路径：工具行表头显示参数，而这条快路径
		// 只换 Tag 不碰控件，会让摘要停在旧参数上（就地更新只覆盖「仍在流式输出」的拍，见 Update）。
		View.Tag = this;
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

	/// 流式输出每来一段就整行重建，会重造 Border / Expander / 秒表，还把这行打回虚拟化估算高度。
	/// 这里只接「同一次调用仍在输出」的更新：表头与摘要只依赖参数，多数拍不用改。
	/// 换成固定耗时或错误样式（完成、失败、停止）、表头控件类型变化（ToolStartedAt 出现）、参数换代，
	/// 或行体已展开（是展开那一刻的快照，还挂着 StickyExpander 的视口）时返回 false，交给整行重建。
	internal bool Update(ChatEntry entry)
	{
		if (!CanUpdate(entry)) return false;
		// 表头摘要只依赖工具名与参数，参数缺失时才退到正文。参数仍是同一个字符串引用时不必重算：
		// bash/terminal 的摘要每次都要解析参数 JSON，流式期每 40ms 一次是白烧。
		if (entry.ToolName != _entry.ToolName || entry.ToolArguments is not { Length: > 0 } ||
			!ReferenceEquals(entry.ToolArguments, _entry.ToolArguments))
		{
			var detail = ExecutionProcess.ToolAction(entry, running: true);
			if (!string.Equals(_detail.Text, detail, StringComparison.Ordinal))
			{
				_detail.Text(detail);
				_detail.IsVisible(detail.Length > 0);
			}
		}

		_entry = entry;
		var state = StateText(entry);
		if (!string.Equals(_stateText.Text, state, StringComparison.Ordinal)) _stateText.Text(state);
		return true;
	}

	private bool CanUpdate(ChatEntry next) =>
		// 只在「运行中 → 仍在运行」上就地更新：两边的耗时空控件必须是同一种（LiveDurationText）。
		_entry.IsStreaming && next.IsStreaming && !next.IsError && !next.IsStopped &&
		next.ToolName == _entry.ToolName && next.ToolCallId == _entry.ToolCallId &&
		next.ToolStartedAt == _entry.ToolStartedAt && next.DurationMs is null &&
		_expander.Content is null;

	private static string StateText(ChatEntry entry) =>
		entry.IsStreaming ? Strings.ToolRunning : entry.IsStopped ? Strings.ToolStopped : entry.IsError ? Strings.ToolFailed : Strings.ToolCompleted;

	private Border BuildBody(ChatEntry entry)
	{
		var detailSource = DetailSource(entry);
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
			_focus.Track(input);
			body.Children(input);
		}
		if (entry.Patch is { Length: > 0 } patch)
		{
			body.Children(SectionLabel(Strings.ToolDiffLabel));
			var host = new Border();
			var unified = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffUnified)).OnClick(() => host.Child(DiffView.Render(patch, _focus)));
			var sideBySide = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffSideBySide)).OnClick(() => host.Child(DiffView.RenderSideBySide(patch, _focus)));
			host.Child(DiffView.Render(patch, _focus));
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
			_focus.Track(text);
			body.Children(text);
		}
		foreach (var image in entry.Images) body.Children(SessionTab.ImageView(image, _preview));
		return new Border().StyleName(AppStyles.Code).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2)).Margin(new Thickness(0, AppTheme.Space2, 0, 0)).Child(body);
	}

	private static TextBlock Timing(ChatEntry entry)
	{
		// 运行中的工具行从 ToolExecutionStartEvent 拿开始时刻，由 LiveDurationText 自己跳秒；
		// ask_question 的等待是人在读题，不能用工具秒表把思考时间算进去（PiDeck 同义）。
		TextBlock timing = entry switch
		{
			{ DurationMs: { } duration } when entry.ToolName is not "ask_question" => new TextBlock().Text(Strings.FormatDuration(duration)),
			{ IsStreaming: true, ToolName: "ask_question" } => new TextBlock().Text(Strings.AskWaitingForAnswer),
			{ IsStreaming: true, ToolStartedAt: { } startedAt } => new LiveDurationText(startedAt),
			_ => new TextBlock(),
		};
		timing.FontSize(AppTheme.FontSizeMicro).SubtleText();
		if (timing.Text.Length == 0 || entry.ToolName is "ask_question") return timing;
		// 读数用等宽数字；只有运行中的秒表需要固定最小宽度，避免每 100 ms 跳一次时
		// 把后面的命令预览推来推去（已完成的固定耗时不需要多占位置）。
		timing.FontFamily(AppTheme.MonoFont);
		if (timing is LiveDurationText) timing.TextAlignment(TextAlignment.Right).MinWidth(44);
		return timing;
	}

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
