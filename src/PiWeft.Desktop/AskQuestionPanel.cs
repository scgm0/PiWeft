using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// <summary>
/// <c>ask_question</c> 的会话内联卡片（对齐 PiDeck 的 <c>SessionRuntimeUiOverlay</c>）：
/// 钉在对话区底部、与输入框同级，不再用遮罩弹窗。卡片按批量信封渲染
/// select / multi_select / confirm / input / editor，回答以同构 JSON 返回。
/// <para>
/// 为什么不做弹窗：代理停下等回答时，用户往往还要上翻历史核对上下文。遮罩弹窗会
/// 把整窗（含对话）压暗并抢走滚动，PiDeck 的做法是让卡片和输入框一样待在列底，
/// 对话照常可读可滚；输入区在这段时间折叠，避免两处同时抢键盘焦点。
/// </para>
/// </summary>
internal sealed class AskQuestionPanel
{
	private readonly TextBlock _title = new();
	private readonly TextBlock _status = new();
	private readonly ProgressBar _progress = new();
	private readonly StackPanel _tabs;
	private readonly ScrollViewer _tabsScroll;
	private readonly StackPanel _body = new();
	private readonly ScrollViewer _bodyScroll;
	private readonly StackPanel _footer;
	private readonly StackPanel _content;
	private readonly Button _collapse;
	private readonly GlyphElement _chevron;
	private readonly Action<bool>? _openChanged;

	private IReadOnlyList<AskQuestion> _questions = [];
	private readonly List<AskAnswer?> _answers = [];
	private readonly List<List<AskOption>> _extraOptions = [];
	private readonly List<HashSet<int>> _multiSelected = [];
	private int _current;
	private bool _review;
	private bool _expanded = true;
	private bool _submitting;
	private TaskCompletionSource<string?>? _completion;
	private CancellationTokenRegistration _cancellation;
	private Func<AskAnswer?>? _draftAnswer;
	private Button? _primary;
	private Grid? _overlayHost;
	private Window? _overlayWindow;

	/// <summary>等待输入提醒的会话标识与本次请求标识（同一批只响一次，回答完再重置）。</summary>
	private string? _alertSessionId;
	private string? _alertRequestId;

	public AskQuestionPanel(Action<bool>? openChanged = null)
	{
		_openChanged = openChanged;

		_chevron = new GlyphElement()
			.Kind(GlyphKind.ChevronUp)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);
		_collapse = new Button()
			.Ghost()
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Width(22)
			.Height(24)
			.Content(_chevron)
			.OnClick(ToggleExpanded)
			.HoverTip(Strings.AskCollapse);

