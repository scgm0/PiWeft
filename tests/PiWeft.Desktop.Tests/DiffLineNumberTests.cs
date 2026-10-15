using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>diff 视图的行号解析：hunk 头给出起点，之后的增删与上下文各自推进对应计数。</summary>
public class DiffLineNumberTests
{
	[Fact]
	public void HunkHeaderParsesBothSidesWithoutCounts()
	{
		Assert.Equal((1, 1), DiffView.ParseHunkHeader("@@ -1 +1 @@", 0, 0));
		Assert.Equal((12, 34), DiffView.ParseHunkHeader("@@ -12,7 +34,9 @@", 0, 0));
	}

	[Fact]
	public void HunkHeaderFallsBackWhenMalformed()
	{
		// 认不出来时保留上一次的计数，行号列最多显示得不准，但不会崩。
		Assert.Equal((5, 8), DiffView.ParseHunkHeader("@@ 坏掉的头 @@", 5, 8));
	}

	[Fact]
	public void HunkHeaderIgnoresTrailingContextText()
	{
		Assert.Equal((7, 9), DiffView.ParseHunkHeader("@@ -7,3 +9,4 @@ private void Render()", 0, 0));
	}
}
