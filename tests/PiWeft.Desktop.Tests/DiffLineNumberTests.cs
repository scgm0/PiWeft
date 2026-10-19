using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>diff 视图的行号解析：hunk 头给出起点，之后的增删与上下文各自推进对应计数。</summary>
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
}
