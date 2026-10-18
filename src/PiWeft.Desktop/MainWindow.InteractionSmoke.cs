using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	/// <summary>Expose fixture coordinates and hint visibility for a native pointer smoke test.</summary>
	private void PrepareHoverSmoke()
	{
		Visit(this);
		void Visit(Element element)
		{
			if (element is FrameworkElement control && HoverHints.Text(control) is { } hint &&
				(hint == Strings.ToggleTheme || hint == Strings.Settings || hint == Strings.SessionStatistics ||
				 hint == Strings.ExecutionExpandHint || ReferenceEquals(control, _rightPanelButton)))
			{
				var name = ReferenceEquals(control, _rightPanelButton) ? "panel" : hint switch
				{
					Strings.ToggleTheme => "theme", Strings.Settings => "settings",
					Strings.SessionStatistics => "statistics", _ => "process",
				};
				var client = new Point(control.Bounds.X + control.Bounds.Width / 2, control.Bounds.Y + control.Bounds.Height / 2);
				if (control.IsVisible && control.Bounds.Width > 0 && new Rect(ClientSize).Contains(client) && InViewport(control, client))
				{
					var point = ClientToScreen(client);
					Console.WriteLine($"hover-point:{name}:{point.X:0},{point.Y:0}");
					if (control is Expander expander && hint == Strings.ExecutionExpandHint)
						expander.ExpandedChanged += expanded => Console.WriteLine($"process-expanded:{expanded}");
				}
				control.MouseEnter += () => Console.WriteLine($"hover-enter:{name}");
				control.MouseLeave += () => Console.WriteLine($"hover-leave:{name}");
			}
			if (element is IVisualTreeHost tree) tree.VisitChildren(child => { Visit(child); return true; });
		}
		static bool InViewport(Element element, Point point)
		{
			for (var parent = element.Parent; parent is not null; parent = parent.Parent)
				if (parent is ScrollViewer scroll && !scroll.Bounds.Contains(point)) return false;
			return true;
		}
		// This diagnostic timer exists only for --ui-smoke hints; normal windows never poll.
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40));
		timer.Tick += () => Console.WriteLine($"hover-state:{HoverHints.IsOpen(this)}");
		Closed += timer.Dispose;
		timer.Start();
	}
}
