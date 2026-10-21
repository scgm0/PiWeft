using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>用户消息不再铺开技能块原文；chip、正文、时间与长文折叠按 PiDeck 的阅读层级展示。</summary>
[Collection("mewui-controls")]
public sealed class UserMessageDisplayTests
{
	private const string SkillBlock = "<skill name=\"mewui\" location=\"/home/test/.pi/agent/skills/mewui/SKILL.md\">\nReferences are relative to /home/test/.pi/agent/skills/mewui.\n\n# MewUI application development\n\nLong skill body.\n</skill>";

	private static SessionTab Tab(string text, long timestamp = 0)
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText(text), Timestamp = timestamp });
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "ok" }] });
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = FakeStreams.Model(), Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = static (_, _, _) => throw new InvalidOperationException(), Persist = false,
		});
		return new SessionTab(session, [session.Model], "test");
	}

	[Fact]
	public void SkillContextBecomesAChipAndTheRawBlockStaysOutOfTheBubble()
	{
		const string body = "1. 修复滚动问题\n2. 参考 pideck 优化用户信息";
		using var tab = Tab(SkillBlock + "\n\n" + body, timestamp: DateTimeOffset.Now.ToUnixTimeMilliseconds());
		ConversationPerformanceTests.Layout(tab.View);

		var chip = Find<TextBlock>(tab.View).Single(block => block.Text == "mewui");
		Assert.Contains(Find<Border>(tab.View), border => Find<TextBlock>(border).Contains(chip));

		var bubbles = Find<MultiLineTextBox>(tab.View).Where(text => text.IsReadOnly).ToArray();
		Assert.Contains(bubbles, text => text.Text == body);
		Assert.DoesNotContain(bubbles, text => text.Text.Contains("<skill", StringComparison.Ordinal));
	}

	[Fact]
	public void FileReferencesProjectToInlinePlaceholdersAndKeepTheRawBody()
	{
		const string text = "请检查 @\"src/App.cs\" 和 @\"/tmp/My Docs/spec.pdf\"。";
		using var tab = Tab(text);
		ConversationPerformanceTests.Layout(tab.View);

		var body = Find<MultiLineTextBox>(tab.View).Single(control => control.IsReadOnly && control.Text.StartsWith("请检查", StringComparison.Ordinal));
		Assert.Equal(text, body.Text);
		Assert.NotEmpty(body.Extensions.Projections);
		Assert.NotEmpty(body.Extensions.ElementGenerators);

		var references = UserFileReferences.Parse(text);
		var line = body.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		var expectedLength = text.Length - references.Sum(reference => reference.End - reference.Start - 1);
		Assert.Equal(expectedLength, line.VisualLines[0].Layout.Lines[0].TextLength);
		foreach (var reference in references)
		{
			Assert.Equal(1, line.MapSourceOffsetToProjected(reference.End) - line.MapSourceOffsetToProjected(reference.Start));
		}
		body.Select(references[0].Start, references[0].End - references[0].Start);
		Assert.Equal("@\"src/App.cs\"", body.SelectedText);
	}

	[Fact]
	public void MessageWithOnlyFileReferencesStillHasAnInlineChip()
	{
		const string text = "@\"report.pdf\"";
		using var tab = Tab(text);
		ConversationPerformanceTests.Layout(tab.View);

		var body = Find<MultiLineTextBox>(tab.View).Single(control => control.IsReadOnly && control.Text == text);
		var reference = Assert.Single(UserFileReferences.Parse(text));
		var line = body.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(1, line.MapSourceOffsetToProjected(reference.End) - line.MapSourceOffsetToProjected(reference.Start));
		Assert.Contains(Find<Border>(tab.View), border => border.StyleName == AppStyles.UserMessage && border.IsVisible);
	}

	[Fact]
	public void ComposerFileReferencesAreInlineAndBackspaceDeletesTheWholeChip()
	{
		using var tab = Tab("hello");
		const string draft = "看 @\"src/App.cs\" 和 @docs/。";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);

		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		Assert.Equal(draft, composer.Text);
		Assert.Equal(2, tab.ComposerReferenceCount);
		Assert.NotEmpty(composer.Extensions.Projections);
		Assert.NotEmpty(composer.Extensions.ElementGenerators);

		var references = UserFileReferences.Parse(draft);
		var line = composer.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(1, line.MapSourceOffsetToProjected(references[0].End) - line.MapSourceOffsetToProjected(references[0].Start));

		// 光标停在 chip 右侧时，Right 跳过整枚，Backspace 删除整枚。
		composer.MoveCaret(references[0].Start, false);
		var right = new KeyEventArgs(Key.Right, 0);
		Assert.True(UserReferenceRichText.TryHandleKey(composer, right));
		Assert.True(right.Handled);
		Assert.Equal(references[0].End, composer.CaretPosition);

		var backspace = new KeyEventArgs(Key.Backspace, 0);
		Assert.True(UserReferenceRichText.TryHandleKey(composer, backspace));
		Assert.True(backspace.Handled);
		Assert.DoesNotContain("@\"src/App.cs\"", composer.Text);
		Assert.Equal(1, tab.ComposerReferenceCount);
	}

	[Fact]
	public void ClickingOnAChipMapsTheCaretToOneOfItsBoundaries()
	{
		using var tab = Tab("hello");
		const string draft = "看 @\"src/App.cs\" 和 @docs/。";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);
		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		var reference = UserFileReferences.Parse(draft)[0];
		var line = composer.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		var start = line.MapSourceOffsetToProjected(reference.Start);
		var end = line.MapSourceOffsetToProjected(reference.End);
		var bounds = new List<Rect>();
		line.GetRangeBounds(new TextRange(start, end - start), bounds);
		Assert.NotEmpty(bounds);

		var hit = line.HitTest(new Point(bounds[0].X + bounds[0].Width / 2, bounds[0].Y + bounds[0].Height / 2));
		var source = line.MapProjectedOffsetToSource(hit.InsertionIndex);
		Assert.True(source == reference.Start || source == reference.End);

		var content = composer.TextViewportBounds;
		var origin = new Point(content.X - composer.Bounds.X, content.Y - composer.Bounds.Y);
		var point = new Point(origin.X + line.DocumentX + bounds[0].X + bounds[0].Width / 2, origin.Y + line.DocumentY + bounds[0].Y + bounds[0].Height / 2);
		Assert.Equal(reference.Path, UserReferenceRichText.ReferenceAtPoint(composer, point)?.Path);
	}

	[Fact]
	public void ChipHitTestUsesTextBoxLocalCoordinatesOnAnyLine()
	{
		var box = MessageText.Create();
		UserReferenceRichText.Attach(box, editable: true);
		box.Text = "第一行没有引用\n第二行看 @\"src/App.cs\" 和 @docs/。";

		// 把文本框放进一个偏离窗口原点的容器：Bounds/TextViewportBounds 是窗口坐标，
		// 命中测试收的却是文本框局部坐标，两者混用就会永远命中不到 chip。
		var host = new Border().Padding(new Thickness(48, 32)).Child(box);
		for (var pass = 0; pass < 8; pass++)
		{
			foreach (var element in Find<UIElement>(host).ToArray()) element.InvalidateArrange();
			host.InvalidateMeasure();
			host.Measure(new Size(720, 480));
			host.Arrange(new Rect(140, 96, 720, 480));
		}
		Assert.True(box.Bounds.X > 140 && box.Bounds.Y > 96);

		var content = box.TextViewportBounds;
		var origin = new Point(content.X - box.Bounds.X, content.Y - box.Bounds.Y);
		var line = box.VisibleTextLines.Single(row => row.LogicalLine.Offset > 0);
		var references = UserFileReferences.Parse(box.Text.Substring(line.LogicalLine.Offset, line.LogicalLine.Length));
		Assert.Equal(2, references.Count);

		var start = line.MapSourceOffsetToProjected(references[0].Start);
		var end = line.MapSourceOffsetToProjected(references[0].End);
		var bounds = new List<Rect>();
		line.GetRangeBounds(new TextRange(start, end - start), bounds);
		Assert.NotEmpty(bounds);

		var chip = new Point(
			origin.X + line.DocumentX + bounds[0].X + bounds[0].Width / 2,
			origin.Y + line.DocumentY + bounds[0].Y + bounds[0].Height / 2);
		Assert.Equal(references[0].Path, UserReferenceRichText.ReferenceAtPoint(box, chip)?.Path);

		// 同一行行首的普通文字不是 chip。
		Assert.Null(UserReferenceRichText.ReferenceAtPoint(box, new Point(origin.X + line.DocumentX + 2, chip.Y)));
	}

	[Fact]
	public void CompositionSuspendsReferenceProjectionAndRestoresItAfterCommit()
	{
		var box = new MultiLineTextBox().Text("看 @\"src/App.cs\" 后继续输入");
		UserReferenceRichText.Attach(box, editable: true);
		Assert.Single(box.Extensions.Projections);
		Assert.Single(box.Extensions.ElementGenerators);

		// 组合输入的装饰范围按源文偏移绘制，投影压缩过路径；组合期间临时撤掉投影，
		// 避免 MewUI 0.22.1 的 GetRangeBounds 在切片/投影文本上越界。
		var client = (ITextCompositionClient)box;
		client.HandleTextCompositionStart(new TextCompositionEventArgs());
		Assert.Empty(box.Extensions.Projections);
		Assert.Empty(box.Extensions.ElementGenerators);
		client.HandleTextCompositionUpdate(new TextCompositionEventArgs("继"));
		Assert.Empty(box.Extensions.Projections);

		client.HandleTextCompositionEnd(new TextCompositionEventArgs("继续"));
		Assert.Single(box.Extensions.Projections);
		Assert.Single(box.Extensions.ElementGenerators);
	}

	[Fact]
	public void ChipHoverHintResolvesTheAbsolutePath()
	{
		Assert.Equal(Path.Combine(Path.GetTempPath(), "src", "App.cs"),
			UserReferenceRichText.FullPath("src/App.cs", Path.GetTempPath()));
		Assert.Equal("/abs/spec.pdf", UserReferenceRichText.FullPath("/abs/spec.pdf", "/work/proj"));
		Assert.Equal("src/App.cs", UserReferenceRichText.FullPath("src/App.cs", null));
	}

	[Fact]
	public void ComposerChipSelectionCopiesTheRawPathAndUndoRestoresIt()
	{
		using var tab = Tab("hello");
		const string draft = "看 @\"src/App.cs\" 和 @docs/。";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);
		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		var reference = UserFileReferences.Parse(draft)[0];

		composer.Select(reference.Start, reference.End - reference.Start);
		Assert.Equal("@\"src/App.cs\"", composer.SelectedText);

		composer.MoveCaret(reference.End, false);
		Assert.True(UserReferenceRichText.TryHandleKey(composer, new KeyEventArgs(Key.Backspace, 0)));
		Assert.DoesNotContain("@\"src/App.cs\"", composer.Text);
		composer.Undo();
		Assert.Contains("@\"src/App.cs\"", composer.Text);
	}

	[Fact]
	public void TypingOverPartOfAChipReplacesTheWholeChip()
	{
		using var tab = Tab("hello");
		const string draft = "看 @\"src/App.cs\" 和 @docs/。";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);
		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		var reference = UserFileReferences.Parse(draft)[0];

		composer.Select(reference.Start + 2, 3);
		var input = new TextInputEventArgs("X");
		Assert.True(UserReferenceRichText.TryHandleTextInput(composer, input));
		Assert.True(input.Handled);
		Assert.DoesNotContain("@\"src/App.cs\"", composer.Text);
		Assert.Contains("X", composer.Text);
	}

	[Fact]
	public void LongUserMessageCollapsesAndExpandsInPlace()
	{
		var lines = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"第 {index} 行"));
		using var tab = Tab(lines);
		ConversationPerformanceTests.Layout(tab.View);

		var body = Find<MultiLineTextBox>(tab.View).Single(text => text.IsReadOnly);
		Assert.Contains("第 8 行", body.Text);
		Assert.DoesNotContain("第 12 行", body.Text);
		var expand = Find<Button>(tab.View).Single(button => button.Content is TextBlock { Text: Strings.MessageExpand });
		ImagePreviewTests.Click(expand);
		Assert.Contains("第 12 行", body.Text);
		var collapse = Find<Button>(tab.View).Single(button => button.Content is TextBlock { Text: Strings.MessageCollapse });
		ImagePreviewTests.Click(collapse);
		Assert.DoesNotContain("第 12 行", body.Text);
	}

	[Fact]
	public void UserTimestampMovesBelowTheBubbleInsteadOfTheRoleHeader()
	{
		using var tab = Tab("hello", timestamp: 1_700_000_000_000);
		ConversationPerformanceTests.Layout(tab.View);
		var time = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).ToLocalTime().ToString("HH:mm");
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == time);
		Assert.DoesNotContain(Find<TextBlock>(tab.View), block => block.Text.StartsWith("你", StringComparison.Ordinal));
	}
}
