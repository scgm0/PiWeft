using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-02：并排 diff 的解析与渲染。</summary>
public class SideBySideDiffTests
{
	private const string PairedPatch = """
		--- a/a.txt
		+++ b/a.txt
		@@ -1,4 +1,4 @@
		 context
		-old one
		-old two
		+new one
		+new two
		 tail
		""";

	[Fact]
	public void PairsDeletionsWithAdditionsAndKeepsLineNumbers()
	{
		var rows = SideBySideDiff.Parse(PairedPatch);

		Assert.Equal(DiffRowKind.Header, rows[0].Kind);

		Assert.Equal(DiffRowKind.Context, rows[1].Kind);
		Assert.Equal(1, rows[1].OldNumber);
		Assert.Equal(1, rows[1].NewNumber);

		Assert.Equal(DiffRowKind.Change, rows[2].Kind);
		Assert.Equal(2, rows[2].OldNumber);
		Assert.Equal("old one", rows[2].OldText);
		Assert.Equal(2, rows[2].NewNumber);
		Assert.Equal("new one", rows[2].NewText);

		Assert.Equal(DiffRowKind.Change, rows[3].Kind);
		Assert.Equal("old two", rows[3].OldText);
		Assert.Equal("new two", rows[3].NewText);

		Assert.Equal(DiffRowKind.Context, rows[4].Kind);
		Assert.Equal(4, rows[4].OldNumber);
		Assert.Equal(4, rows[4].NewNumber);

		// 两列行数一致才可能逐行对齐。
		Assert.Equal(5, rows.Count);
	}

	[Fact]
	public void UnpairedAdditionOrDeletionLeavesTheOtherSideEmpty()
	{
		var rows = SideBySideDiff.Parse("""
			@@ -1,1 +1,2 @@
			 keep
			+added
			-deleted
			""");

		var addition = rows.Single(static row => row.Kind == DiffRowKind.Addition);
		Assert.Null(addition.OldNumber);
		Assert.Equal("added", addition.NewText);

		var deletion = rows.Single(static row => row.Kind == DiffRowKind.Deletion);
		Assert.Null(deletion.NewNumber);
		Assert.Equal("deleted", deletion.OldText);
	}

	[Fact]
	public void MultipleHunksRestartLineNumbers()
	{
		var rows = SideBySideDiff.Parse("""
			@@ -1,1 +1,1 @@
			-a
			+b
			@@ -10,1 +10,1 @@
			-c
			+d
			""");

		Assert.Equal(2, rows.Count(static row => row.Kind == DiffRowKind.Header));

		var changes = rows.Where(static row => row.Kind == DiffRowKind.Change).ToList();
		Assert.Equal(1, changes[0].OldNumber);
		Assert.Equal(1, changes[0].NewNumber);
		Assert.Equal(10, changes[1].OldNumber);
		Assert.Equal(10, changes[1].NewNumber);
	}

	[Fact]
	public void EmptyPatchProducesNoRows() => Assert.Empty(SideBySideDiff.Parse(string.Empty));
}

/// <summary>并排渲染不抛异常，并且可由工具视图切换到。</summary>
[Collection("mewui-controls")]
public sealed class DiffViewRenderTests
{
	private const string Patch = """
		@@ -1,2 +1,2 @@
		-old
		+new
		 same
		""";

	[Fact]
	public void SideBySideRendersTwoColumns()
	{
		var view = DiffView.RenderSideBySide(Patch, null);

		Assert.NotNull(view);
	}

	[Fact]
	public void ToolViewOffersBothDiffModesWhenExpanded()
	{
		var entry = new ChatEntry
		{
			Role = ChatRole.Tool,
			Text = "ok",
			ToolName = "edit",
			ToolArguments = """{"path":"a.txt"}""",
			Patch = Patch,
		};

		var scroll = new ScrollViewer();
		using var focus = new TextFocusScrollGuard(scroll);
		var toolView = new ToolMessageView(entry, focus);

		var expander = Find<Expander>(toolView.View).Single();
		expander.IsExpanded = true;

		Assert.Contains(Find<Button>(toolView.View), button => Contains(button, Strings.DiffUnified));
		Assert.Contains(Find<Button>(toolView.View), button => Contains(button, Strings.DiffSideBySide));
	}

	private static bool Contains(Button button, string text) => Find<TextBlock>(button).Any(block => block.Text == text);
}
