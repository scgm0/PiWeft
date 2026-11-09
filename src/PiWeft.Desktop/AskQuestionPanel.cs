using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// <c>ask_question</c> 的会话内联卡片（对齐 PiDeck 的 <c>SessionRuntimeUiOverlay</c>）：
/// 钉在对话区底部、与输入框同级，不再用遮罩弹窗。卡片按批量信封渲染
/// select / multi_select / confirm / input / editor，回答以同构 JSON 返回。
/// 为什么不做弹窗：代理停下等回答时，用户往往还要上翻历史核对上下文。遮罩弹窗会
/// 把整窗（含对话）压暗并抢走滚动，PiDeck 的做法是让卡片和输入框一样待在列底，
/// 对话照常可读可滚；输入区在这段时间折叠，避免两处同时抢键盘焦点。
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
	private readonly List<string> _textDrafts = [];
	private readonly List<string> _customDrafts = [];
	private int _draftGeneration;
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
	private OverlayFade? _overlayFade;
	private int _overlayGeneration;
	private bool _finishing;

	private string? _alertSessionId;
	private string? _alertRequestId;
	private readonly Func<string>? _cwd;
	private AskTextTarget? _textTarget;

	public AskQuestionPanel(Action<bool>? openChanged = null, Func<string>? cwd = null)
	{
		_openChanged = openChanged;
		_cwd = cwd;

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
			.AllowDrop()
			.OnDragEnter(OnFilesDragOver)
			.OnDragOver(OnFilesDragOver)
			.OnDrop(OnFilesDropped)
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

	public Border View { get; }

	public bool IsOpen => View.IsVisible;

	public void Close() => Finish(cancelled: true);

	internal void BindSession(string? sessionId) => _alertSessionId = sessionId;

	/// 窗口级浮层模式：只给「会话还没建出来」的场景用（启动时的项目信任询问）。
	/// 普通 ask_question 一律走 <see cref="ShowAsync"/> 的会话内联卡片。
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
		_overlayGeneration++;
		TransientOverlay.Open(window, host, () => Finish(cancelled: true));
		_overlayFade = new OverlayFade(host);
		_overlayFade.Open();
		return ShowAsync(questions, review, cancellationToken);
	}

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
		// 先换代：上一批控件的迟到回调不能写进新一批草稿。
		_draftGeneration++;
		_review = review && questions.Count > 1;
		_answers.Clear();
		_answers.AddRange(Enumerable.Repeat<AskAnswer?>(null, questions.Count));
		_extraOptions.Clear();
		_multiSelected.Clear();
		_textDrafts.Clear();
		_customDrafts.Clear();
		for (var index = 0; index < questions.Count; index++)
		{
			_extraOptions.Add([]);
			_multiSelected.Add([]);
			// 草稿按题保存：切题、多选重绘、拖入路径后都不会丢；新一批用 prefill 重新开始。
			_textDrafts.Add(questions[index].Prefill ?? string.Empty);
			_customDrafts.Add(string.Empty);
		}

		_current = 0;
		_expanded = true;
		_submitting = false;
		// 新一批接管：上一批的淡出回调不再算数，也不能把这一批一起收掉。
		_finishing = false;
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
		if (!IsOpen || _completion is null || _finishing)
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
		var overlay = _overlayHost;
		if (overlay is not null && _overlayFade is { IsClosing: false } fade && Application.IsRunning)
		{
			_finishing = true;
			var window = _overlayWindow;
			var generation = _overlayGeneration;
			fade.Close(() => CompleteFinish(overlay, window, generation, completion, answers, incomplete));
			return;
		}

		CompleteFinish(overlay, _overlayWindow, _overlayGeneration, completion, answers, incomplete);
	}

	private void CompleteFinish(
		Grid? overlay,
		Window? window,
		int generation,
		TaskCompletionSource<string?> completion,
		List<AskAnswer> answers,
		bool incomplete)
	{
		// 淡出还没完就来了新一批：只把这个旧浮层从层里摘掉，共享状态与卡片归新一批。
		if (overlay is not null && generation != _overlayGeneration)
		{
			if (window is not null) TransientOverlay.Remove(window, overlay);
			overlay.Clear();
			completion.TrySetResult(AskQuestions.EncodeResponse(answers, incomplete));
			return;
		}

		_finishing = false;
		// 先作废旧控件的输入回调，再清空/重填草稿：IME 提交可能在这两步之间才到达。
		_draftGeneration++;
		ReleaseFocus();
		_questions = [];
		_answers.Clear();
		_extraOptions.Clear();
		_multiSelected.Clear();
		_textDrafts.Clear();
		_customDrafts.Clear();
		_draftAnswer = null;
		_textTarget = null;
		_primary = null;
		_body.Clear();
		_tabs.Clear();
		_footer.Clear();
		_submitting = false;
		View.IsVisible(false);
		if (overlay is not null)
		{
			_overlayHost = null;
			if (window is not null)
			{
				TransientOverlay.Remove(window, overlay);
			}

			_overlayWindow = null;
			_overlayFade = null;
			overlay.Clear();
		}

		_openChanged?.Invoke(false);
		completion.TrySetResult(AskQuestions.EncodeResponse(answers, incomplete));
	}

	/// 收起前把焦点从即将移除的编辑器上摘掉：IME 提交和按键不能再落到已脱离树的控件上。
	private void ReleaseFocus()
	{
		if (View.FindVisualRoot() is not Window window)
		{
			return;
		}

		if (window.FocusManager.FocusedElement is { } focused &&
			(ReferenceEquals(focused, View) || View.IsAncestorOf(focused)))
		{
			window.FocusManager.ClearFocus();
		}
	}

	private void ToggleExpanded()
	{
		_expanded = !_expanded;
		_content.IsVisible(_expanded);
		_chevron.Kind(_expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
		_collapse.HoverTip(_expanded ? Strings.AskCollapse : Strings.AskExpand);
	}


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
		// 每次重建正文都换一代：旧控件即使迟到 TextChanged 也不再写草稿。
		_draftGeneration++;
		_body.Clear();
		_draftAnswer = null;
		_textTarget = null;
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
		var questionIndex = _current;
		var draftGeneration = _draftGeneration;
		var custom = CustomInputBox(question, _customDrafts[questionIndex]);
		_textTarget = For(custom);
		var use = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.AskCustomUse).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.IsEnabled(!string.IsNullOrWhiteSpace(_customDrafts[questionIndex]))
			.OnClick(() => CommitCustom(question, custom));
		custom.OnTextChanged(value =>
		{
			if (draftGeneration != _draftGeneration)
			{
				return;
			}

			_customDrafts[questionIndex] = value;
			use.IsEnabled(!string.IsNullOrWhiteSpace(value));
		});
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

		var questionIndex = _current;
		var draftGeneration = _draftGeneration;
		var custom = CustomInputBox(question, _customDrafts[questionIndex]);
		_textTarget = For(custom);
		var add = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.AskCustomAdd).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.IsEnabled(!string.IsNullOrWhiteSpace(_customDrafts[questionIndex]));
		void AddCustom()
		{
			if (draftGeneration != _draftGeneration)
			{
				return;
			}

			var text = custom.Text?.Trim() ?? string.Empty;
			if (text.Length == 0)
			{
				return;
			}

			_extraOptions[questionIndex].Add(new AskOption(text, text, null));
			_multiSelected[questionIndex].Add(OptionsFor(question).Count - 1);
			_customDrafts[questionIndex] = string.Empty;
			custom.Text = string.Empty;
			add.IsEnabled(false);
			RenderCard();
		}

		add.OnClick(AddCustom);
		custom.OnTextChanged(value =>
		{
			if (draftGeneration != _draftGeneration)
			{
				return;
			}

			_customDrafts[questionIndex] = value;
			add.IsEnabled(!string.IsNullOrWhiteSpace(value));
		});
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
		var questionIndex = _current;
		var draftGeneration = _draftGeneration;
		var draft = _textDrafts[questionIndex];
		if (multiLine)
		{
			var editor = new MultiLineTextBox().PrimarySelection()
				.Height(120)
				.StyleName(AppStyles.TextInput)
				.Text(draft)
				.Placeholder(question.Placeholder ?? string.Empty);
			_body.Children(editor);
			_textTarget = new AskTextTarget(editor, () => editor.Text, value => editor.Text = value);
			_draftAnswer = () => AnswerOf(question, editor.Text);
			editor.OnTextChanged(value =>
			{
				if (draftGeneration != _draftGeneration)
				{
					return;
				}

				_textDrafts[questionIndex] = value;
				RefreshPrimaryState();
			});
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

		var text = new TextBox().PrimarySelection()
			.StyleName(AppStyles.TextInputLine)
			.Text(draft)
			.Placeholder(question.Placeholder ?? string.Empty);
		_body.Children(text);
		_textTarget = For(text);
		_draftAnswer = () => AnswerOf(question, text.Text);
		text.OnTextChanged(value =>
		{
			if (draftGeneration != _draftGeneration)
			{
				return;
			}

			_textDrafts[questionIndex] = value;
			RefreshPrimaryState();
		});
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

	private List<AskOption> OptionsFor(AskQuestion question)
	{
		var options = _current >= 0 && _current < _extraOptions.Count ? _extraOptions[_current] : [];
		var all = new List<AskOption>(question.Options ?? []);
		all.AddRange(options);
		return all;
	}

	private TextBox CustomInputBox(AskQuestion question, string text) => new TextBox().PrimarySelection()
		.StyleName(AppStyles.TextInputLine)
		.Text(text)
		.Placeholder(question.Placeholder ?? Strings.AskCustomPlaceholder);

	private static AskTextTarget For(TextBox box) => new(box, () => box.Text, value => box.Text = value);

	/// 卡片打开时主输入区已经折叠，拖放继续冒泡只会写进看不见的草稿。
	/// 卡片自己接管落点：有文本题就把路径插到当前输入框，没有就明确拒绝。
	private void OnFilesDragOver(DragEventArgs e)
	{
		var accepts = IsOpen && _expanded && !_submitting && !_finishing && _textTarget is not null &&
			ComposerDrop.CanAccept(e.Data) && (e.AllowedEffects & DragDropEffects.Copy) != 0;
		e.Accepted = accepts;
		e.Effect = accepts ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnFilesDropped(DragEventArgs e)
	{
		OnFilesDragOver(e);
		if (!e.Accepted || _textTarget is not { } target)
		{
			return;
		}

		// 格式在 DragOver 时就已宣告，路径要到 Drop 才拿得到（X11 尤其如此）。
		e.Accepted = false;
		e.Effect = DragDropEffects.None;
		var paths = ComposerDrop.Paths(e.Data, _cwd?.Invoke() ?? Environment.CurrentDirectory);
		if (paths.Count == 0)
		{
			return;
		}

		InsertReferences(target, paths);
		e.Accepted = true;
		e.Effect = DragDropEffects.Copy;
	}

	private static void InsertReferences(AskTextTarget target, IReadOnlyList<string> paths)
	{
		var text = target.Text;
		var start = Math.Clamp(target.Control.SelectionStart, 0, text.Length);
		var length = Math.Clamp(target.Control.SelectionLength, 0, text.Length - start);
		var end = start + length;
		var references = string.Join(" ", paths.Select(ComposerDrop.Reference));
		var before = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : string.Empty;
		var after = end < text.Length && !char.IsWhiteSpace(text[end]) ? " " : string.Empty;
		var insertion = before + references + after;
		target.Text = string.Concat(text.AsSpan(0, start), insertion, text.AsSpan(end));
		target.Control.Select(start + insertion.Length, 0);
		target.Control.Focus();
		target.Control.ScrollToCaret();
	}

	private sealed class AskTextTarget(TextBase control, Func<string> getText, Action<string> setText)
	{
		public TextBase Control { get; } = control;

		public string Text
		{
			get => getText();
			set => setText(value);
		}
	}

	/// 卡片打开后把焦点交给真正要用的控件：文本题给输入框（一进来就能打字），
	/// 选项题给第一个可点的按钮（回车/空格就能选）。之前主按钮禁用时一个控件都拿不到焦点，
	/// 用户得先点一下才能输入，Esc 也递不进来。
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

	/// 主按钮上的文字。字色不写死：清色继承 Button 的 Foreground，主按钮样式自己按状态切换
	/// （可用 TextOnAccent、禁用 TextFaint）。以前这里复用 <see cref="Label"/>（PrimaryText），
	/// 深色主题下近白的字压在近白的强调底上，「下一题」看起来像一块禁用态的灰斑；改成写死
	/// OnAccentText 又会在禁用时变成深色字压深色底。跟随底色才是这两个失败态的一般解。
	private static TextBlock AccentLabel(string text) => new TextBlock()
		.Text(text)
		.FontSize(AppTheme.FontSizeCaption)
		.FontWeight(FontWeight.Medium);
}
