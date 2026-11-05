using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// 新建会话（0 行）时布局必须一次收敛。面板在 Arrange 里做清理，若空列表也走「清空子元素」
/// 就会每帧作废测量：`Arrange → InvalidateMeasure → Measure → Arrange → …`，桌面进程 100% CPU 卡死。
/// 这里直接盯元素在布局后的脏标记，等价于真实调度器的收敛判据。
[Collection("mewui-controls")]
public sealed class EmptyTimelineLayoutTests(ITestOutputHelper output)
{
	[Fact]
	public void EmptyTimelineSettles()
	{
		using var timeline = new ConversationTimeline(_ => new Border().Height(50));
		timeline.SetEntries([]);
		ConversationPerformanceTests.Layout(timeline.View, 960, 720);
		var content = (Element)timeline.Scroll.Content!;
		output.WriteLine($"after layout: view dirty={timeline.Scroll.IsMeasureDirty} content dirty={content.IsMeasureDirty} " +
			$"total={content.DesiredSize.Height}");
		Assert.False(content.IsMeasureDirty, "empty timeline must not invalidate measure while arranging");
		Assert.False(timeline.Scroll.IsMeasureDirty);
	}

	[Fact]
	public void EmptySessionTabSettles()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "empty");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);
		var content = (Element)tab.TimelineFromTests.Scroll.Content!;
		output.WriteLine($"tab: view dirty={tab.TimelineFromTests.Scroll.IsMeasureDirty} content dirty={content.IsMeasureDirty}");
		Assert.False(content.IsMeasureDirty, "empty session must not invalidate measure while arranging");
	}

	[Fact]
	public void RemovingEveryRowSettlesAfterOnePass()
	{
		using var timeline = new ConversationTimeline(_ => new Border().Height(50));
		timeline.SetEntries(Enumerable.Range(0, 20)
			.Select(index => new ChatEntry { Role = ChatRole.User, Text = $"row {index}", EntryId = $"r{index}" })
			.ToArray());
		ConversationPerformanceTests.Layout(timeline.View, 700, 500);
		timeline.SetEntries([]);
		ConversationPerformanceTests.Layout(timeline.View, 700, 500);
		var content = (Element)timeline.Scroll.Content!;
		output.WriteLine($"after clearing rows: content dirty={content.IsMeasureDirty} total={content.DesiredSize.Height:F0}");
		Assert.False(content.IsMeasureDirty);
		Assert.Equal(0, content.DesiredSize.Height, 0.5);
	}
}
