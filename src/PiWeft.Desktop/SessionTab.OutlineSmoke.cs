using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	/// <summary>仅供 --ui-smoke outline 的原生鼠标验收：报告刻度坐标、跳转后的阅读位置与预览状态。</summary>
	internal void PrepareOutlineSmoke()
	{
		var previous = new Dictionary<string, string>();
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60));
		timer.Tick += () =>
		{
			if (View.FindVisualRoot() is not Window window) return;
			var ticks = _outline.Ticks.ToArray();
			for (var index = 0; index < ticks.Length; index++)
			{
				var (row, bounds) = ticks[index];
				if (bounds.Height <= 0) continue;
				var point = window.ClientToScreen(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
				Report($"outline-tick:{index}", $"{row},{point.X:0},{point.Y:0}");
			}
			Report("outline-state", $"{_outline.ActiveRow},{_timeline.RowAtViewportAnchor()},{_outline.IsPreviewOpen},{_conversationScroll.Following},{_scroll.VerticalOffset:0}");
			if (_outline.IsPreviewOpen) Report("outline-preview", _outline.PreviewTitle);
		};
		// 截图检查紧凑过程行：只展开过程与组，不展开每个工具的长输出。
		View.KeyDown += args =>
		{
			if (args.Key != Key.F8) return;
			var run = FindByHint(_messages, Strings.ExecutionExpandHint);
			if (run is null) return;
			run.IsExpanded = true;
			if (run.Content is { } content && FindByHint(content, Strings.ExecutionGroupHint) is { } group)
				group.IsExpanded = true;
			args.Handled = true;
		};
		if (View.FindVisualRoot() is Window owner) owner.Closed += timer.Dispose;
		timer.Start();

		void Report(string name, string value)
		{
			if (previous.GetValueOrDefault(name) == value) return;
			previous[name] = value;
			Console.WriteLine($"{name}:{value}");
		}
	}
}
