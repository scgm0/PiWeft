using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 起始页项目菜单与「+」菜单同一套行：图标列、当前目录打勾、添加项目单独分组。
[Collection("mewui-controls")]
public sealed class StartTargetMenuTests
{
	[Fact]
	public void RowsListTargetsCheckTheCurrentOneAndRunThePickedAction()
	{
		using var menu = new StartTargetMenu();
		var picked = new List<string>();
		var added = 0;
		var content = menu.BuildForTests(new StartTargetMenu.Options(
			[new StartTargetMenu.Item("/chat", Strings.SidebarTabChats), new StartTargetMenu.Item("/project", "project")],
			"/project",
			picked.Add,
			() => added += 1));

		var texts = Find<TextBlock>(content).Select(block => block.Text).ToArray();
		Assert.Contains(Strings.SidebarTabChats, texts);
		Assert.Contains("project", texts);
		Assert.Contains(Strings.WelcomeTargetAdd, texts);

		// 只有当前目录那一行打勾；勾在右侧固定列里，切目录时不推着标题走。
		var checks = Find<Image>(content)
			.Where(image => image.Tag is string tag && tag == StartTargetMenu.CheckTag)
			.ToArray();
		Assert.Single(checks, check => check.IsVisible);

		var rows = Find<Button>(content).ToArray();
		Assert.Equal(3, rows.Length);
		ImagePreviewTests.Click(rows[1]);
		Assert.Equal(new[] { "/project" }, picked);

		ImagePreviewTests.Click(rows[2]);
		Assert.Equal(1, added);
	}
}
