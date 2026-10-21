using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>右缘定位轴的刻度规划与当前项解析（PiDeck planRailTicks / outlineRailActive 的契约）。</summary>
[Collection("mewui-controls")]
public sealed class OutlineRailTests
{
	private static IReadOnlyList<int> Rows(int count) => [.. Enumerable.Range(0, count)];

	[Fact]
	public void EmptyRailHasNoTicks()
	{
		var plan = OutlineRailTicks.Build(Rows(0), 400);
		Assert.Empty(plan.Items);
		Assert.Equal(OutlineRailTicks.MaxSize, plan.Size);
	}

	[Fact]
	public void ShortSessionsKeepEveryTickAtTheMaximumSpacing()
	{
		var plan = OutlineRailTicks.Build(Rows(6), 400);
		Assert.Equal(6, plan.Items.Count);
		Assert.Equal(OutlineRailTicks.MaxSize, plan.Size);
	}

	[Fact]
	public void LongSessionsShrinkTheSpacingBeforeSampling()
	{
		// 6 条刻度、60px 高：每格 10px，仍在 4..14 之间 → 全量展示但间距缩小。
		var plan = OutlineRailTicks.Build(Rows(6), 60);
		Assert.Equal(6, plan.Items.Count);
		Assert.Equal(10, plan.Size);
	}

	[Fact]
	public void OverflowingRailsSampleButKeepBothEnds()
	{
		// 40 条、100px 高：每格 2.5px 低于下限 → 抽稀到 100/4=25 条，间距取 4。
		var plan = OutlineRailTicks.Build(Rows(40), 100);
		Assert.Equal(OutlineRailTicks.MinSize, plan.Size);
		Assert.InRange(plan.Items.Count, 2, 25);
		Assert.Equal(0, plan.Items[0]);
		Assert.Equal(39, plan.Items[^1]);
		// 下标严格递增，不重复。
		for (var index = 1; index < plan.Items.Count; index++)
		{
			Assert.True(plan.Items[index] > plan.Items[index - 1]);
		}
	}

	[Fact]
	public void UnmeasuredRailsRenderEverythingOnTheFirstFrame()
	{
		var plan = OutlineRailTicks.Build(Rows(12), 0);
		Assert.Equal(12, plan.Items.Count);
		Assert.Equal(OutlineRailTicks.MaxSize, plan.Size);
	}

	[Fact]
	public void ActiveTickIsTheLastOneAtOrAboveTheAnchor()
	{
		IReadOnlyList<int> positions = [0, 40, 90];
		Assert.Equal(1, OutlineRailTicks.ActiveIndex(positions, 45));
		Assert.Equal(2, OutlineRailTicks.ActiveIndex(positions, 400));
		// 锚点在最上面那条之前 → 退回第一条，而不是没有高亮。
		Assert.Equal(0, OutlineRailTicks.ActiveIndex(positions, -20));
		Assert.Equal(-1, OutlineRailTicks.ActiveIndex([], 10));
	}

	[Fact]
	public void SampledAwayRowsMapToTheNearestRenderedTick()
	{
		IReadOnlyList<int> rendered = [0, 3, 6, 9];
		Assert.Equal(0, OutlineRailTicks.NearestRendered(rendered, 1));
		Assert.Equal(1, OutlineRailTicks.NearestRendered(rendered, 4));
		Assert.Equal(3, OutlineRailTicks.NearestRendered(rendered, 9));
		Assert.Equal(-1, OutlineRailTicks.NearestRendered([], 2));
	}

	[Fact]
	public void OutlineBuildsOneTickPerMessageAndTracksTheActiveRow()
	{
		using var outline = new ConversationOutline();
		IReadOnlyList<ConversationOutline.Item> items =
		[
			new(0, "第一条", "10:00"),
			new(4, "第二条", "10:05"),
			new(9, "第三条", "10:09"),
		];
		outline.SetItems(items);
		Assert.Equal(3, outline.TickCount);
		Assert.Equal([0, 4, 9], outline.RenderedRows);
		Assert.Equal(-1, outline.ActiveRow);

		outline.SetActiveRow(4);
		Assert.Equal(4, outline.ActiveRow);

		// 相同的条目列表不会重建刻度（避免每帧重建控件）。
		var first = outline.RenderedRows;
		outline.SetItems([.. items]);
		Assert.Same(first, outline.RenderedRows);
	}

	[Fact]
	public void PreviewAndClickUseTheSampledMessageRatherThanTheTickOrdinal()
	{
		using var outline = new ConversationOutline();
		outline.SetItems([new(2, "第一条", "10/20 10:00"), new(18, "第二条", "10/20 10:05")]);
		var jumped = -1;
		outline.Jump += row => jumped = row;
		outline.Preview(1);
		Assert.Equal("第二条", outline.PreviewTitle);
		outline.Select(1);
		Assert.Equal(18, jumped);
		Assert.Equal(18, outline.ActiveRow);
		Assert.False(outline.IsPreviewOpen);
	}

	[Fact]
	public void ReadingInsideAnAnswerStillHighlightsThePrecedingQuestion()
	{
		IReadOnlyList<int> userRows = [0, 4, 8];
		Assert.Equal(1, OutlineRailTicks.ActiveIndex(userRows, 7));
	}

	[Fact]
	public void LongRailResizesAndKeepsItsFirstAndLastNavigationTargets()
	{
		using var outline = new ConversationOutline();
		outline.SetItems(Enumerable.Range(0, 1000).Select(i => new ConversationOutline.Item(i * 3, $"问题 {i}", "10:00")).ToArray());
		for (var pass = 0; pass < 4; pass++)
		{
			outline.View.Measure(new Size(400, 160));
			outline.View.Arrange(new Rect(100, 200, 400, 160));
		}
		Assert.InRange(outline.TickCount, 2, 40);
		Assert.Equal(0, outline.RenderedRows[0]);
		Assert.Equal(2997, outline.RenderedRows[^1]);
	}

	[Fact]
	public void PreviewTitleUsesUserTextNotTheExpandedSkillContext()
	{
		var entry = new ChatEntry
		{
			Role = ChatRole.User,
			Text = "<skill name=\"mewui\" location=\"/tmp/SKILL.md\">very long model context</skill>\n\n检查布局",
		};
		Assert.Equal("检查布局", SessionTab.OutlineTitle(entry));
		Assert.Equal(Strings.OutlineImageMessage, SessionTab.OutlineTitle(entry with { Text = "", Images = [new("data", "image/png")] }));
	}

	[Fact]
	public void HoveringATickWaitsForTheSharedHoverDelayBeforePreviewing()
	{
		using var outline = new ConversationOutline();
		outline.SetItems([new(2, "第一条", "10/20 10:00"), new(18, "第二条", "10/20 10:05")]);

		// 刻度高亮跟着指针立刻走，文字预览则和其它悬停提示一样等 350ms。
		outline.Hover(1);
		Assert.True(outline.PreviewPending);
		Assert.Equal("第二条", outline.PreviewTitle);
		Assert.Equal("10/20 10:05", outline.PreviewTime);

		// 光标离开会取消这次等待；键盘聚焦仍然立即预览。
		outline.HidePreview();
		Assert.False(outline.PreviewPending);
		outline.Preview(0);
		Assert.False(outline.PreviewPending);
		Assert.Equal("第一条", outline.PreviewTitle);
	}
}
