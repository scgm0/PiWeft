using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 轮末 chip 的 diff 浮层：头部给文件名、完整路径与增删行数，主体是补丁文本。
[Collection("mewui-controls")]
public sealed class FileDiffPreviewTests
{
	private const string Patch = "--- a/docs/a.md\n+++ b/docs/a.md\n@@ -1,1 +1,2 @@\n-old line\n+new line\n+another line\n";

	[Fact]
	public void LoadsPatchIntoTheDiffBody()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(
			new ChatFileChange("docs/a.md", 2, 2, 1, Patch),
			"/repo/docs/a.md");

		Assert.False(preview.IsOpen);
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "a.md");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "/repo/docs/a.md");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "+2 −1");
		Assert.Contains(Find<MultiLineTextBox>(preview.View),
			view => view.Text.Contains("+another line", StringComparison.Ordinal));
	}

	[Fact]
	public void MissingPatchShowsPlaceholder()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(new ChatFileChange("docs/a.md", 1, 0, 0, string.Empty), "/repo/docs/a.md");

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.GitNoDiff);
	}
}
