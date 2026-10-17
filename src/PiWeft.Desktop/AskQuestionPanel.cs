using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// <summary>
/// D-01：<c>ask_question</c> 的桌面对话框。渲染与 PiDeck 相同的批量问询卡片
/// （select / multi_select / confirm / input / editor），并在关闭时返回同构 JSON。
/// </summary>
internal sealed class AskQuestionPanel
{
	private readonly StackPanel _body = new();
	private readonly Button _submit;
	private Window? _window;
	private Window? _themeWindow;
	private UIElement? _returnFocus;
	private TaskCompletionSource<string?>? _completion;
	private List<Func<AskAnswer>> _answers = [];

	public AskQuestionPanel()
	{
		_submit = new Button().Primary().Content(Label(Strings.AskSubmit)).OnClick(() => Finish(cancelled: false));
		var cancel = new Button().Content(Label(Strings.AskCancel)).OnClick(() => Finish(cancelled: true));

		var card = new Border()
			.StyleName(AppStyles.Panel)
			.CornerRadius(AppTheme.RadiusLg)
			.Padding(new Thickness(AppTheme.Space4))
			.MaxWidth(640)
			.MaxHeight(560)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.Child(new StackPanel()
				.Spacing(AppTheme.Space3)
				.Children(
					new TextBlock().Text(Strings.AskTitle).FontSize(AppTheme.FontSizeTitle).FontWeight(FontWeight.Bold).PrimaryText(),
					new ScrollViewer().SmoothScrolling().AutoVerticalScroll().NoHorizontalScroll().MaxHeight(420)
						.Content(_body.Spacing(AppTheme.Space4)),
					new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
						.Children(_submit, cancel)));

		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0xE0, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(() => Finish(cancelled: true)),
			card.OnMouseDown(args => args.Handled = true))
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					Finish(cancelled: true);
				}
			});
	}

	public UIElement View { get; }

	public bool IsOpen => View.IsVisible;

	/// <summary>强制关闭（标签或窗口销毁时调用），等待中的请求按取消处理。</summary>
	public void Close() => Finish(cancelled: true);

	/// <summary>展示卡片并等待回答；<paramref name="owner"/> 用来定位窗口。</summary>
	public Task<string?> ShowAsync(UIElement owner, IReadOnlyList<AskQuestion> questions, CancellationToken cancellationToken)
	{
		if (owner.FindVisualRoot() is not Window window || questions.Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		if (_window is not null && !ReferenceEquals(_window, window))
		{
			Finish(cancelled: true);
		}

		_window = window;
		if (!ReferenceEquals(_themeWindow, window))
		{
			if (_themeWindow is not null) _themeWindow.ThemeChanged -= OnWindowThemeChanged;
			_themeWindow = window;
			window.ThemeChanged += OnWindowThemeChanged;
		}
		_returnFocus = window.FocusManager.FocusedElement;
		_completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		cancellationToken.Register(static state => ((AskQuestionPanel)state!).Finish(cancelled: true), this);

		_body.Clear();
		_answers = new List<Func<AskAnswer>>(questions.Count);
		foreach (var question in questions)
		{
			BuildQuestion(question);
		}

		TransientOverlay.Open(window, View, () => Finish(cancelled: true));
		View.IsVisible(true);
		AppStyles.ApplyTextColors(View);
		_submit.Focus();
		return _completion.Task;
	}

	private void OnWindowThemeChanged(Theme oldTheme, Theme newTheme) => AppStyles.ApplyTextColors(View);

	private void BuildQuestion(AskQuestion question)
	{
		var block = new StackPanel().Spacing(AppTheme.Space2);
		block.Children(new TextBlock()
			.Text(question.Question)
			.FontSize(AppTheme.FontSizeChat)
			.PrimaryText()
			.TextWrapping(TextWrapping.Wrap));

		switch (question.Type)
		{
			case "select":
				BuildSelect(question, block, multi: false);
				break;
			case "multi_select":
				BuildSelect(question, block, multi: true);
				break;
			case "confirm":
				BuildConfirm(question, block);
				break;
			case "editor":
				BuildText(question, block, multiLine: true);
				break;
			default:
				BuildText(question, block, multiLine: false);
				break;
		}

		_body.Children(block);
	}

	private void BuildSelect(AskQuestion question, StackPanel block, bool multi)
	{
		var options = question.Options ?? [];
		var box = new TextBox()
			.Placeholder(question.Placeholder ?? Strings.AskCustomPlaceholder)
			.Text(question.Prefill ?? string.Empty);

		if (multi)
		{
			var selected = new bool[options.Count];
			for (var index = 0; index < options.Count; index++)
			{
				var captured = index;
				block.Children(new CheckBox()
					.Content(Label(Describe(options[index])))
					.OnCheckedChanged(value => selected[captured] = value));
			}

			block.Children(box);
			_answers.Add(() =>
			{
				var custom = box.Text?.Trim() ?? string.Empty;
				var values = new List<string>();
				for (var index = 0; index < options.Count; index++)
				{
					if (selected[index])
					{
						values.Add(options[index].Value);
					}
				}

				if (custom.Length > 0)
				{
					values.AddRange(custom.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				}

				return new AskAnswer(
					question.Id,
					question.Type,
					values.Count > 0 ? new JsonArray([.. values.Select(static value => (JsonNode?)JsonValue.Create(value))]) : null,
					null,
					custom.Length > 0);
			});
			return;
		}

		var labels = new List<string> { Strings.AskSelectPlaceholder };
		labels.AddRange(options.Select(Describe));
		var combo = new ComboBox().ItemsSource(ItemsView.Create(labels));
		combo.SelectedIndex = 0;
		block.Children(combo, box);
		_answers.Add(() =>
		{
			var custom = box.Text?.Trim() ?? string.Empty;
			if (custom.Length > 0)
			{
				return new AskAnswer(question.Id, question.Type, JsonValue.Create(custom), null, true);
			}

			var index = combo.SelectedIndex - 1;
			return index >= 0 && index < options.Count
				? new AskAnswer(question.Id, question.Type, JsonValue.Create(options[index].Value), options[index].Label, false)
				: new AskAnswer(question.Id, question.Type, null, null, false);
		});
	}

	private void BuildConfirm(AskQuestion question, StackPanel block)
	{
		var yes = new CheckBox().Content(Label(Strings.AskYes)).IsChecked(true);
		block.Children(yes);
		_answers.Add(() => new AskAnswer(question.Id, question.Type, JsonValue.Create(yes.IsChecked), null, false));
	}

	private void BuildText(AskQuestion question, StackPanel block, bool multiLine)
	{
		var prefill = question.Prefill ?? string.Empty;
		if (multiLine)
		{
			var editor = new MultiLineTextBox().Height(120).Text(prefill).Placeholder(question.Placeholder ?? string.Empty);
			block.Children(editor);
			_answers.Add(() => new AskAnswer(question.Id, question.Type, JsonValue.Create(Value(editor.Text, prefill)), null, false));
			return;
		}

		var text = new TextBox().Text(prefill).Placeholder(question.Placeholder ?? string.Empty);
		block.Children(text);
		_answers.Add(() => new AskAnswer(question.Id, question.Type, JsonValue.Create(Value(text.Text, prefill)), null, false));
	}

	private void Finish(bool cancelled)
	{
		if (!IsOpen || _completion is null)
		{
			return;
		}

		var answers = new List<AskAnswer>(_answers.Count);
		if (!cancelled)
		{
			foreach (var answer in _answers)
			{
				answers.Add(answer());
			}
		}

		var completion = _completion;
		_completion = null;
		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
		}

		if (_themeWindow is { } themeWindow)
		{
			themeWindow.ThemeChanged -= OnWindowThemeChanged;
			_themeWindow = null;
		}

		_window = null;
		_answers = [];
		_returnFocus?.Focus();
		_returnFocus = null;

		// 未作答的问题按取消处理，让工具结果明确说明。
		var incomplete = cancelled || answers.Any(static answer => answer.Value is null);
		completion.TrySetResult(AskQuestions.EncodeResponse(answers, incomplete));
	}

	private static string Describe(AskOption option) =>
		option.Description is { Length: > 0 } description ? $"{option.Label} — {description}" : option.Label;

	private static string Value(string? value, string prefill) =>
		value is { Length: > 0 } text ? text : prefill;

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
}
