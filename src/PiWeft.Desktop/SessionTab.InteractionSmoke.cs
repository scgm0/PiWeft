using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	/// <summary>无窗口测试：立即渲染一帧（DispatcherTimer 在无调度器时不会自动 tick）。</summary>
	internal void RenderForTests() => Render();

	/// <summary>是否打印 scroll-sweep 的高频状态探针（PIWEFT_SWEEP_PROBE=1）。</summary>
	private static bool SweepProbe => Environment.GetEnvironmentVariable("PIWEFT_SWEEP_PROBE") == "1";

	/// <summary>滚到顶部，让「移动到最新」悬浮按钮进入自动显示状态。</summary>
	/// <summary>
	/// 仅供 --ui-smoke scroll-sweep：从会话底部按步长向上滚动，覆盖整条会话，
	/// 每步打印 offset 供外部脚本对齐截图帧。步长小于视口，任何一帧里
	/// 「两块已渲染内容之间出现成段空白」都会被像素检测抓到。
	/// </summary>
	internal void PrepareScrollSweepSmoke()
	{
		PiWeft.Markdown.MarkdownView.PaintProbeEnabled = SweepProbe;
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(900));
		DispatcherTimer? probe = null;
		var step = 0;
		if (Environment.GetEnvironmentVariable("PIWEFT_SWEEP_DOWN") == "1")
		{
			_scroll.SetScrollOffsets(0, 0);
		}
		timer.Tick += () =>
		{
			step++;
			double direction = Environment.GetEnvironmentVariable("PIWEFT_SWEEP_DOWN") == "1" ? 1 : -1;
			double target = direction < 0
				? Math.Max(0, _scroll.VerticalOffset - 520)
				: _scroll.VerticalOffset + 520;
			_scroll.SetScrollOffsets(0, target);
			if (SweepProbe)
			{
				Console.WriteLine($"sweep-step {step} at={System.Diagnostics.Stopwatch.GetTimestamp()} offset={target:F0}");
			}
			if (!SweepProbe)
			{
				return;
			}
			// 跳滚后的高频采样：区分「corrective 布局迟到」与「布局已跑但重绘迟到」。
			var sample = 0;
			probe?.Stop();
			probe = new DispatcherTimer(TimeSpan.FromMilliseconds(16));
			probe.Tick += () =>
			{
				sample++;
				var realized = new System.Text.StringBuilder();
				foreach (var markdown in EnumerateMarkdownViews())
				{
					realized.Append(markdown.RealizedBlockCount).Append('/').Append(markdown.BlockCount).Append(' ');
				}
				Console.WriteLine(
					$"probe s{step}.{sample} offset={_scroll.VerticalOffset:F0} scrollY={_scroll.Bounds.Y:F0} rows={_timeline.VisibleRowCount} md=[{realized}]");
				if (sample % 2 == 0)
				{
					foreach (var markdown in EnumerateMarkdownViews())
					{
						var (first, last, boundsY) = markdown.RealizeStateForTest;
						var slots = new System.Text.StringBuilder();
						for (var index = 0; index < markdown.BlockCount; index++)
						{
							slots.Append(index).Append(':').Append(markdown.GetBlockTopForTest(index).ToString("F0"))
								.Append(markdown.IsBlockAttachedForTest(index) ? '+' : '-').Append(' ');
						}
						Console.WriteLine($"state s{step}.{sample} md boundsY={boundsY:F0} window=[{first}..{last}] total={markdown.TotalHeight:F0} slots={slots}");
						if (sample == 2)
						{
							Console.WriteLine($"blocks md {markdown.DescribeBlocksForTest()}");
						}
					}
				}
				if (sample >= 14)
				{
					probe.Stop();
				}
			};
			probe.Start();
			if (target <= 0 || step >= 48)
			{
				timer.Stop();
				Console.WriteLine("sweep-done");
			}
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	private System.Collections.Generic.IEnumerable<PiWeft.Markdown.MarkdownView> EnumerateMarkdownViews()
	{
		var pending = new System.Collections.Generic.Stack<Element>();
		foreach (var row in _timeline.VisibleRows)
		{
			if (row is Element root) pending.Push(root);
		}
		while (pending.Count > 0)
		{
			var current = pending.Pop();
			if (current is PiWeft.Markdown.MarkdownView markdown) yield return markdown;
			if (current is IVisualTreeHost host)
			{
				host.VisitChildren(child =>
				{
					pending.Push(child);
					return true;
				});
			}
		}
	}

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
			// 本演练专挑会挪动正文区的动作：待办卡展开/收起 + 搜索条 + 底部终端坞。
			// 只有让正文区真的移动/变矮，才能验出「浮层停在旧位置」这类缺陷。
			if (tick % 2 == 1) ToggleSearch();
			if (tick % 6 == 2) ToggleTerminal();
			Render(true);
			if (SweepProbe)
			{
				Console.WriteLine(
					$"churn {tick} btn=({_scrollToLatest.Bounds.X:F0},{_scrollToLatest.Bounds.Y:F0}+{_scrollToLatest.Bounds.Width:F0}x{_scrollToLatest.Bounds.Height:F0}) " +
					$"mainBottom={_mainArea.Bounds.Bottom:F0} hosted={_floatingHosted} op={_scrollToLatestFloat.Opacity:F2}");
			}
			if (tick == 3) Console.WriteLine("churn-ready");
			if (++tick > 400) timer.Stop();
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	/// <summary>
	/// 仅供 --ui-smoke wheel：展开执行过程组体后沿真实滚轮入口持续波浪滚动 ~24 秒，
	/// 供外部脚本采性能样本（dotnet-trace / 诊断 timings）对照长帧治理效果。
	/// </summary>
	internal void PrepareWheelSmoke()
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
					timer.Stop();
					StartWheelPump(group);
				}
			}
			if (++step > 40) timer.Stop();
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();

		void StartWheelPump(Expander? group)
		{
			var body = group?.Content is { } groupContent
				? Find<ScrollViewer>(groupContent).FirstOrDefault(viewer => viewer.VerticalScroll != ScrollMode.Disabled)
				: null;
			var smooth = body is null ? null : SmoothScroll.For(body);
			Console.WriteLine($"wheel-pump: body={(body is not null)}:max={smooth?.MaxVerticalOffset ?? -1:0}:start={body?.VerticalOffset:0}");
			var pump = new DispatcherTimer(TimeSpan.FromMilliseconds(16));
			var tick = 0;
			pump.Tick += () =>
			{
				// 波浪滚动：32 拍向下、8 拍向上，让组体视口持续处于动画与行实现压力下。
				var dir = tick % 40 < 32 ? 1 : -1;
				smooth?.ScrollBy(0, dir);
				if (++tick >= 1500) pump.Stop();
				if (tick % 250 == 0)
					Console.WriteLine($"wheel-pump: tick={tick}:offset={body?.VerticalOffset:0}/{smooth?.MaxVerticalOffset:0}:anim={smooth?.IsAnimating}");
			};
			if (View.FindVisualRoot() is Window pumpWindow) pumpWindow.Closed += pump.Dispose;
			pump.Start();
		}
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

	/// <summary>
	/// 仅供 <c>--ui-smoke stream</c>：按真实流式的节奏持续追加思考与正文（含标题/列表/代码块），
	/// 让逐字打字机、增量 Markdown 与每帧重绘一直跑；每秒打印工作集、托管堆与框架的渲染缓存台账，
	/// 用来定位「对话越久工作集越大」到底是文本位图、几何、图集还是托管堆。
	/// </summary>
	internal void PrepareStreamSmoke()
	{
		var chunks = BuildStreamSmokeCorpus();
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16));
		var tick = 0;
		var cursor = 0;
		const int thinkingTicks = 60 * 45;
		const int totalTicks = 60 * 105;
		timer.Tick += () =>
		{
			var inThinking = tick < thinkingTicks;
			if (cursor >= chunks.Length)
			{
				cursor = 0;
			}
			var delta = chunks[cursor++];
			// 思考先写一段，再切到正文：两条逐字通道都要覆盖。
			if (inThinking)
			{
				_chat.SmokeStream(string.Empty, delta, finish: false);
				if (tick == 2) ExpandThinkingForStreamSmoke();
			}
			else
			{
				_chat.SmokeStream(delta, string.Empty, finish: false);
			}
			if (tick % 60 == 59)
			{
				var resources = RenderResourceMetrics.Snapshot();
				var managed = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
				Console.WriteLine(
					$"stream t={tick / 60}s chars={(inThinking ? "thinking" : "body")} ws={Environment.WorkingSet / (1024 * 1024)}MiB " +
					$"heap={managed}MiB gc={GC.CollectionCount(0)}/{GC.CollectionCount(2)} " +
					$"text={resources.TextCacheBytes / 1024}KiB geom={resources.GeometryCacheBytes / 1024}KiB " +
					$"atlas={resources.AtlasPageCount}/{resources.AtlasPageBytes / 1024}KiB " +
					$"persist={resources.PersistentResourceBytes / 1024}KiB scratch={resources.ScratchPooledBytes / 1024}KiB " +
					$"pending={resources.PendingReleaseBytes / 1024}KiB");
			}
			if (tick == 150)
			{
				Console.WriteLine("stream-ready");
			}
			if (tick >= totalTicks)
			{
				_chat.SmokeStream(string.Empty, string.Empty, finish: true);
				timer.Stop();
				Console.WriteLine("stream-done");
			}
			tick++;
		};
		if (View.FindVisualRoot() is Window window) window.Closed += timer.Dispose;
		timer.Start();
	}

	/// <summary>流式压测语料：切成「块」，按 tick 一块一块喂，接近真实模型的分块节奏。</summary>
	private static string[] BuildStreamSmokeCorpus()
	{
		var builder = new System.Text.StringBuilder();
		for (var i = 0; i < 400; i++)
		{
			switch (i % 5)
			{
				case 0:
					builder.Append($"## 段落 {i}\n\n这是第 {i} 段正文，包含一句普通文本、`inline code` 与一个[链接](https://example.com/{i})，用来让增量 Markdown 每次都要重排尾块。\n\n");
					break;
				case 1:
					builder.Append($"- 列表项 {i}.a，带一点长度让行高变化\n- 列表项 {i}.b，第二行内容\n- 列表项 {i}.c\n\n");
					break;
				case 2:
					builder.Append($"```csharp\n// 代码块 {i}\nvar value = Compute({i});\nConsole.WriteLine(value);\n```\n\n");
					break;
				case 3:
					builder.Append($"> 引用 {i}：这里是一段被引用的话，长度接近真实回复里的段落。\n\n");
					break;
				default:
					builder.Append($"普通段落 {i}：" + string.Join("", Enumerable.Repeat("内容", 12)) + "\n\n");
					break;
			}
		}
		var text = builder.ToString();
		// 每块 24 字：约等于 60fps 下每秒 1.4K 字，接近长回复的流式速度。
		var blocks = new List<string>(text.Length / 24 + 1);
		for (var offset = 0; offset < text.Length; offset += 24)
		{
			blocks.Add(text.Substring(offset, Math.Min(24, text.Length - offset)));
		}
		return [.. blocks];
	}

	/// <summary>展开当前流式回复的思考折叠：逐字打字的 Markdown 正文路径也要进压测。</summary>
	private void ExpandThinkingForStreamSmoke()
	{
		foreach (var expander in Find<Expander>(_messages))
		{
			if (expander.Header is not { } header) continue;
			var hasThinkingLabel = Find<TextBlock>(header).Any(text => text.Text == Strings.ThinkingActive);
			if (!hasThinkingLabel) continue;
			expander.IsExpanded = true;
			return;
		}
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
			var link = Find<MarkdownView>(_messages).LastOrDefault(text => text.Source.StartsWith("[sample]", StringComparison.Ordinal));
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
