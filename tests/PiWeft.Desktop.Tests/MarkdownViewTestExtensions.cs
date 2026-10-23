using PiWeft.Markdown;

namespace PiWeft.Desktop.Tests;

internal static class MarkdownViewTestExtensions
{
	/// <summary>Plain text of the document model, independent of selection state.</summary>
	public static string Text(this MarkdownView view)
	{
		view.SelectAll();
		return view.SelectedText;
	}
}
