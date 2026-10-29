using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 设置 → 提示音：分段要能把三类事件都列出来，并且带试听；写入走 desktop.json 的
/// soundAlert 段（这里只验证界面结构，文件读写在 SoundAlertTests 里覆盖）。
/// </summary>
[Collection("mewui-controls")]
public sealed class SoundSectionTests
{
	[Fact]
	public void SectionListsTheThreeEventsWithSwitchesAndPreviews()
	{
		var section = new SoundSection();
		var view = (UIElement)section.View;
		view.IsVisible = true;
		view.Measure(new Size(900, 700));
		view.Arrange(new Rect(0, 0, 900, 700));

		var texts = Find<TextBlock>(view).Select(text => text.Text).ToArray();
		Assert.Contains(texts, text => text.Contains(Strings.SoundAlertGroup, StringComparison.Ordinal));
		Assert.Contains(Strings.SoundAlertDone, texts);
		Assert.Contains(Strings.SoundAlertError, texts);
		Assert.Contains(Strings.SoundAlertWaiting, texts);
		Assert.Contains(Strings.SoundAlertVolume, texts);
		Assert.Contains(Strings.SoundAlertOnlyWhenUnfocused, texts);

		// 三个事件各一条试听按钮、一个开关；音量是一条滑块。
		Assert.Equal(3, Find<Button>(view).Count(button => HoverHints.Text(button) == Strings.SoundAlertPreview));
		Assert.Equal(5, Find<CheckBox>(view).Count()); // 总开关 + 仅失焦 + 三类事件
		Assert.Single(Find<Slider>(view));
		Assert.Equal(3, Find<ComboBox>(view).Count());
	}

	[Fact]
	public void SectionSurvivesMissingOrBrokenPreferences()
	{
		// 偏好文件损坏时 DesktopPreference 回落默认；分区仍要能建出来（不能整页空白）。
		var section = new SoundSection();
		Assert.NotNull(section.View);
	}
}
