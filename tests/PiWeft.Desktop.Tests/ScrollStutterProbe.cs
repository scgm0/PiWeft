using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using PiWeft.CodingAgent.Resources;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 两个用户报告的滚动卡顿场景的计时探针：执行过程展开后的首次滚动、设置页性能分区的置底滚动。
/// </summary>
[Collection("mewui-controls")]
public sealed class ScrollStutterProbe(ITestOutputHelper output)
{
	[Fact]
	public void MeasureExecutionExpandAndFirstScroll()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var members = Enumerable.Range(0, 24).Select(index => new ChatEntry
		{
			Role = ChatRole.Tool,
			EntryId = $"tool/{index}",
			ToolName = "bash",
			ToolCallId = $"call-{index}",
			ToolArguments = """{"command":"dotnet build"}""",
			Text = string.Join('\n', Enumerable.Range(0, 80).Select(line => $"output {index} line {line}: dotnet build 输出的一行，带一点中文和路径 src/PiWeft.Desktop/Program.cs")),
			DurationMs = 120,
		}).ToArray();
		members = [.. members, new ChatEntry
		{
			Role = ChatRole.Assistant, Text = string.Empty, EntryId = "thinking/live/0", Timestamp = 1, IsStreaming = true, IsThinking = true,
			Thinking = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"思考段落 {i}，长一点的文本让测量有分量。")),
		}];
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process/expand", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		static void Pump(UIElement view, double width = 700, double height = 500)
		{
			view.Measure(new Size(width, height));
			view.Arrange(new Rect(0, 0, width, height));
		}
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, guard).View
			: new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		var fold = Find<Expander>(view.View).Single(e => HoverHints.Text(e) == Strings.ExecutionExpandHint);

		var watch = Stopwatch.StartNew();
		fold.IsExpanded = true;
		Pump(view.View);
		// 过程组自己的折叠：展开它才真正建组体（步骤行 + 思考行）。
		var group = Find<Expander>(fold.Content!).Single(e => HoverHints.Text(e) == Strings.ExecutionGroupHint);
		var groupWatch = Stopwatch.StartNew();
		output.WriteLine($"group expanded before? {group.IsExpanded}, hints in run content: {string.Join(",", Find<Expander>(fold.Content!).Select(e => HoverHints.Text(e) ?? "?"))}");
		group.IsExpanded = true;
		Pump(view.View);
		output.WriteLine($"group expanded after? {group.IsExpanded}, content is null? {group.Content is null}, text blocks: {Find<TextBlock>(view.View).Count()}");
		output.WriteLine($"expand run fold: {watch.Elapsed.TotalMilliseconds:0.0} ms; expand group body + layout: {groupWatch.Elapsed.TotalMilliseconds:0.0} ms");

		var body = Find<ScrollViewer>(view.View).Single(v => v.VerticalScroll != ScrollMode.Disabled && v.Bounds.Height > 0);
		var smooth = SmoothScroll.For(body);
		watch.Restart();
		smooth.ScrollBy(0, 3);
		smooth.FlushPendingWheel();
		output.WriteLine($"first wheel flush: {watch.Elapsed.TotalMilliseconds:0.0} ms");
		watch.Restart();
		Pump(view.View);
		output.WriteLine($"first scroll frame layout: {watch.Elapsed.TotalMilliseconds:0.0} ms");

		for (var frame = 1; frame <= 6; frame++)
		{
			smooth.ScrollBy(0, 3);
			smooth.FlushPendingWheel();
			watch.Restart();
			Pump(view.View);
			output.WriteLine($"scroll frame {frame}: {watch.Elapsed.TotalMilliseconds:0.0} ms");
		}
		UiLifetime.Release(view.View);
	}

	[Fact]
	public async Task MeasureDiagnosticsPageScroll()
	{
		var agentDir = Path.Combine(Path.GetTempPath(), "piweft-stutter-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "desktop.json"), "{\"diagnostics\":true}");
		Diagnostics.SetEnabled(true, agentDir);
		try
		{
			// 铺出一段有分量的统计数据，让性能分区接近真实尺寸。
			for (var index = 0; index < 60; index++)
			{
				Diagnostics.Record("probe.slow", 12.5, $"detail {index} with some text to make the line long enough");
			}
			Diagnostics.Count("probe.counter", 42);
			var view = new SettingsView(() => new SettingsSnapshot
			{
				AgentDir = agentDir, Cwd = "/tmp", ModelCount = 0,
				Tools = [], Skills = [], McpServers = [], Resources = ResourceSnapshot.Empty,
			});
			await view.SelectSectionAsync("diagnostics");
			// 预热一遍：把字体加载与文本整形的冷启动成本排除，量的是稳态。
			view.View.Measure(new Size(1000, 700));
			view.View.Arrange(new Rect(0, 0, 1000, 700));
			var watch = Stopwatch.StartNew();
			foreach (var element in Find<UIElement>(view.View)) element.InvalidateArrange();
			view.View.Measure(new Size(1000, 700));
			view.View.Arrange(new Rect(0, 0, 1000, 700));
			output.WriteLine($"settings warm full re-layout: {watch.Elapsed.TotalMilliseconds:0.0} ms");

			var scroll = Find<ScrollViewer>(view.View).First(v => v.VerticalScroll != ScrollMode.Disabled);
			watch.Restart();
			scroll.SetScrollOffsets(0, scroll.MaxVerticalOffset());
			view.View.Measure(new Size(1000, 700));
			view.View.Arrange(new Rect(0, 0, 1000, 700));
			output.WriteLine($"jump to bottom + layout: {watch.Elapsed.TotalMilliseconds:0.0} ms");

			var smooth = SmoothScroll.For(scroll);
			for (var frame = 0; frame < 6; frame++)
			{
				watch.Restart();
				scroll.SetScrollOffsets(0, Math.Max(0, scroll.VerticalOffset - 90));
				view.View.Measure(new Size(1000, 700));
				view.View.Arrange(new Rect(0, 0, 1000, 700));
				output.WriteLine($"scroll-up frame {frame}: {watch.Elapsed.TotalMilliseconds:0.0} ms");
			}
		}
		finally
		{
			Diagnostics.SetEnabled(false);
			Directory.Delete(agentDir, recursive: true);
		}
	}
}

file static class ScrollViewerExtensions
{
	public static double MaxVerticalOffset(this ScrollViewer viewer)
	{
		var max = SmoothScroll.For(viewer).MaxVerticalOffset;
		return max;
	}
}
