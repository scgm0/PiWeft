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
	/// 仅供 --ui-smoke latest-theme：先上滚让「移动到最新」按钮浮出，再动态切到浅色主题，
	/// 供外部脚本前后截图核对按钮与正文的配色（长会话 + 运行中换肤才会暴露）。
	/// </summary>
	internal void PrepareLatestThemeSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700));
		var step = 0;
		timer.Tick += () =>
		{
			step++;
			switch (step)
			{
				case 2:
					// 离开底部：按钮浮出，同时切到「非跟随」状态。
					_scroll.SetScrollOffsets(0, Math.Max(0, _scroll.VerticalOffset - 900));
					break;
				case 3:
					if (FindByHint(_messages, Strings.ExecutionExpandHint) is { } run) run.IsExpanded = true;
					break;
				case 4:
					if (FindByHint(_messages, Strings.ExecutionGroupHint) is { } group) group.IsExpanded = true;
					break;
				case 5:
					if (FindByHint(_messages, Strings.ToolExpandHint) is { } tool) tool.IsExpanded = true;
					break;
				case 6:
					// 把展开的工具行对齐到视口上沿，换肤前后都看得到同一块代码表面。
					if (FindByHint(_messages, Strings.ToolExpandHint) is { } expanded)
						_scroll.SetScrollOffsets(0, Math.Max(0,
							_scroll.VerticalOffset + expanded.Bounds.Y - _scroll.Bounds.Y - 60));
					Console.WriteLine("latest-ready");
					break;
				case 8:
					(DesktopPreference.Load() with { Theme = DesktopPreference.LightTheme }).Save();
					if (Application.IsRunning) Application.Current.SetTheme(ThemeVariant.Light);
					break;
				case 10:
					// 换肤后再离开底部一点：确认「移动到最新」重新浮出时是当前主题的颜色。
					_scroll.SetScrollOffsets(0, Math.Max(0, _scroll.VerticalOffset - 300));
					break;
				case 12:
					Console.WriteLine("theme-light");
					timer.Stop();
					break;
			}
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

	/// <summary>
	/// 仅供 --ui-smoke collapse：按用户路径展开过程组与工具调用，报告滚动范围、已实现行与空白行，
	/// 用于定位「展开/收起长内容后正文不渲染，滚动一下才恢复」。
	/// </summary>
	internal void PrepareCollapseSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300));
		var step = 0;
		timer.Tick += () =>
		{
			step++;
			var run = FindByHint(_messages, Strings.ExecutionExpandHint);
			if (run is null)
			{
				// 还没实现出过程行：往上找，直到视口里出现一条（真实会话底部可能没有）。
				step--;
				_scroll.SetScrollOffsets(0, Math.Max(0, _scroll.VerticalOffset - 800));
				return;
			}
			Expander? group = run.Content is { } runContent ? FindByHint(runContent, Strings.ExecutionGroupHint) : null;
			Expander? tool = group?.Content is { } groupContent ? FindByHint(groupContent, Strings.ToolExpandHint) : null;
			switch (step)
			{
				case 1:
					// 先贴底，再往上滚一点：用户是在「向上浏览」时展开的，跟流已关闭。
					SmoothScroll.For(_scroll).JumpToEnd();
					_scroll.SetScrollOffsets(0, Math.Max(0, _scroll.VerticalOffset - 200));
					break;
				case 2:
					run.IsExpanded = true;
					break;
				case 3:
					if (group is not null) { _conversationScroll.PreserveAnchor(group); group.IsExpanded = true; }
					break;
				case 4:
					Report("group-expanded");
					if (tool is not null) { _conversationScroll.PreserveAnchor(tool); tool.IsExpanded = true; }
					break;
				case 6:
					Report("tool-expanded");
					if (tool is not null) { _conversationScroll.PreserveAnchor(tool); tool.IsExpanded = false; }
					break;
				case 8:
					Report("tool-collapsed");
					timer.Stop();
					Console.WriteLine("collapse-ready");
					break;
			}
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();

		void Report(string phase)
		{
			var smooth = SmoothScroll.For(_scroll);
			var hosts = Find<ContentControl>(_messages).ToArray();
			// 只统计「绑定了条目却没有内容」的行；容器池里未使用的空壳不算。
			var blankHosts = hosts.Where(host => host.Tag is ChatEntry && host.Content is null && host.Bounds.Height > 0 &&
				host.Bounds.Bottom > _scroll.Bounds.Top && host.Bounds.Top < _scroll.Bounds.Bottom).ToArray();
			var blank = blankHosts.Length;
			var blankTags = string.Join(",", blankHosts.Select(host =>
				host.Tag is ChatEntry entry ? entry.EntryId : host.Tag?.GetType().Name ?? "null"));
			var rows = hosts
				.Where(row => row.Tag is ChatEntry)
				.Select(row =>
				{
					var entry = (ChatEntry)row.Tag!;
					var inside = row.Bounds.Bottom > _scroll.Bounds.Top && row.Bounds.Top < _scroll.Bounds.Bottom;
					var content = row.Content?.DesiredSize.Height ?? -1;
					return $"{entry.EntryId}@{row.Bounds.Y:0}+{row.Bounds.Height:0}/c{content:0}{(inside ? "*" : string.Empty)}";
				});
			Console.WriteLine($"collapse:{phase}:offset={_scroll.VerticalOffset:0.##}:max={smooth.MaxVerticalOffset:0.##}:" +
				$"viewport={_scroll.ViewportHeight:0.##}:blank={blank}[{blankTags}]:rows={string.Join("|", rows)}");
		}
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
			// 悬浮按钮的状态与位置：外部脚本据此在「可见」的瞬间连拍，核对渲染是否完整。
			Report("latest", new Point(_scrollToLatest.Bounds.X + 18, _scrollToLatest.Bounds.Y + 18),
				_scrollToLatest.Opacity.ToString("0.##"));
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
