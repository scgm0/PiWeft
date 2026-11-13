using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Globalization;

namespace PiWeft.Desktop;

internal sealed class ToolMessageView
{
	private readonly TextBlock _stateText;
	private readonly TextBlock _detail;
	private readonly StickyExpander _expander;
	private readonly TextFocusScrollGuard _focus;
	private readonly Action<ImageSource>? _preview;
	private readonly Action<string>? _copy;
	// 就地更新会换掉这个引用（条目不可变，事件到达时整份换新）；主题回调和行体构建都读它。
	private ChatEntry _entry;

	public ToolMessageView(ChatEntry entry, TextFocusScrollGuard focus, Action<ImageSource>? preview = null, Action<string>? copy = null)
	{
		_entry = entry;
		_focus = focus;
		_preview = preview;
		_copy = copy;
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
		var details = ToolCallDetails.For(entry);
		var body = new StackPanel().Spacing(AppTheme.Space2);
		Button? copy = null;
		if (_copy is not null && entry.ToolArguments is { Length: > 0 } arguments &&
			(details.Fields.Count > 0 || details.Exchanges.Count > 0 || details.BlockText is { Length: > 0 } || details.Todos.Count > 0 || details.Fallback is { Length: > 0 }))
		{
			// 参数分栏后不再是可选中的文本，给一个整包复制入口；原文保持 JSON，不带展示层的改写。
			copy = AppIcons.Button("copy", Strings.CopyToolArguments, () => _copy(arguments), size: 12);
		}
		var copyPending = copy is not null;
		var answered = details.Exchanges.Count > 0 && details.Exchanges.All(static exchange => exchange.Answer is { Length: > 0 });
		if (details.Fields.Count > 0)
		{
			var fields = FieldRows(details.Fields);
			body.Children(copyPending ? WithTrailing(fields, copy!) : fields);
			copyPending = false;
		}
		if (details.Exchanges.Count > 0)
		{
			// 答案就贴在题干下；只有没作答（取消/无答案）时才在结果区原样贴回去。
			body.Children(SectionRow(answered ? Strings.ToolAnswersLabel : Strings.ToolQuestionLabel, copyPending ? copy : null));
			copyPending = false;
			body.Children(ExchangeRows(details.Exchanges));
		}
		if (details.BlockText is { Length: > 0 } blockText)
		{
			body.Children(SectionRow(details.BlockLabel ?? Strings.ToolArgsLabel, copyPending ? copy : null));
			copyPending = false;
			body.Children(Code(blockText));
		}
		if (details.Todos.Count > 0)
		{
			body.Children(SectionRow(Strings.ToolPlanLabel, copyPending ? copy : null));
			copyPending = false;
			body.Children(TodoRows(details.Todos));
		}
		if (details.Fallback is { Length: > 0 } fallback)
		{
			body.Children(SectionRow(Strings.ToolArgsLabel, copyPending ? copy : null));
			copyPending = false;
			body.Children(Code(fallback));
		}
		if (entry.Patch is { Length: > 0 } patch)
		{
			var host = new Border();
			var unified = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffUnified)).OnClick(() => ReplaceDiff(host, () => DiffView.Render(patch, _focus)));
			var sideBySide = new Button().StyleName(AppStyles.CodeButton).Content(CodeLabel(Strings.DiffSideBySide)).OnClick(() => ReplaceDiff(host, () => DiffView.RenderSideBySide(patch, _focus)));
			ReplaceDiff(host, () => DiffView.Render(patch, _focus));
			// 分区标题与视图切换同一行：展开后第一眼看到改动，不用先跨过一行按钮。
			body.Children(new Grid().Columns("*,Auto,Auto").Children(
				SectionLabel(Strings.ToolDiffLabel).Column(0).VerticalAlignment(VerticalAlignment.Center),
				unified.Column(1).Margin(new Thickness(6, 0, 0, 0)),
				sideBySide.Column(2).Margin(new Thickness(4, 0, 0, 0))), host);
		}
		if (entry.ToolDetails is System.Text.Json.Nodes.JsonObject detailNode &&
			detailNode["imageUnsupportedByModel"]?.GetValue<string>() is { Length: > 0 } unsupportedModel)
		{
			// pi 1.5.2+：警告不进结果正文，桌面从 details 补一行给用户看。
			body.Children(new TextBlock().Text(Strings.ToolImageUnsupported(unsupportedModel))
				.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap));
		}

		var result = answered ? null : ResultText(entry, details);
		if (result is { Length: > 0 })
		{
			body.Children(SectionLabel(Strings.ToolResultLabel));
			body.Children(Code(result));
		}
		foreach (var image in entry.Images) body.Children(SessionTab.ImageView(image, _preview));
		return new Border().StyleName(AppStyles.Code).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2)).Margin(new Thickness(0, AppTheme.Space2, 0, 0)).Child(body);
	}

	private static string? ResultText(ChatEntry entry, ToolCallDetails details)
	{
		var source = entry.Text;
		var text = source.AsSpan().TrimEnd("\r\n");
		if (text.Length == 0) return null;
		// 运行中的工具在拿到结果前，Text 是参数或「→ 名称 参数」占位，别再当成结果重复一遍。
		// 中断的工具也可能停在参数占位上；完成后的输出即使碰巧和命令/内容相同也是真结果。
		if (entry.ToolArguments is { Length: > 0 } arguments &&
			((entry.IsStreaming || entry.IsStopped) &&
				(text.SequenceEqual(arguments.AsSpan()) || text.StartsWith("→ ", StringComparison.Ordinal) ||
				 entry.IsStreaming && details.BlockText is { } block && text.SequenceEqual(block.AsSpan().TrimEnd("\r\n")))))
		{
			return null;
		}

		return text.Length == source.Length ? source : text.ToString();
	}

	private static Grid WithTrailing(Element content, Button copy) => new Grid().Columns("*,Auto").Children(
		content.Column(0),
		copy.Column(1).VerticalAlignment(VerticalAlignment.Top));

	private static Grid SectionRow(string label, Button? copy)
	{
		var row = new Grid().Columns("*,Auto").Children(
			SectionLabel(label).Column(0).VerticalAlignment(VerticalAlignment.Center));
		if (copy is not null) row.Children(copy.Column(1).VerticalAlignment(VerticalAlignment.Center));
		return row;
	}

	private Grid FieldRows(IReadOnlyList<ToolCallField> fields)
	{
		// 一个 Grid 而不是每行一个：Auto 标签列要跨行共用，否则「忽略大小写」会把值列推歪。
		var grid = new Grid().Columns("Auto,*")
			.Rows(string.Join(",", Enumerable.Repeat("Auto", fields.Count)));
		for (var index = 0; index < fields.Count; index++)
		{
			var field = fields[index];
			var label = new TextBlock().Text(field.Label)
				.FontSize(AppTheme.FontSizeMicro).SubtleText()
				.Margin(new Thickness(0, 1, 12, 1)).VerticalAlignment(VerticalAlignment.Center);
			// 值用可选中文本控件：路径、模式可以直接拖选复制，和 bash 命令一致。
			// 居中而不是顶对齐：值用的字号比标签大，顶对齐会让文字基线差两三像素。
			var value = Code(field.Value).Margin(new Thickness(0, 1, 0, 1))
				.VerticalAlignment(VerticalAlignment.Center);
			grid.Children(label.Row(index).Column(0), value.Row(index).Column(1));
		}
		return grid;
	}

	private static void ReplaceDiff(Border host, Func<UIElement> render)
	{
		// 反复切换统一/并排会换掉整棵 diff 子树：先释放旧控件，别让 TextBox 的事件订阅跟着堆。
		if (host.Child is { } old) UiLifetime.Release(old);
		host.Child(render());
	}

	private StackPanel TodoRows(IReadOnlyList<ToolCallTodo> todos)
	{
		var panel = new StackPanel().Spacing(4);
		foreach (var todo in todos)
		{
			var text = Selectable(todo.Text).VerticalAlignment(VerticalAlignment.Center);
			text.WithTheme((theme, block) =>
			{
				var palette = AppTheme.For(theme);
				block.Foreground = todo.Status switch
				{
					"completed" => palette.TextFaint,
					"in_progress" => palette.TextPrimary,
					_ => palette.TextMuted,
				};
				AppStyles.RefreshTextView(block);
			});
			panel.Children(new Grid().Columns("Auto,*").Children(
				new TodoStatusGlyph(todo.Status).Column(0),
				text.Column(1).Margin(new Thickness(8, 0, 0, 0))));
		}
		return panel;
	}

	private StackPanel ExchangeRows(IReadOnlyList<ToolCallExchange> exchanges)
	{
		var rows = new StackPanel().Spacing(AppTheme.Space2);
		for (var index = 0; index < exchanges.Count; index++)
		{
			var exchange = exchanges[index];
			var content = new StackPanel().Spacing(2).Children(Prose(exchange.Question, muted: true));
			if (exchange.Answer is { Length: > 0 } answer)
			{
				content.Children(new Grid().Columns("Auto,*").Children(
					new TextBlock().Text("→").FontSize(AppTheme.FontSizeCaption).FaintText()
						.Margin(new Thickness(0, 0, 6, 0)).VerticalAlignment(VerticalAlignment.Top).Column(0),
					Prose(answer, muted: false).Column(1)));
			}
			// 序号列兼作对齐基准：多题时一眼能把答案对回题干。
			rows.Children(new Grid().Columns("18,*").Children(
				new TextBlock().Text((index + 1).ToString(CultureInfo.InvariantCulture))
					.FontSize(AppTheme.FontSizeMicro).FaintText()
					.Margin(new Thickness(0, 2, 0, 0)).VerticalAlignment(VerticalAlignment.Top).Column(0),
				content.Column(1)));
		}
		return rows;
	}

	private MultiLineTextBox Prose(string text, bool muted)
	{
		var view = Selectable(text);
		view.WithTheme((theme, block) =>
		{
			block.Foreground = muted ? AppTheme.For(theme).TextMuted : AppTheme.For(theme).TextPrimary;
			AppStyles.RefreshTextView(block);
		});
		return view;
	}

	private MultiLineTextBox Selectable(string text)
	{
		var view = MessageText.Create(text, fontSize: AppTheme.FontSizeCaption, smoothScroll: false);
		if (MessageText.IsLargeText(text)) view.MaxHeight(MessageText.LargeTextMaxHeight);
		view.Tag = null;
		_focus.Track(view);
		return view;
	}

	private MultiLineTextBox Code(string text)
	{
		var view = MessageText.Create(text, muted: true, mono: true, fontSize: AppTheme.FontSizeCaption);
		// 超过阈值时只给固定视口，TextViewLayout 只物化可见行；整篇撑高会在超大文件上卡顿。
		if (MessageText.IsLargeText(text)) view.MaxHeight(MessageText.LargeTextMaxHeight);
		// 代码表面随主题在深浅之间切换，文字颜色必须一起跟着换，不能停在创建时的快照上。
		view.WithTheme((theme, block) =>
		{
			block.Foreground = AppTheme.For(theme).CodeText;
			AppStyles.RefreshTextView(block);
		});
		view.Tag = null;
		_focus.Track(view);
		return view;
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
