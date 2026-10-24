using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 复现「组体展开置顶、滚轮持续向下（内容向上）时平滑滚动失效；向上滚一格后恢复」：
/// 滑动动画进行中，虚拟化 presenter 的锚点/像素舍入校正会直接写视口偏移（extent 稳定），
/// SmoothScroll 的 AutoFollowAtEnd 几何分支曾把它当成「读者接管末尾」而停掉动画——
/// 每个滚轮刻度都重新起动画，连续滚动变成一顿一顿；只有滚轮向上设置 escape 后才绕过该分支。
/// </summary>
[Collection("mewui-controls")]
public sealed class WheelGlideProbe(ITestOutputHelper output)
{
    // AnimationManager.Instance 对外不可见，走与 AnimationFrames 相同的反射通道；这里用真实时间戳，
    // 让 SmoothScroll.Advance 里的 dt 是真实帧间隔。
    private static readonly object Manager = typeof(AnimationManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static readonly MethodInfo UpdateAt = typeof(AnimationManager).GetMethod("UpdateAt", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static ChatEntry Row(string id) => new()
    {
        Role = ChatRole.User, Text = id, EntryId = id, Timestamp = 0,
    };

    private sealed class Fixture
    {
        public required ConversationTimeline Timeline { get; init; }
        public required ScrollViewer Body { get; init; }
        public required SmoothScroll BodySmooth { get; init; }

        public void Pump() => ConversationPerformanceTests.Layout(Timeline.View);

        /// <summary>一个 16ms 帧：动画心跳（Advance→Apply）+ 布局。</summary>
        public void Frame()
        {
            Thread.Sleep(16);
            UpdateAt.Invoke(Manager, [Stopwatch.GetTimestamp()]);
            Pump();
        }

        /// <summary>presenter 式校正：extent 不变的外部微位移（真实应用里是 ±几 DIP 的像素舍入）。</summary>
        public void PresenterCorrection() =>
            Body.SetScrollOffsets(Body.HorizontalOffset, Body.VerticalOffset - 0.5);
    }

    private static ScrollViewer FindBody(ConversationTimeline timeline) =>
        Find<ScrollViewer>(timeline.View).Single(v => v.VerticalScroll != ScrollMode.Disabled && v.Bounds.Height > 0 &&
            !ReferenceEquals(v, timeline.Scroll));

    private static Fixture Build(int historyRows, int steps)
    {
        ConversationScroll? conversation = null;
        var rows = Enumerable.Range(0, historyRows).Select(index => Row($"h{index}")).ToList<ChatEntry>();
        var members = Enumerable.Range(0, steps).Select(index => new ChatEntry
        {
            Role = ChatRole.Tool, EntryId = $"tool/call-{index}", ToolName = "bash", ToolCallId = $"call-{index}",
            ToolArguments = """{"command":"dotnet test"}""",
            Text = $"output {index} 一行输出文本让行高有变化 {index * 7}",
            DurationMs = 12,
        }).ToList();
        // 已完成的会话：非流式，手动展开的组体保持置顶。
        var process = new ChatEntry
        {
            Role = ChatRole.Process, Text = string.Empty, EntryId = "process/done",
            ProcessEntries = [.. members],
        };
        rows.Add(process);
        rows.Add(Row("answer"));
        ExecutionProcessView? processView = null;
        var timeline = new ConversationTimeline(entry =>
        {
            if (entry.Role == ChatRole.Process)
            {
                var view = new ExecutionProcessView(entry, inner => new Border().Height(40).Child(new TextBlock().Text(inner.Text)), null);
                processView = view;
                return view.View;
            }
            return new Border().Height(40).Child(new TextBlock().Text(entry.Text[..Math.Min(40, entry.Text.Length)]));
        });
        var guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
        using (guard)
        {
            timeline.SetEntries(rows);
            ConversationPerformanceTests.Layout(timeline.View);
            // 完成态下运行折叠条展开后组折叠条才出现：循环展开直到不再出现新的折叠条。
            for (var pass = 0; pass < 4; pass++)
            {
                var pending = Find<Expander>(timeline.View)
                    .Where(expander => !expander.IsExpanded &&
                        HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint)
                    .ToArray();
                if (pending.Length == 0) break;
                foreach (var expander in pending) expander.IsExpanded = true;
                ConversationPerformanceTests.Layout(timeline.View);
            }
        }
        var body = FindBody(timeline);
        var fixture = new Fixture { Timeline = timeline, Body = body, BodySmooth = SmoothScroll.For(body) };
        body.SetScrollOffsets(0, 0);
        fixture.Pump();
        Assert.True(fixture.BodySmooth.MaxVerticalOffset > 400, "fixture should have a scrollable virtualized body");
        return fixture;
    }

    [Fact]
    public void LayoutCorrectionMidGlideKeepsTheAnimationAlive()
    {
        var fixture = Build(6, 30);
        try
        {
            var smooth = fixture.BodySmooth;
            smooth.ScrollBy(0, 1);
            smooth.FlushPendingWheel();
            fixture.Frame();
            Assert.True(smooth.IsAnimating, "the wheel glide should be running before the correction");

            // presenter 的锚点/舍入校正：extent 稳定的外部位移。它不是读者接管，不能停掉滑动。
            fixture.PresenterCorrection();
            fixture.Pump();
            Assert.True(smooth.IsAnimating,
                "a layout correction with a stable extent must not kill the reader's glide");
        }
        finally
        {
            UiLifetime.Release(fixture.Timeline.View);
        }
    }

    [Fact]
    public void ContinuousWheelDownFromTopAdvancesEveryFrame()
    {
        var fixture = Build(6, 30);
        try
        {
            var smooth = fixture.BodySmooth;
            var offsets = new List<double>();
            for (var notch = 0; notch < 6; notch++)
            {
                smooth.ScrollBy(0, 1);
                smooth.FlushPendingWheel();
                fixture.Frame();
                // 每帧都可能来一次 presenter 校正（真实应用里 extent 抖动 ±2 DIP 时触发）。
                fixture.PresenterCorrection();
                fixture.Pump();
                offsets.Add(fixture.Body.VerticalOffset);
                Assert.True(smooth.IsAnimating,
                    $"glide died at frame {notch} (offset {offsets[^1]:0.##}); continuous scrolling would stutter");
            }
            // 步长 50 DIP/格：滚轮必须真正推进（修复前的失败点是上面的动画存活断言）。
            Assert.True(offsets[^1] > 40,
                $"expected meaningful progress over 6 notches, got {offsets[^1]:0.##}");
        }
        finally
        {
            UiLifetime.Release(fixture.Timeline.View);
        }
    }
}
