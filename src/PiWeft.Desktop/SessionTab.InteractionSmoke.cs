using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	/// <summary>滚到顶部，让「移动到最新」悬浮按钮进入自动显示状态。</summary>
	internal void PrepareLatestSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(2500));
		timer.Tick += () =>
		{
			timer.Stop();
			_scroll.SetScrollOffsets(_scroll.HorizontalOffset, 0);
			Console.WriteLine("latest-ready");
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	/// <summary>
	/// 滚到顶部后反复切换输入区上方卡片的展开与可见性，制造消息区高度抖动，
	/// 复现悬浮按钮「显示不全」的偶发绘制缺陷（外部脚本趁机连拍截图）。
	/// </summary>
	internal void PrepareLatestChurnSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(180));
		var tick = 0;
		timer.Tick += () =>
		{
			_scroll.SetScrollOffsets(0, 0);
			switch (tick % 4)
			{
				case 0: _todoCard.IsExpanded = false; break;
				case 1: _todoCard.IsExpanded = true; break;
				case 2: _todoCard.Show(!_todoCard.View.IsVisible); break;
				case 3: _todoCard.Show(!_todoCard.View.IsVisible); break;
			}
			Render(true);
			if (tick == 3) Console.WriteLine("churn-ready");
			if (++tick > 400) timer.Stop();
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	/// <summary>展开本轮执行过程，供原生截图检查时间线和工具详情。</summary>
	internal void PrepareProcessSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120));
		var step = 0;
		timer.Tick += () =>
		{
			var run = FindByHint(_messages, Strings.ExecutionExpandHint);
			if (run is null) return;
			if (!run.IsExpanded) run.IsExpanded = true;
			else
			{
				var group = run.Content is { } content ? FindByHint(content, Strings.ExecutionGroupHint) : null;
				if (group is { IsExpanded: false }) group.IsExpanded = true;
				else
				{
					var tool = run.Content is { } expandedContent ? FindByHint(expandedContent, Strings.ToolExpandHint) : null;
					if (tool is { IsExpanded: false }) tool.IsExpanded = true;
					else if (tool is { IsExpanded: true })
					{
						timer.Stop();
						Console.WriteLine("process-ready");
					}
				}
			}
			if (++step > 40) timer.Stop();
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	private static Expander? FindByHint(Element root, string hint)
	{
		if (root is Expander expander && HoverHints.Text(expander) == hint) return expander;
		if (root is not IVisualTreeHost host) return null;
		Expander? found = null;
		host.VisitChildren(child =>
		{
			found ??= FindByHint(child, hint);
			return found is null;
		});
		return found;
	}

	/// <summary>Native interaction diagnostics only; --ui-smoke interactions uses isolated fixtures.</summary>
	internal void PrepareInteractionSmoke()
	{
		View.KeyDown += key =>
		{
			if (key.Key != Key.F8) return;
			key.Handled = true;
			PreviewImage(ChatImages.Source(new ChatImage(
				"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII=", "image/png")));
		};
		Expander? run = null;
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50));
		var previous = new Dictionary<string, string>();
		timer.Tick += () =>
		{
			if (View.FindVisualRoot() is not Window window) return;
			run ??= Find<Expander>(_messages).Where(element => HoverHints.Text(element) == Strings.ExecutionExpandHint &&
				element.Bounds.Y >= _scroll.Bounds.Y && element.Bounds.Y < _scroll.Bounds.Bottom).OrderBy(element => element.Bounds.Y).FirstOrDefault();
			foreach (var (name, hint) in new[] { ("run", Strings.ExecutionExpandHint), ("group", Strings.ExecutionGroupHint), ("tool", Strings.ToolExpandHint) })
			{
				var fold = run is null ? null : Find<Expander>(run).FirstOrDefault(element => HoverHints.Text(element) == hint &&
					element.Bounds.Y >= _scroll.Bounds.Y && element.Bounds.Y < _scroll.Bounds.Bottom);
				if (fold is not null) Report(name, new Point(fold.Bounds.X + 10, fold.Bounds.Y + 10), fold.IsExpanded.ToString());
			}
			var diff = Find<MultiLineTextBox>(_messages).LastOrDefault(text => text.Extensions.Classifiers.Any(classifier => classifier is DiffView.DiffClassifier));
			if (diff is not null)
			{
				Report("diff-start", new Point(diff.Bounds.X + 10, diff.Bounds.Y + diff.DefaultLineHeight * 1.4), diff.SelectionLength.ToString());
				Report("diff-end", new Point(diff.Bounds.X + 65, diff.Bounds.Y + diff.DefaultLineHeight * 2.5), diff.SelectionLength.ToString());
			}
			var link = Find<MarkdownPresenter>(_messages).LastOrDefault(text => text.Markdown.StartsWith("[sample]", StringComparison.Ordinal));
			if (link is not null) Report("link", new Point(link.Bounds.X + 25, link.Bounds.Y + 10), "ready");
			Report("preview", new Point(60, window.ClientSize.Height / 2), _imagePreview.IsOpen.ToString());

			void Report(string name, Point client, string state)
			{
				var point = window.ClientToScreen(client);
				var value = $"{point.X:0},{point.Y:0}:{state}";
				if (previous.GetValueOrDefault(name) == value) return;
				previous[name] = value;
				Console.WriteLine($"interaction:{name}:{value}");
			}
		};
		if (View.FindVisualRoot() is Window owner) owner.Closed += timer.Dispose;
		timer.Start();
	}
}
