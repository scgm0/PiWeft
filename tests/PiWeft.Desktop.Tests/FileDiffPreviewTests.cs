using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 文件差异浮层：头部给文件名、来源、完整路径与增删行数，主体是补丁文本或说明。
/// 轮末 chip 与右侧 Git 面板共用这一个浮层。
[Collection("mewui-controls")]
public sealed class FileDiffPreviewTests
{
	private const string Patch = "--- a/docs/a.md\n+++ b/docs/a.md\n@@ -1,1 +1,2 @@\n-old line\n+new line\n+another line\n";

	private static DiffPreviewContent Content(
		string patch = Patch, string context = "", string? message = null, bool isError = false, bool canOpen = true) =>
		new("a.md", context, "/repo/docs/a.md", 2, 1, patch, message, isError, canOpen);

	[Fact]
	public void LoadsPatchIntoTheDiffBody()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content());

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
		preview.LoadForTests(Content(patch: string.Empty));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.GitNoDiff);
	}

	[Fact]
	public void ContextAndReadFailureComeFromTheContent()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(patch: string.Empty, context: "提交 abc1234",
			message: Strings.GitDiffFailedReason("boom"), isError: true, canOpen: false));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "提交 abc1234");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.GitDiffFailedReason("boom"));
		// 历史来源（提交 / 比较）不给「在编辑器中打开」。
		Assert.False(preview.CanOpenFile);
	}

	[Fact]
	public void GitDiffUsesTheSameOverlay()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(context: "提交 abc1234"));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "提交 abc1234");
		Assert.Contains(Find<MultiLineTextBox>(preview.View),
			view => view.Text.Contains("+new line", StringComparison.Ordinal));
		// 统一 / 并排两种模式都在。
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.DiffUnified);
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.DiffSideBySide);
	}
}
