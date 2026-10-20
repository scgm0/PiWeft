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
			// 过程组内部的视口：原生测试用它验证「组内滚动不带动会话」。
			var inner = Find<ScrollViewer>(_messages).FirstOrDefault(view => !ReferenceEquals(view, _scroll) && view.IsVisible);
			if (inner is not null && inner.Bounds.Width > 0)
			{
				var point = window.ClientToScreen(new Point(inner.Bounds.X + inner.Bounds.Width / 2, inner.Bounds.Y + inner.Bounds.Height / 2));
				Report("group-body", $"{point.X:0},{point.Y:0},{inner.VerticalOffset:0},{SmoothScroll.For(inner).MaxVerticalOffset:0}");
			}
			var earlier = Find<Button>(_messages).FirstOrDefault(button => Find<TextBlock>(button)
				.Any(text => text.Text.StartsWith("显示更早", StringComparison.Ordinal)));
			if (earlier is not null && earlier.Bounds.Width > 0)
			{
				var point = window.ClientToScreen(new Point(earlier.Bounds.X + earlier.Bounds.Width / 2, earlier.Bounds.Y + earlier.Bounds.Height / 2));
				Report("older-steps", $"{point.X:0},{point.Y:0}");
			}
			if (_outline.IsPreviewOpen) Report("outline-preview", _outline.PreviewTitle);
			// 过程组内部所有可滚动层：原生测试据此定位「内容层」并检查谁在响应滚轮。
			var layer = 0;
			foreach (var view in Find<ScrollViewer>(_messages))
			{
				if (ReferenceEquals(view, _scroll) || !view.IsVisible || view.Bounds.Height <= 0) continue;
				var maximum = SmoothScroll.For(view).MaxVerticalOffset;
				if (maximum <= 0.5) continue;
				var point = window.ClientToScreen(new Point(view.Bounds.X + view.Bounds.Width / 2, view.Bounds.Y + view.Bounds.Height / 2));
				Report($"scrollable:{layer++}", $"{point.X:0},{point.Y:0},{view.VerticalOffset:0},{maximum:0},scroll");
			}
			foreach (var text in Find<MultiLineTextBox>(_messages))
			{
				if (!text.IsVisible || text.Bounds.Width <= 0) continue;
				var extent = text.ExtentHeight - text.TextViewportBounds.Height;
				if (extent <= 0.5) continue;
				var point = window.ClientToScreen(new Point(text.Bounds.X + text.Bounds.Width / 2, text.Bounds.Y + text.Bounds.Height / 2));
				Report($"scrollable:{layer++}", $"{point.X:0},{point.Y:0},{text.ScrollOffset.Y:0},{extent:0},text");
			}
			Report("scrollable-count", layer.ToString());
		};
		// 截图检查紧凑过程行：只展开过程与组，不展开每个工具的长输出。
		View.KeyDown += args =>
		{
			if (args.Key is not (Key.F8 or Key.F9)) return;
			args.Handled = true;
			var run = FindByHint(_messages, Strings.ExecutionExpandHint);
			if (run is null) return;
			if (args.Key == Key.F8)
			{
				run.IsExpanded = true;
				if (run.Content is { } content && FindByHint(content, Strings.ExecutionGroupHint) is { } group)
					group.IsExpanded = true;
				return;
			}
			// F9：展开组内第一个工具步骤，露出「命令 / 结果」内容层。
			if (run.Content is { } body && FindByHint(body, Strings.ToolExpandHint) is { } tool)
				tool.IsExpanded = true;
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
