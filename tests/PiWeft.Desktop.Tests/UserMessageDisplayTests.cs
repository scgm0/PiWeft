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
		// 投影与原文等长（引用用 U+FFFC + 零宽空格占位），按源文偏移的组合输入/选区因此不会越界。
		Assert.Equal(text.Length, line.VisualLines[0].Layout.Lines[0].TextLength);
		foreach (var reference in references)
		{
			Assert.Equal(reference.End - reference.Start, line.MapSourceOffsetToProjected(reference.End) - line.MapSourceOffsetToProjected(reference.Start));
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
		Assert.Equal(reference.End - reference.Start, line.MapSourceOffsetToProjected(reference.End) - line.MapSourceOffsetToProjected(reference.Start));
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
		Assert.Equal(references[0].End - references[0].Start, line.MapSourceOffsetToProjected(references[0].End) - line.MapSourceOffsetToProjected(references[0].Start));

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
	public void CompositionKeepsTheChipProjectedAndItsSpanStaysInsideTheProjectedText()
	{
		var box = new MultiLineTextBox().Text("看 @\"src/App.cs\" 后继续输入");
		UserReferenceRichText.Attach(box, editable: true);
		ConversationPerformanceTests.Layout(new Border().Child(box), 720, 240);
		Assert.Single(box.Extensions.Projections);
		Assert.Single(box.Extensions.ElementGenerators);

		// 中文 IME 组合期间 chip 必须仍是 chip：不再临时撤掉投影。
		var client = (ITextCompositionClient)box;
		var compositionStart = box.Text.Length;
		box.MoveCaret(compositionStart, false);
		client.HandleTextCompositionStart(new TextCompositionEventArgs());
		Assert.Single(box.Extensions.Projections);
		Assert.Single(box.Extensions.ElementGenerators);
		client.HandleTextCompositionUpdate(new TextCompositionEventArgs("中"));
		Assert.Single(box.Extensions.Projections);

		// MewUI 0.22.1 按源文偏移在投影文本上画组合下划线/span；投影等长时这段范围
		// 必须仍落在文本里，否则 GetRangeBounds 会抛 ArgumentOutOfRange。
		var line = box.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		var rects = new List<Rect>();
		line.GetRangeBounds(new TextRange(compositionStart, 1), rects);
		Assert.NotEmpty(rects);
		Assert.Equal(box.Text.Length, line.LogicalLine.Length);

		client.HandleTextCompositionEnd(new TextCompositionEventArgs("中"));
		Assert.Single(box.Extensions.Projections);
		Assert.Single(box.Extensions.ElementGenerators);
		Assert.EndsWith("中", box.Text, StringComparison.Ordinal);
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

	[Fact]
	public void SwappedComposerHintStaysClickThroughToTheComposer()
	{
		using var tab = Tab("hello");
		ConversationPerformanceTests.Layout(tab.View);
		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);

		// 输入再删除：显示换新被调度；无调度器时直接驱动这一拍。
		composer.Text = "输入一些内容再删除";
		composer.Text = string.Empty;
		tab.SwapComposerHint();
		ConversationPerformanceTests.Layout(tab.View);

		// 换新后的提示仍然可见，但绝不参与命中 —— 否则它会盖住输入框吞掉那一行的点击
		//（窗口里表现为光标变箭头、点了没反应）。断言用与输入路由同一个入口：根元素 HitTest。
		var hint = Assert.Single(Find<TextBlock>(tab.View).Where(block => block.Text == Strings.ComposerPlaceholder));
		Assert.True(hint.IsVisible);
		Assert.False(hint.IsHitTestVisible);
		var point = new Point(hint.Bounds.X + 12, hint.Bounds.Y + 9);
		var hit = ((UIElement)tab.View).HitTest(point);
		Assert.True(ReferenceEquals(hit, composer),
			$"点击占位行应命中输入框，实际命中 {hit?.GetType().Name ?? "null"}");
	}

	[Fact]
	public async Task EditAndResendBringsTheOriginalImagesBackAsAttachments()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		// 1x1 PNG（ComposerAttachmentTests 同款），缩略图测量时需要可解码。
		var image = new ImageContent
		{
			Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII=",
			MimeType = "image/png",
		};
		manager.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromBlocks([new TextContent { Text = "看这张图" }, image]),
		});
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "ok" }] });
		var model = FakeStreams.Model() with { Input = ["text", "image"] };
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "ok"), Persist = false,
		});
		using var tab = new SessionTab(session, [model], "edit");
		ConversationPerformanceTests.Layout(tab.View);

		var pencil = Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.EditAndResend);
		ImagePreviewTests.Click(pencil);
		ConversationPerformanceTests.Layout(tab.View);

		// 原文回到输入框、图片回到附件区，重发才会带上同样的内容块。
		Assert.Equal("看这张图", Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly).Text);
		var attachment = Assert.Single(tab.ComposerAttachments);
		Assert.Equal(image.Data, attachment.Data);
		Assert.Equal(image.MimeType, attachment.MimeType);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.EditImageLabel);

		// 发送时以内容块形式发给模型（fork 到原提问之前）：最后一条用户消息是本次提问。
		await tab.SendAsync("再看一次");
		var message = session.Session.BuildSessionContext().Messages.OfType<UserMessage>().Last();
		Assert.Equal("再看一次", Assert.IsType<TextContent>(message.Content.Blocks![0]).Text);
		Assert.Single(message.Content.Blocks!.OfType<ImageContent>());
	}
}
