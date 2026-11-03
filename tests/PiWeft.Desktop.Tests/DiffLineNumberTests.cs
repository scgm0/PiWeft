using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>diff 视图的行号解析：hunk 头给出起点，之后的增删与上下文各自推进对应计数。</summary>
[Collection("mewui-controls")]
public class DiffLineNumberTests
{
	[Theory]
	[InlineData("@@ -1 +1 @@", 1, 1)]
	[InlineData("@@ -12,7 +34,9 @@", 12, 34)]
	[InlineData("@@ -7,3 +9,4 @@ private void Render()", 7, 9)]
	public void HunkHeaderParsesBothSidesAndIgnoresTrailingContext(string header, int oldStart, int newStart)
	{
		Assert.Equal((oldStart, newStart), DiffView.ParseHunkHeader(header, 0, 0));
	}

	[Fact]
	public void HunkHeaderFallsBackWhenMalformed()
	{
		// 认不出来时保留上一次的计数，行号列最多显示得不准，但不会崩。
		Assert.Equal((5, 8), DiffView.ParseHunkHeader("@@ 坏掉的头 @@", 5, 8));
	}

	[Theory]
	[InlineData("--- a/file.cs\n+++ b/file.cs\n@@ -1 +1 @@\n-old\n+new\n context")] // 结尾无换行
	[InlineData("--- a/file.cs\n+++ b/file.cs\n@@ -1 +1 @@\n-old\n+new\n context\n")] // 结尾有换行
	[InlineData("@@ -1 +1 @@\n-a\n+b\n")]
	[InlineData("@@ -10,0 +20,3 @@\n+a\n+b\n+c\n")]
	[InlineData("")]
	public void GutterHasExactlyOneEntryPerBodyLine(string patch)
	{
		// 行号列与正文是两个独立文档，行数必须一一对应：差一行时滚到底部会错开一格。
		// 边界在「末尾空行」与「没有换行的最后一行」，它们决定最后一条计数归谁。
		var view = DiffView.Render(patch);
		var texts = Find<MultiLineTextBox>(view).ToArray();
		var body = texts.Single(control => control.IsHitTestVisible);
		var gutter = texts.Single(control => !control.IsHitTestVisible);

		Assert.Equal(LineCount(body.Text), LineCount(gutter.Text));
		UiLifetime.Release(view);
	}

	private static int LineCount(string text) => text.Length == 0 ? 1 : text.AsSpan().Count('\n') + 1;
}
