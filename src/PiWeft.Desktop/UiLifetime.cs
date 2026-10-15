using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Release detached message/editor resources, including caret timers and scrolling clocks.</summary>
internal static class UiLifetime
{
	public static void Release(Element element)
	{
		if (element is Expander expander)
		{
			// Collapsed content is absent from MewUI's visual traversal, but still owns resources.
			if (expander.Header is { } header) Release(header);
			if (expander.Content is { } content) Release(content);
		}
		else if (element is IVisualTreeHost tree)
		{
			var children = new List<Element>();
			tree.VisitChildren(child => { children.Add(child); return true; });
			foreach (var child in children) Release(child);
		}
		SmoothScroll.Release(element);
		if (element is Border { Tag: AssistantMessageView assistant }) assistant.Dispose();
		if (element is FrameworkElement control) control.Dispose();
	}
}
