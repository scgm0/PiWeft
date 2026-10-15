using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	/// <summary>Isolated smoke fixtures can exercise the real native middle-button route and PRIMARY owner.</summary>
	internal void PreparePrimarySmoke()
	{
		_composer.Text = "before after";
		var viewer = Find<MarkdownPresenter>(_messages).Last();
		viewer.SelectAll();
		Console.WriteLine("primary-source:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(viewer.SelectedText)));
		var point = _composer.GetCharRectInWindow(7);
		Console.WriteLine($"primary-point:{point.X:0},{point.Y + point.Height / 2:0}");
	}

	internal void ReportConversationSmoke()
	{
		Console.WriteLine($"timeline: viewport={_scroll.ViewportWidth:0}x{_scroll.ViewportHeight:0}, offset={_scroll.VerticalOffset:0}/{SmoothScroll.For(_scroll).MaxVerticalOffset:0}, bounds={_scroll.Bounds}, presenter={_scroll.Content?.Bounds}");
		var last = _chat.Entries().LastOrDefault();
		var visible = last is not { Role: ChatRole.Assistant, Text.Length: > 0 } ||
			Find<MarkdownPresenter>(_messages).Any(markdown => markdown.Markdown == last.Text &&
				markdown.Bounds.Bottom > _scroll.Bounds.Top && markdown.Bounds.Top < _scroll.Bounds.Bottom);
		Console.WriteLine($"timeline latest visible: {visible}");
	}

	internal void ReportPrimarySmoke() => Console.WriteLine("primary-draft:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_composer.Text)));

	private static IEnumerable<T> Find<T>(Element root) where T : Element
	{
		if (root is T match) yield return match;
		if (root is not IVisualTreeHost host) yield break;
		var children = new List<Element>();
		host.VisitChildren(child => { children.Add(child); return true; });
		foreach (var child in children)
			foreach (var found in Find<T>(child)) yield return found;
	}
}
