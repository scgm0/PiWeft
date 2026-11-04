using PiWeft.Markdown;

namespace PiWeft.Desktop.Tests;

internal static class MarkdownViewTestExtensions
{
	public static string Text(this MarkdownView view)
	{
		view.SelectAll();
		return view.SelectedText;
	}
}
