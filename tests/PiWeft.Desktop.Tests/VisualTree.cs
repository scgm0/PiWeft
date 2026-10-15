using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop.Tests;

internal static class VisualTree
{
	public static IEnumerable<T> Find<T>(Element root) where T : Element
	{
		var pending = new Stack<Element>();
		pending.Push(root);
		while (pending.TryPop(out var element))
		{
			if (element is T match) yield return match;
			if (element is IVisualTreeHost host)
			{
				var children = new List<Element>();
				host.VisitChildren(child => { children.Add(child); return true; });
				for (var index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
			}
		}
	}
}