		_title.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium).PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
		_status.FontSize(AppTheme.FontSizeMicro).MutedText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2, 0, 0, 0));

		var header = new Grid()
			.Columns("Auto,Auto,*,Auto,Auto")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				_collapse.Column(0),
				AppIcons.Icon("circle-question-mark", AppStyles.RoleInfo, 14)
					.Margin(new Thickness(0, 0, AppTheme.Space1 + 2, 0))
					.Column(1),
				_title.Column(2),
				_status.Column(3),
				AppIcons.Button("x", Strings.AskCancel, () => Finish(cancelled: true), size: 13)
					.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
					.Width(24).Height(24)
					.Column(4));

		_progress.Minimum(0).Maximum(100).Height(3).Margin(new Thickness(0, 0, 0, AppTheme.Space1));

		_tabs = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2);
		_tabsScroll = new ScrollViewer()
			.Content(_tabs)
			.AutoHorizontalScroll()
			.NoVerticalScroll()
			.MaxHeight(28)
			.IsVisible(false);

		_bodyScroll = new ScrollViewer()
			.Content(_body)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.MaxHeight(320);
		_body.Spacing(AppTheme.Space2);

		_footer = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(false);

		_content = new StackPanel().Spacing(AppTheme.Space1)
			.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3))
			.Children(_progress, _tabsScroll, _bodyScroll, _footer);

		View = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.BorderThickness(1)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.IsVisible(false)
			// Esc = 取消整批（PiDeck 同款）：卡片是会话里唯一的阻塞点，键盘要能直接退出。
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape)
				{
					return;
				}

				key.Handled = true;
				Finish(cancelled: true);
			})
			.Child(new StackPanel().Children(
				new Border()
					.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space1, 0))
					.Child(header),
				_content));
	}

	/// <summary>卡片本体；由会话标签放进输入列（对话区与输入框之间）。</summary>
	public Border View { get; }

	public bool IsOpen => View.IsVisible;

	/// <summary>强制关闭（标签或窗口销毁时调用），等待中的请求按取消处理。</summary>
	public void Close() => Finish(cancelled: true);

	/// <summary>绑定会话标识：提示音按会话做批次去重，和 PiDeck 的 waiting 提醒同义。</summary>
	internal void BindSession(string? sessionId) => _alertSessionId = sessionId;

	/// <summary>
	/// 窗口级浮层模式：只给「会话还没建出来」的场景用（启动时的项目信任询问）。
	/// 普通 ask_question 一律走 <see cref="ShowAsync"/> 的会话内联卡片。
	/// </summary>
	public Task<string?> ShowOverlayAsync(Window window, IReadOnlyList<AskQuestion> questions, bool review, CancellationToken cancellationToken)
	{
		var backdrop = new Button()
			.Background(new Color(0xE0, 0, 0, 0))
			.BorderThickness(0)
			.CornerRadius(0)
			.Cursor(CursorType.Arrow)
			.Focusable(false)
			.OnClick(() => Finish(cancelled: true));
		var shell = new Border()
			.StyleName(AppStyles.Panel)
			.CornerRadius(AppTheme.RadiusLg)
			.Padding(new Thickness(AppTheme.Space3))
			.MaxWidth(680)
			.MaxHeight(620)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.Child(View);
		var host = new Grid().Children(backdrop, shell.OnMouseDown(args => args.Handled = true));

		_overlayHost = host;
		_overlayWindow = window;
		TransientOverlay.Open(window, host, () => Finish(cancelled: true));
		return ShowAsync(questions, review, cancellationToken);
	}

	/// <summary>展示卡片并等待回答。</summary>
	public Task<string?> ShowAsync(IReadOnlyList<AskQuestion> questions, bool review, CancellationToken cancellationToken)
	{
		if (questions.Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		// 同一面板一次只处理一批：上一批还没答完就来了新的，按取消收尾。
		if (IsOpen)
		{
			Finish(cancelled: true);
		}

		_questions = questions;
		_review = review && questions.Count > 1;
		_answers.Clear();
		_answers.AddRange(Enumerable.Repeat<AskAnswer?>(null, questions.Count));
		_extraOptions.Clear();
		_multiSelected.Clear();
		for (var index = 0; index < questions.Count; index++)
		{
			_extraOptions.Add([]);
			_multiSelected.Add([]);
		}

		_current = 0;
		_expanded = true;
		_submitting = false;
		_completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_cancellation = cancellationToken.Register(static state => ((AskQuestionPanel)state!).DispatchCancel(), this);

		View.IsVisible(true);
		_openChanged?.Invoke(true);
		RenderCard();
		// 代理停下来等用户回答：这是「需要你」的唯一现场，等用户真的看一眼再说话。
		_alertRequestId = Guid.NewGuid().ToString("N");
		SoundAlerts.Shared.RequestWaiting(_alertSessionId, _alertRequestId);
		FocusCurrent();
		return _completion.Task;
	}

	private void DispatchCancel()
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			Finish(cancelled: true);
			return;
		}

		dispatcher.BeginInvoke(() => Finish(cancelled: true));
	}

	private void Finish(bool cancelled)
	{
		if (!IsOpen || _completion is null)
		{
			return;
		}

		var completion = _completion;
		_completion = null;
		_cancellation.Dispose();
		SoundAlerts.Shared.CompleteWaiting(_alertSessionId, _alertRequestId);
		_alertRequestId = null;

		var answers = !cancelled
			? _answers.Where(static answer => answer is not null).Select(static answer => answer!).ToList()
			: [];
		// 未作答的问题按取消处理，让工具结果明确说明。
		var incomplete = cancelled || answers.Count < _questions.Count;

		_questions = [];
		_answers.Clear();
		_extraOptions.Clear();
		_multiSelected.Clear();
		_draftAnswer = null;
		_primary = null;
		_body.Clear();
		_tabs.Clear();
		_footer.Clear();
		_submitting = false;
		View.IsVisible(false);
		if (_overlayHost is { } overlay)
		{
			_overlayHost = null;
			if (_overlayWindow is { } window)
			{
				TransientOverlay.Remove(window, overlay);
			}

			_overlayWindow = null;
			overlay.Clear();
		}

		_openChanged?.Invoke(false);
		completion.TrySetResult(AskQuestions.EncodeResponse(answers, incomplete));
	}

	private void ToggleExpanded()
	{
		_expanded = !_expanded;
		_content.IsVisible(_expanded);
		_chevron.Kind(_expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
		_collapse.HoverTip(_expanded ? Strings.AskCollapse : Strings.AskExpand);
	}

	// ------------------------------------------------------------------ 渲染

	private void RenderCard()
	{
		if (!IsOpen)
		{
			return;
		}

		var total = _questions.Count;
		var answered = _answers.Count(static answer => answer is not null);
		_title.Text(Strings.AskTitle);
		_status.Text(total > 1 ? Strings.AskProgress(answered, total) : string.Empty);
		_status.IsVisible(total > 1);
		_progress.Value = total > 0 ? answered * 100d / total : 0;
		_progress.IsVisible(total > 1);
		_content.IsVisible(_expanded);
		_chevron.Kind(_expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
		_collapse.HoverTip(_expanded ? Strings.AskCollapse : Strings.AskExpand);

		RenderTabs();
		RenderBody();
		RenderFooter();
	}

	private void RenderTabs()
	{
		_tabs.Clear();
		var total = _questions.Count;
		_tabsScroll.IsVisible(total > 1);
		if (total <= 1)
		{
			return;
		}

		for (var index = 0; index < total; index++)
		{
			var captured = index;
			var answered = _answers[index] is not null;
			var active = _current == index;
			var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center).Children(
					new TextBlock().Text((index + 1).ToString()).FontSize(AppTheme.FontSizeMicro)
						.FontWeight(FontWeight.SemiBold).MutedText(),
					new TextBlock().Text(Shorten(_questions[index].Question)).FontSize(AppTheme.FontSizeMicro)
						.TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(140)
						.Role(active ? AppStyles.RolePrimary : AppStyles.RoleMuted));
			if (answered)
			{
				content.Children(AppIcons.Icon("check", AppStyles.RoleSuccess, 11));
			}

			_tabs.Children(new Button()
				.StyleName(active ? AppStyles.SelectedButton : AppStyles.GhostButton)
				.Height(24)
				.Padding(new Thickness(AppTheme.Space1 + 2, 0, AppTheme.Space1 + 2, 0))
				.Content(content)
				.HoverTip(_questions[index].Question)
				.OnClick(() => JumpTo(captured)));
		}

		if (_review)
		{
			var active = _current == total;
			_tabs.Children(new Button()
				.StyleName(active ? AppStyles.SelectedButton : AppStyles.GhostButton)
				.Height(24)
				.Padding(new Thickness(AppTheme.Space1 + 2, 0, AppTheme.Space1 + 2, 0))
				.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
					.VerticalAlignment(VerticalAlignment.Center).Children(
						AppIcons.Icon("list-checks", active ? AppStyles.RolePrimary : AppStyles.RoleMuted, 12),
						new TextBlock().Text(Strings.AskReviewTab).FontSize(AppTheme.FontSizeMicro)
							.Role(active ? AppStyles.RolePrimary : AppStyles.RoleMuted)))
				.HoverTip(Strings.AskReviewHint)
				.OnClick(() => JumpTo(total)));
		}
	}

	private void JumpTo(int index)
	{
		if (_submitting || index < 0 || index > (_review ? _questions.Count : _questions.Count - 1))
		{
			return;
		}

		_current = index;
		RenderCard();
	}

	private void RenderBody()
	{
		_body.Clear();
		_draftAnswer = null;
		if (_review && _current == _questions.Count)
		{
			RenderReview();
			return;
		}

		if (_current < 0 || _current >= _questions.Count)
		{
			return;
		}

		var question = _questions[_current];
		_body.Children(new TextBlock()
			.Text(question.Question)
			.FontSize(AppTheme.FontSizeControl)
			.PrimaryText()
			.TextWrapping(TextWrapping.Wrap));
		if (_questions.Count > 1)
		{
			_body.Children(new TextBlock()
				.Text(Strings.AskQuestionProgress(_current + 1, _questions.Count))
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		// 一行操作提示：单选/多选/确认/输入各自的推进方式不一样，卡片又没有说明文字，
		// 不写清楚用户会以为点了选项就算提交完了。
		var hint = HintFor(question);
		if (hint.Length > 0)
		{
			_body.Children(new TextBlock()
				.Text(hint)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText()
				.TextWrapping(TextWrapping.Wrap));
		}

		switch (question.Type)
		{
			case "select":
				RenderSelect(question);
				break;
			case "multi_select":
				RenderMultiSelect(question);
				break;
			case "confirm":
				RenderConfirm(question);
				break;
			case "editor":
				RenderText(question, multiLine: true);
				break;
			default:
				RenderText(question, multiLine: false);
				break;
		}
	}

	private void RenderOptions(AskQuestion question, bool multi)
	{
		var options = OptionsFor(question);
		var selected = multi ? _multiSelected[_current] : [];
		for (var index = 0; index < options.Count; index++)
		{
			var captured = index;
			var option = options[index];
			var isSelected = multi && selected.Contains(captured);
			var label = new StackPanel().Spacing(1).Children(
				new TextBlock().Text(option.Label).FontSize(AppTheme.FontSizeControl).PrimaryText());
			if (option.Description is { Length: > 0 } description)
			{
				label.Children(new TextBlock().Text(description).FontSize(AppTheme.FontSizeMicro)
					.FaintText().TextWrapping(TextWrapping.Wrap));
			}

			var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center);
			if (multi && isSelected)
			{
				content.Children(AppIcons.Icon("check", AppStyles.RoleSuccess, 13));
			}

			content.Children(label);

			_body.Children(new Button()
				.StyleName(isSelected ? AppStyles.SelectedButton : AppStyles.CardMutedButton)
				.HorizontalAlignment(HorizontalAlignment.Stretch)
				.Padding(new Thickness(AppTheme.Space2, 6, AppTheme.Space2, 6))
				.Content(content)
				.OnClick(() =>
				{
					if (_submitting)
					{
						return;
					}

					if (multi)
					{
						if (!selected.Add(captured))
						{
							selected.Remove(captured);
						}

						RenderCard();
						return;
					}

					Commit(new AskAnswer(question.Id, question.Type, JsonValue.Create(option.Value), option.Label, false));
				}));
		}
	}

	private void RenderSelect(AskQuestion question)
	{
		RenderOptions(question, multi: false);

		// 选择题总留一个自定义输入（与 PiDeck 的 allowOther 同义）：Enter 或「使用」提交。
		var custom = CustomInputBox(question);
		var use = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.AskCustomUse).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.IsEnabled(false)
			.OnClick(() => CommitCustom(question, custom));
		custom.OnTextChanged(value => use.IsEnabled(!string.IsNullOrWhiteSpace(value)));
		custom.OnKeyDown(key =>
		{
			if (key.Key != Key.Enter)
			{
				return;
			}

			key.Handled = true;
			CommitCustom(question, custom);
		});
		_body.Children(new DockPanel().LastChildFill()
			.Children(use.DockRight(), custom));
	}

	private void RenderMultiSelect(AskQuestion question)
	{
		RenderOptions(question, multi: true);

		var custom = CustomInputBox(question);
		var add = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.AskCustomAdd).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.IsEnabled(false);
		void AddCustom()
		{
			var text = custom.Text?.Trim() ?? string.Empty;
			if (text.Length == 0)
			{
				return;
			}

			_extraOptions[_current].Add(new AskOption(text, text, null));
			_multiSelected[_current].Add(OptionsFor(question).Count - 1);
			custom.Text = string.Empty;
			add.IsEnabled(false);
			RenderCard();
		}

		add.OnClick(AddCustom);
		custom.OnTextChanged(value => add.IsEnabled(!string.IsNullOrWhiteSpace(value)));
		custom.OnKeyDown(key =>
		{
			if (key.Key != Key.Enter)
			{
				return;
			}

			key.Handled = true;
			AddCustom();
		});
		_body.Children(new DockPanel().LastChildFill().Children(add.DockRight(), custom));

		_draftAnswer = () =>
		{
			var options = OptionsFor(question);
			var values = new List<string>();
			var labels = new List<string>();
			var customUsed = false;
			foreach (var index in _multiSelected[_current].Order())
			{
				if (index < 0 || index >= options.Count)
				{
					continue;
				}

				values.Add(options[index].Value);
				labels.Add(options[index].Label);
				if (index >= (question.Options?.Count ?? 0))
				{
					customUsed = true;
				}
			}

			return values.Count == 0
				? null
				: new AskAnswer(
					question.Id,
					question.Type,
					new JsonArray([.. values.Select(static value => (JsonNode?)JsonValue.Create(value))]),
					string.Join("、", labels),
					customUsed);
		};
	}

	private void RenderConfirm(AskQuestion question)
	{
		var yes = new Button().Primary().Content(AccentLabel(Strings.AskYes));
		yes.OnClick(() => Commit(new AskAnswer(question.Id, question.Type, JsonValue.Create(true), Strings.AskYes, false)));
		var no = new Button().StyleName(AppStyles.CardMutedButton).Content(Label(Strings.AskNo));
		no.OnClick(() => Commit(new AskAnswer(question.Id, question.Type, JsonValue.Create(false), Strings.AskNo, false)));
		if (!string.IsNullOrWhiteSpace(question.Prefill))
		{
			_body.Children(new TextBlock().Text(question.Prefill).FontSize(AppTheme.FontSizeMicro)
				.FaintText().TextWrapping(TextWrapping.Wrap));
		}

		_body.Children(new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.Children(yes, no));
	}

	private void RenderText(AskQuestion question, bool multiLine)
	{
		var prefill = question.Prefill ?? string.Empty;
		if (multiLine)
		{
			var editor = new MultiLineTextBox()
				.Height(120)
				.StyleName(AppStyles.TextInput)
				.Text(prefill)
				.Placeholder(question.Placeholder ?? string.Empty);
			_body.Children(editor);
			_draftAnswer = () => AnswerOf(question, editor.Text);
			editor.OnTextChanged(_ => RefreshPrimaryState());
			// 多行编辑沿用提交框的约定：Ctrl+Enter 提交，普通 Enter 换行。
			editor.OnKeyDown(key =>
			{
				if (key.Key != Key.Enter || !key.ControlKey)
				{
					return;
				}

				key.Handled = true;
				SubmitDraft();
			});
			return;
		}

		var text = new TextBox()
			.StyleName(AppStyles.TextInputLine)
			.Text(prefill)
			.Placeholder(question.Placeholder ?? string.Empty);
		_body.Children(text);
		_draftAnswer = () => AnswerOf(question, text.Text);
		text.OnTextChanged(_ => RefreshPrimaryState());
		text.OnKeyDown(key =>
		{
			if (key.Key != Key.Enter)
			{
				return;
			}

			key.Handled = true;
			SubmitDraft();
		});
	}

	/// <summary>本题的推进方式：卡片里没有说明文字，靠一行提示说清「点一下就算答完」还是「还要点提交」。</summary>
	private string HintFor(AskQuestion question)
	{
		if (_questions.Count == 1)
		{
			return question.Type switch
			{
				"select" => string.Empty,
				"confirm" => string.Empty,
				"multi_select" => Strings.AskHintMultiSelectLast,
				"editor" => Strings.AskHintEditor,
				_ => Strings.AskHintInputLast,
			};
		}

		var last = _current == _questions.Count - 1 && !_review;
		return question.Type switch
		{
			"select" => last ? Strings.AskHintSelectLast : Strings.AskHintSelect,
			"confirm" => last ? Strings.AskHintConfirmLast : Strings.AskHintConfirm,
			"multi_select" => Strings.AskHintMultiSelect,
			"editor" => Strings.AskHintEditor,
			_ => Strings.AskHintInput,
		};
	}

	private void RenderReview()
	{
		_body.Children(new TextBlock().Text(Strings.AskReviewTitle)
			.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.Medium).PrimaryText());
		_body.Children(new TextBlock().Text(Strings.AskReviewHint)
			.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap));

		for (var index = 0; index < _questions.Count; index++)
		{
			var captured = index;
			var answered = _answers[index] is not null;
			var label = _answers[index] is { } answer && answer.Label is { Length: > 0 } text
				? text
				: answered ? Strings.AskAnswered : Strings.AskUnanswered;
			_body.Children(new Button()
				.StyleName(AppStyles.CardMutedButton)
				.HorizontalAlignment(HorizontalAlignment.Stretch)
				.Padding(new Thickness(AppTheme.Space2, 5, AppTheme.Space2, 5))
				.Content(new Grid().Columns("18,*,Auto").Children(
					new TextBlock().Text((index + 1).ToString()).FontSize(AppTheme.FontSizeMicro)
						.FontWeight(FontWeight.SemiBold).MutedText().Column(0),
					new TextBlock().Text(_questions[index].Question).FontSize(AppTheme.FontSizeCaption)
						.PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis).Column(1),
					new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro)
						.Role(answered ? AppStyles.RoleSuccess : AppStyles.RoleFaint)
						.Margin(new Thickness(AppTheme.Space2, 0, 0, 0)).Column(2)))
				.OnClick(() => JumpTo(captured)));
		}
	}

	private void RenderFooter()
	{
		_footer.Clear();
		_primary = null;
		_footer.IsVisible(false);
		if (_review && _current == _questions.Count)
		{
			var previous = new Button().Ghost()
				.Content(new TextBlock().Text(Strings.AskPrevious).FontSize(AppTheme.FontSizeCaption).PrimaryText())
				.OnClick(() => JumpTo(Math.Max(0, _questions.Count - 1)));
			var submit = new Button().Primary()
				.Content(AccentLabel(Strings.AskSubmitAll))
				.IsEnabled(_answers.All(static answer => answer is not null))
				.OnClick(() => Finish(cancelled: false));
			_footer.Children(previous);
			_footer.Children(submit);
			_footer.IsVisible(true);
			return;
		}

		if (_current < 0 || _current >= _questions.Count)
		{
			return;
		}

		var question = _questions[_current];
		var needsExplicitSubmit = question.Type is "multi_select" or "input" or "editor";
		if (!needsExplicitSubmit)
		{
			// select / confirm：选项本身就是提交按钮，页脚只在多题时给一个回看入口。
			if (_questions.Count > 1)
			{
				_footer.Children(new Button().Ghost()
					.Content(new TextBlock().Text(Strings.AskPrevious).FontSize(AppTheme.FontSizeCaption).PrimaryText())
					.IsEnabled(_current > 0)
					.OnClick(() => JumpTo(_current - 1)));
				_footer.IsVisible(true);
			}

			return;
		}

		if (_questions.Count > 1)
		{
			_footer.Children(new Button().Ghost()
				.Content(new TextBlock().Text(Strings.AskPrevious).FontSize(AppTheme.FontSizeCaption).PrimaryText())
				.IsEnabled(_current > 0)
				.OnClick(() => JumpTo(_current - 1)));
		}

		_primary = new Button().Primary()
			.Content(AccentLabel(_current == _questions.Count - 1 && !_review ? Strings.AskSubmit : Strings.AskNext))
			.IsEnabled(_draftAnswer?.Invoke() is not null)
			.OnClick(SubmitDraft);
		_footer.Children(_primary);
		_footer.IsVisible(true);
	}

	private void SubmitDraft()
	{
		if (_submitting || _draftAnswer?.Invoke() is not { } answer)
		{
			return;
		}

		Commit(answer);
	}

	/// <summary>页脚主按钮的可用态跟着草稿走：空输入不能提交，填上即亮。</summary>
	private void RefreshPrimaryState()
	{
		if (_primary is { } primary)
		{
			primary.IsEnabled(_draftAnswer?.Invoke() is not null);
		}
	}

	private void CommitCustom(AskQuestion question, TextBox box)
	{
		var text = box.Text?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return;
		}

		Commit(new AskAnswer(question.Id, question.Type, JsonValue.Create(text), null, true));
	}

	private static AskAnswer? AnswerOf(AskQuestion question, string? value)
	{
		var text = value?.Trim() ?? string.Empty;
		return text.Length == 0
			? null
			: new AskAnswer(question.Id, question.Type, JsonValue.Create(text), null, false);
	}

	/// <summary>写入一题答案并推进：全部答完即提交，否则跳到下一道未答的题。</summary>
	private void Commit(AskAnswer answer)
	{
		if (_submitting || _current < 0 || _current >= _answers.Count)
		{
			return;
		}

		_answers[_current] = answer;
		if (_answers.All(static item => item is not null))
		{
			if (_review)
			{
				_current = _questions.Count;
				RenderCard();
				return;
			}

			_submitting = true;
			Finish(cancelled: false);
			return;
		}

		if (NextUnanswered() is { } next)
		{
			_current = next;
		}

		RenderCard();
	}

	private int? NextUnanswered()
	{
		var total = _questions.Count;
		for (var step = 1; step <= total; step++)
		{
			var index = (_current + step) % total;
			if (_answers[index] is null)
			{
				return index;
			}
		}

		return null;
	}

	/// <summary>选项 + 本题临时追加的自定义项（自定义项排在后面，索引稳定）。</summary>
	private List<AskOption> OptionsFor(AskQuestion question)
	{
		var options = _current >= 0 && _current < _extraOptions.Count ? _extraOptions[_current] : [];
		var all = new List<AskOption>(question.Options ?? []);
		all.AddRange(options);
		return all;
	}

	private TextBox CustomInputBox(AskQuestion question) => new TextBox()
		.StyleName(AppStyles.TextInputLine)
		.Placeholder(question.Placeholder ?? Strings.AskCustomPlaceholder);

	/// <summary>
	/// 卡片打开后把焦点交给真正要用的控件：文本题给输入框（一进来就能打字），
	/// 选项题给第一个可点的按钮（回车/空格就能选）。之前主按钮禁用时一个控件都拿不到焦点，
	/// 用户得先点一下才能输入，Esc 也递不进来。
	/// </summary>
	private void FocusCurrent()
	{
		if (_current < 0 || _current >= _questions.Count)
		{
			return;
		}

		var typing = _questions[_current].Type is "input" or "editor";
		UIElement? target = typing ? FindFirstEditor(_body) ?? (UIElement?)FindFirstFocusable() : FindFirstFocusable();
		if (target is null)
		{
			return;
		}

		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			target.Focus();
			return;
		}

		dispatcher.BeginInvoke(() => target.Focus());
	}

	private Button? FindFirstFocusable()
	{
		if (_primary is { IsEnabled: true } primary)
		{
			return primary;
		}

		foreach (var button in FindButtons(_body))
		{
			if (button.IsEnabled)
			{
				return button;
			}
		}

		return null;
	}

	/// <summary>卡片里第一个能打字的控件（单行框或多行框）。</summary>
	private static UIElement? FindFirstEditor(Element root)
	{
		foreach (var element in Walk(root))
		{
			if (element is TextBox or MultiLineTextBox)
			{
				return (UIElement)element;
			}
		}

		return null;
	}

	private static IEnumerable<Button> FindButtons(Element root)
	{
		foreach (var element in Walk(root))
		{
			if (element is Button button)
			{
				yield return button;
			}
		}
	}

	/// <summary>深度优先走一遍子树（标签、选项、输入框都在卡片自己这一小撮节点里）。</summary>
	private static IEnumerable<Element> Walk(Element root)
	{
		var pending = new Stack<Element>();
		pending.Push(root);
		while (pending.TryPop(out var element))
		{
			yield return element;

			if (element is IVisualTreeHost host)
			{
				var children = new List<Element>();
				host.VisitChildren(child =>
				{
					children.Add(child);
					return true;
				});
				for (var index = children.Count - 1; index >= 0; index--)
				{
					pending.Push(children[index]);
				}
			}
		}
	}

	private static string Shorten(string text)
	{
		var value = text.Trim().Replace('\n', ' ');
		return value.Length <= 16 ? value : value[..16] + "…";
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();

	/// <summary>
	/// 主按钮上的文字。字色不写死：清色继承 Button 的 Foreground，主按钮样式自己按状态切换
	/// （可用 TextOnAccent、禁用 TextFaint）。以前这里复用 <see cref="Label"/>（PrimaryText），
	/// 深色主题下近白的字压在近白的强调底上，「下一题」看起来像一块禁用态的灰斑；改成写死
	/// OnAccentText 又会在禁用时变成深色字压深色底。跟随底色才是这两个失败态的一般解。
	/// </summary>
	private static TextBlock AccentLabel(string text) => new TextBlock()
		.Text(text)
		.FontSize(AppTheme.FontSizeCaption)
		.FontWeight(FontWeight.Medium);
}
