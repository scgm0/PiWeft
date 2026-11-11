using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 「添加工具」下拉：目录分内置/内置扩展两段，行是复选样式，点一次原地切换、不关菜单。
[Collection("mewui-controls")]
public sealed class ToolPickerMenuTests
{
	private static ToolPickerMenu.Options Options(
		Func<string, bool>? isEnabled = null,
		Func<string, bool>? isRemoved = null,
		Action<string>? toggle = null,
		IReadOnlySet<string>? blocked = null) =>
		new(
			ConfigurationTools.Catalog,
			blocked ?? new HashSet<string>(StringComparer.Ordinal),
			isEnabled ?? (_ => true),
			isRemoved ?? (_ => false),
			toggle ?? (_ => { }));

	[Fact]
	public void CatalogIsSplitIntoTwoGroupsAndNamesStayVerbatim()
	{
		var content = new ToolPickerMenu().BuildForTests(Options());
		var texts = Find<TextBlock>(content).Select(block => block.Text).ToArray();

		Assert.Contains(Strings.ConfigToolsGroupBuiltin, texts);
		Assert.Contains(Strings.ConfigToolsGroupExtension, texts);
		// 名字原样画：菜单文本把 `_` 当助记符吃掉，ask_question 曾显示成 askquestion。
		Assert.Contains("ask_question", texts);
		Assert.Contains("tool_search", texts);
		Assert.DoesNotContain("askquestion", texts);
		Assert.Equal(ConfigurationTools.Catalog.Count, Rows(content).Count());
	}

	[Fact]
	public void ToolsTogglesInPlaceWithoutClosingAndRepaintsTheCheck()
	{
		var enabled = new HashSet<string>(StringComparer.Ordinal) { "read" };
		string? toggled = null;
		var content = new ToolPickerMenu().BuildForTests(Options(
			isEnabled: enabled.Contains,
			toggle: name =>
			{
				toggled = name;
				if (!enabled.Remove(name)) enabled.Add(name);
			}));

		var bash = Row(content, "bash");
		Assert.False(Check(bash).IsVisible);
		ImagePreviewTests.Click(bash);
		Assert.Equal("bash", toggled);
		Assert.True(Check(bash).IsVisible);

		ImagePreviewTests.Click(bash);
		Assert.False(Check(bash).IsVisible);
	}

	[Fact]
	public void RowRemovedByThisLayerIsMarked()
	{
		var content = new ToolPickerMenu().BuildForTests(Options(isRemoved: name => name == "bash"));

		Assert.Contains(Find<TextBlock>(content), block => block.Text == "−bash");
	}

	[Fact]
	public void ToolsBlockedByExtensionSwitchesStayListedWithAReason()
	{
		var content = new ToolPickerMenu().BuildForTests(Options(
			blocked: new HashSet<string>(StringComparer.Ordinal) { "codemode" }));

		var codemode = Row(content, "codemode");
		Assert.Equal(Strings.ConfigToolDisabledByExtension, HoverHints.Text(codemode));
		Assert.Equal(ConfigurationTools.Catalog.Count, Rows(content).Count());
	}

	private static IEnumerable<Button> Rows(UIElement content) =>
		Find<Button>(content).Where(button =>
			Find<TextBlock>(button).Any(block => block.Text is { } text && ConfigurationTools.Catalog.Contains(text)));

	private static Button Row(UIElement content, string name) =>
		Rows(content).Single(button => Find<TextBlock>(button).Any(block => block.Text == name));

	private static Image Check(Button row) =>
		Find<Image>(row).Single(image => Equals(image.Tag, ToolPickerMenu.CheckTag));
}
