using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Security;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 安全档位菜单：顶部说明、盾形图标、当前勾与会话覆盖的「跟随默认」。
[Collection("mewui-controls")]
public sealed class SecurityMenuTests
{
	private static readonly ToolSecurity[] Levels = [ToolSecurity.Off, ToolSecurity.Standard, ToolSecurity.Strict];

	[Fact]
	public void ShieldIconFollowsTheProtectionStrength()
	{
		Assert.Equal("shield-off", SecurityMenu.IconFor(ToolSecurity.OffLevel));
		Assert.Equal("shield-check", SecurityMenu.IconFor(ToolSecurity.StandardLevel));
		Assert.Equal("shield-alert", SecurityMenu.IconFor(ToolSecurity.StrictLevel));
		Assert.Equal("shield", SecurityMenu.IconFor("custom-level"));
	}

	[Fact]
	public void MenuShowsAllLevelsWithOneCheckAndPicksTheClickedLevel()
	{
		var selected = new List<string?>();
		var content = new SecurityMenu().BuildForTests(true, Levels, ToolSecurity.StandardLevel, hasOverride: false, selected.Add);

		Assert.Contains(Find<TextBlock>(content), block => block.Text == Strings.SecurityMenuHint);
		var names = Find<TextBlock>(content).Select(block => block.Text).ToArray();
		Assert.Contains(ToolSecurity.Off.Name, names);
		Assert.Contains(ToolSecurity.Standard.Name, names);
		Assert.Contains(ToolSecurity.Strict.Name, names);
		Assert.DoesNotContain(Strings.SecurityFollowDefault, names);

		var checks = Find<Image>(content)
			.Where(image => Equals(image.Tag, SecurityMenu.SelectedCheckTag))
			.ToArray();
		Assert.Equal(3, checks.Length);
		Assert.Single(checks, check => check.IsVisible);

		var rows = Find<Button>(content).ToArray();
		Assert.Equal(3, rows.Length);
		ImagePreviewTests.Click(rows[2]);
		Assert.Equal([ToolSecurity.StrictLevel], selected);
	}

	[Fact]
	public void SessionOverrideAddsAFollowDefaultRowThatClearsTheOverride()
	{
		var selected = new List<string?>();
		var content = new SecurityMenu().BuildForTests(true, Levels, ToolSecurity.StrictLevel, hasOverride: true, selected.Add);

		Assert.Contains(Find<TextBlock>(content), block => block.Text == Strings.SecurityFollowDefault);
		var rows = Find<Button>(content).ToArray();
		Assert.Equal(4, rows.Length);
		ImagePreviewTests.Click(rows[3]);
		Assert.Single(selected);
		Assert.Null(selected[0]);
	}

	[Fact]
	public void DisabledGateStillOpensWithAReasonAndNoFollowRow()
	{
		var content = new SecurityMenu().BuildForTests(false, Levels, ToolSecurity.OffLevel, hasOverride: true, _ => { });

		Assert.Contains(Find<TextBlock>(content), block => block.Text == Strings.SecurityDisabledHint);
		Assert.DoesNotContain(Find<TextBlock>(content), block => block.Text == Strings.SecurityFollowDefault);
		Assert.All(Find<Button>(content), row => Assert.False(row.IsEnabled));
	}
}
