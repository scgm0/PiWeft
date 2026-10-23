using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	private MarkdownView? _themeSmokeMarkdown;
	private string? _themeSmokeSelection;
	private Expander? _themeSmokeTool;
	private UIElement? _themeSmokeToolContent;
	private Border? _themeSmokeUser;

	/// <summary>Real-window fixtures verify message identity and reading state as well as composer state.</summary>
	internal void PrepareThemeSmoke()
	{
		_composer.Text = "theme smoke draft";
		_composer.Select(6, 5);
		for (var depth = 0; depth < 2; depth++)
			foreach (var expander in Find<Expander>(_messages).Where(expander =>
				HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint).ToArray()) expander.IsExpanded = true;
		_themeSmokeMarkdown = Find<MarkdownView>(_messages).LastOrDefault();
		_themeSmokeMarkdown?.SelectAll();
		_themeSmokeSelection = _themeSmokeMarkdown?.SelectedText;
		_themeSmokeTool = Find<Expander>(_messages).FirstOrDefault(expander =>
			HoverHints.Text(expander) == Strings.ToolExpandHint);
		if (_themeSmokeTool is { } tool)
		{
			tool.IsExpanded = true;
			_themeSmokeToolContent = tool.Content as UIElement;
		}
		_themeSmokeUser = Find<Border>(_messages).FirstOrDefault(row => row.StyleName == AppStyles.UserMessage);
	}

	internal bool VerifyThemeSmoke(Color oldPickerColor) =>
		_composer.Text == "theme smoke draft" && _composer.SelectionStart == 6 && _composer.SelectionLength == 5 &&
		_composer.BorderBrush == AppTheme.Border && _thinkingLabel.Foreground != oldPickerColor &&
		(_themeSmokeMarkdown is null || Find<MarkdownView>(_messages).Contains(_themeSmokeMarkdown) &&
			_themeSmokeMarkdown.SelectedText == _themeSmokeSelection) &&
		(_themeSmokeTool is null || _themeSmokeTool.IsExpanded && ReferenceEquals(_themeSmokeToolContent, _themeSmokeTool.Content)) &&
		(_themeSmokeUser is null || _themeSmokeUser.Background == AppTheme.CardBackgroundAlt);

	internal Color ThemeSmokePickerColor => _thinkingLabel.Foreground;
}
