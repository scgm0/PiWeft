using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-02：文件浏览器可以打开、编辑并保存，也能进入子目录。</summary>
[Collection("mewui-controls")]
public sealed class FileBrowserViewTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-fileview-" + Guid.NewGuid().ToString("N")[..8]);

	public FileBrowserViewTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		File.WriteAllText(Path.Combine(_root, "README.md"), "readme");
		File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App {}");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static void Layout(UIElement view) => ConversationPerformanceTests.Layout(view, 360, 700);

	[Fact]
	public void OpenEditAndSaveRoundTrips()
	{
		var view = new FileBrowserView(() => _root);
		Layout(view.View);

		var row = Find<Button>(view.View).First(button => Find<TextBlock>(button).Any(block => block.Text == "README.md"));
		ImagePreviewTests.Click(row);

		var editor = Find<MultiLineTextBox>(view.View).Single();
		Assert.Equal("readme", editor.Text);

		editor.Text = "changed";
		var save = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.FileBrowserSave);
		ImagePreviewTests.Click(save);

		Assert.Equal("changed", File.ReadAllText(Path.Combine(_root, "README.md")));
	}

	[Fact]
	public void NavigateIntoDirectoryListsItsFiles()
	{
		var view = new FileBrowserView(() => _root);
		Layout(view.View);

		var directory = Find<Button>(view.View).First(button => Find<TextBlock>(button).Any(block => block.Text == "src/"));
		ImagePreviewTests.Click(directory);
		Layout(view.View);

		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == "app.cs");
		// 进入后提供返回上一级的入口。
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.FileBrowserParent);
	}

	[Fact]
	public void LargeDirectoriesOnlyRealizeTheViewportAndCanReachTheLastFile()
	{
		for (var index = 0; index < 400; index++)
		{
			File.WriteAllText(Path.Combine(_root, $"file-{index:D4}.txt"), "x");
		}

		var view = new FileBrowserView(() => _root);
		Layout(view.View);
		var buttons = Find<Button>(view.View).Where(button => button.Tag?.GetType().Name == "RowParts").ToArray();
		Assert.InRange(buttons.Length, 1, 40);
		Assert.DoesNotContain(Find<TextBlock>(view.View), block => block.Text == "file-0399.txt");

		var list = Find<ItemsControl>(view.View).First();
		list.ScrollIntoView(400);
		Layout(view.View);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == "file-0399.txt");
	}
}
