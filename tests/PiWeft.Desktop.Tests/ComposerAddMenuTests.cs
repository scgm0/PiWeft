using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 输入区「+」菜单：附件/技能/提示词三项动作，发丝线后是互斥的模式分组。
/// 原生 Popup 的开关由框架负责，这里只测内容与回调。
[Collection("mewui-controls")]
public sealed class ComposerAddMenuTests
{
	private static ComposerAddMenu.Options Options(
		ComposerMode mode = ComposerMode.Normal,
		List<string>? actions = null,
		List<ComposerMode>? modes = null,
		bool canSelectMode = true) =>
		new(
			mode,
			() => actions?.Add("attach"),
			() => actions?.Add("skill"),
			() => actions?.Add("prompt"),
			mode => modes?.Add(mode),
			canSelectMode);

	[Fact]
	public void MenuMergesAttachSkillPromptAndTheModeSection()
	{
		var actions = new List<string>();
		var content = new ComposerAddMenu().BuildForTests(Options(actions: actions));
		var texts = Find<TextBlock>(content).Select(block => block.Text).ToArray();

		Assert.Contains(Strings.ComposerAddAttach, texts);
		Assert.Contains(Strings.ComposerAddSkill, texts);
		Assert.Contains(Strings.ComposerAddPrompt, texts);
		Assert.Contains(Strings.ComposerAddMode, texts);
		Assert.Contains(Strings.ModeGoal, texts);
		Assert.Contains(Strings.ModePlan, texts);

		var rows = Find<Button>(content).ToArray();
		Assert.Equal(5, rows.Length);
		ImagePreviewTests.Click(rows[0]);
		ImagePreviewTests.Click(rows[1]);
		ImagePreviewTests.Click(rows[2]);
		Assert.Equal(["attach", "skill", "prompt"], actions);
	}

	[Fact]
	public void TheActiveModeIsTickedAndClickingItReportsThatSameMode()
	{
		var modes = new List<ComposerMode>();
		var content = new ComposerAddMenu().BuildForTests(Options(ComposerMode.Plan, modes: modes));
		var checks = Find<Image>(content)
			.Where(image => Equals(image.Tag, ComposerAddMenu.ModeCheckTag))
			.ToArray();

		Assert.Equal(2, checks.Length);
		var ticked = Assert.Single(checks, check => check.IsVisible);
		Assert.NotNull(ticked.Source);

		// 第 4、5 行分别是目标与计划。菜单只报「点了哪一项」，
		// 「再点当前项回普通」由 SessionTab.SelectMode 统一决定（见 ComposerModeTests）。
		var rows = Find<Button>(content).ToArray();
		ImagePreviewTests.Click(rows[4]);
		Assert.Equal([ComposerMode.Plan], modes);
	}

	[Fact]
	public void ChoosingAnUncheckedModeSelectsIt()
	{
		var modes = new List<ComposerMode>();
		var content = new ComposerAddMenu().BuildForTests(Options(modes: modes));
		var rows = Find<Button>(content).ToArray();

		ImagePreviewTests.Click(rows[3]);
		Assert.Equal([ComposerMode.Goal], modes);
	}

	[Fact]
	public void ModeRowsAreDisabledWhileTheSessionIsBusy()
	{
		var content = new ComposerAddMenu().BuildForTests(Options(canSelectMode: false));
		var rows = Find<Button>(content).ToArray();

		// 附件、技能、提示词在流式期间仍可执行；模式切换会改工具集，只能等这一轮收尾。
		Assert.All(rows[..3], row => Assert.True(row.IsEnabled));
		Assert.All(rows[3..], row => Assert.False(row.IsEnabled));
	}
}
