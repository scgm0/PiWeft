using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 思考强度弹层的行为契约：档位名既是预览也是点击入口，拖拽/点档位只改草稿，
/// 关闭才提交一次；没有变化就关闭不产生任何写入。
[Collection("mewui-controls")]
public sealed class ThinkingPickerTests
{
	private static readonly string[] Levels = ["off", "low", "high", "max"];

	private static TextBlock Header(UIElement content) =>
		Find<TextBlock>(content).Single(text => text.FontSize == AppTheme.FontSizeCaption);

	[Fact]
	public void HeaderAndTickRowCoverTheSupportedLevels()
	{
		var content = new ThinkingPicker().BuildForTests(Levels, "low");

		Assert.Equal(Strings.ThinkingLevelName("low"), Header(content).Text);
		var labels = Find<Button>(content).ToArray();
		Assert.Equal(Levels.Length, labels.Length);
		Assert.Equal(
			Levels.Select(Strings.ThinkingLevelName),
			labels.Select(button => Find<TextBlock>(button).Single().Text));
	}

	[Fact]
	public void PickingATickOnlyPreviewsUntilThePopupCloses()
	{
		var picked = new List<string>();
		var picker = new ThinkingPicker();
		var content = picker.BuildForTests(Levels, "low", picked.Add);
		var labels = Find<Button>(content).ToArray();

		ImagePreviewTests.Click(labels[^1]);
		Assert.Empty(picked);
		Assert.Equal(Strings.ThinkingLevelName("max"), Header(content).Text);
		Assert.Equal(FontWeight.SemiBold, Find<TextBlock>(labels[^1]).Single().FontWeight);

		picker.CommitForTests();
		Assert.Equal(["max"], picked);

		// 同一次选择只提交一次：后续关闭通知不能重复落盘。
		picker.CommitForTests();
		Assert.Equal(["max"], picked);
	}

	[Fact]
	public void ClosingWithoutAChangeWritesNothing()
	{
		var picked = new List<string>();
		var picker = new ThinkingPicker();
		picker.BuildForTests(Levels, "high", picked.Add);

		picker.CommitForTests();

		Assert.Empty(picked);
	}

	[Fact]
	public void ContinuousDragSnapsToTheNearestLevel()
	{
		var content = new ThinkingPicker().BuildForTests(Levels, "off");
		var slider = Find<Slider>(content).Single();

		slider.Value = 1.6;

		Assert.Equal(2, slider.Value);
		Assert.Equal(Strings.ThinkingLevelName("high"), Header(content).Text);
	}

	[Fact]
	public void ShowingWithoutABackendDoesNotThrowOrOpen()
	{
		var picker = new ThinkingPicker();
		picker.Show(new Button(), Levels, "low", _ => { });
		Assert.False(picker.IsOpen);
	}

	[Fact]
	public void CurrentLevelOutsideTheCapabilityListFallsBackToTheFirstLevel()
	{
		var content = new ThinkingPicker().BuildForTests(["low", "high"], "xhigh");

		Assert.Equal(Strings.ThinkingLevelName("low"), Header(content).Text);
	}
}
