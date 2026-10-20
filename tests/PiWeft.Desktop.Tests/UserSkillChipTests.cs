using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 用户消息 / 草稿里的 <c>/skill:名称</c> 和文件引用一样是行内 chip：
/// 投影成一个占位字符、编辑时整枚原子、悬浮给出完整信息，原文保持 <c>/skill:名称</c>。
/// </summary>
[Collection("mewui-controls")]
public sealed class UserSkillChipTests
{
	[Fact]
	public void SkillInvocationsAreParsedAndOtherSlashTokensAreNot()
	{
		var skills = UserSkillReferences.Parse("用 /skill:mewui 和 /skill:foo-bar_2 试试");
		Assert.Equal(2, skills.Count);
		Assert.Equal("mewui", skills[0].Name);
		Assert.Equal("/skill:mewui", skills[0].Command);
		Assert.Equal(2, skills[0].Start);
		Assert.Equal("foo-bar_2", skills[1].Name);

		// 路径片段、URL、行内代码、围栏代码里的同名文本保持纯文本。
		Assert.Empty(UserSkillReferences.Parse("./skill:mewui"));
		Assert.Empty(UserSkillReferences.Parse("/skill:mewui/sub"));
		Assert.Empty(UserSkillReferences.Parse("看 `/skill:mewui` 这段代码"));
		Assert.Empty(UserSkillReferences.Parse("```\n/skill:mewui\n```"));
		Assert.Empty(UserSkillReferences.Parse("https://example.com/skill:mewui"));
		Assert.Empty(UserSkillReferences.Parse("/skill:"));
	}

	[Fact]
	public void FilesAndSkillsMergeIntoOneOrderedChipList()
	{
		const string text = "看 @\"src/App.cs\" 再 /skill:mewui";
		var chips = UserChips.Parse(text);
		Assert.Equal(2, chips.Count);
		Assert.Equal(UserChipKind.File, chips[0].Kind);
		Assert.Equal("src/App.cs", chips[0].Value);
		Assert.Equal("@App.cs", chips[0].Display);
		Assert.Equal(UserChipKind.Skill, chips[1].Kind);
		Assert.Equal("mewui", chips[1].Value);
		Assert.Equal("mewui", chips[1].Display);
	}

	[Fact]
	public void SkillChipHintPrefersTheSkillFileAndFallsBackToTheCommand()
	{
		var skill = Assert.Single(UserSkillReferences.Parse("跑 /skill:mewui"));
		Assert.Equal("/home/me/.pi/agent/skills/mewui/SKILL.md",
			UserReferenceRichText.HintFor(skill, "/work/proj", name => name == "mewui" ? "/home/me/.pi/agent/skills/mewui/SKILL.md" : null));
		Assert.Equal("/skill:mewui", UserReferenceRichText.HintFor(skill, "/work/proj", null));
		Assert.Equal("/skill:mewui", UserReferenceRichText.HintFor(skill, "/work/proj", _ => null));
	}

	[Fact]
	public void SkillTokenProjectsToOnePlaceholderAndStaysInTheRawDraft()
	{
		using var tab = Tab("hello");
		const string draft = "跑 /skill:mewui 看看 @docs/。";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);

		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		Assert.Equal(draft, composer.Text);
		Assert.Equal(2, tab.ComposerReferenceCount);

		var skill = UserSkillReferences.Parse(draft)[0];
		var line = composer.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(1, line.MapSourceOffsetToProjected(skill.End) - line.MapSourceOffsetToProjected(skill.Start));

		// 光标停在 chip 右侧时，Backspace 整枚删除；原文仍是 /skill:mewui 的形式。
		composer.MoveCaret(skill.End, false);
		Assert.True(UserReferenceRichText.TryHandleKey(composer, new KeyEventArgs(Key.Backspace, 0)));
		Assert.DoesNotContain("/skill:mewui", composer.Text);
		Assert.Equal(1, tab.ComposerReferenceCount);
	}

	[Fact]
	public void SkillChipIsNotAFileAndDoesNotOpenAnything()
	{
		using var tab = Tab("hello");
		const string draft = "跑 /skill:mewui 看看";
		tab.SetExtensionText(draft);
		ConversationPerformanceTests.Layout(tab.View);

		var composer = Find<MultiLineTextBox>(tab.View).Single(control => !control.IsReadOnly);
		var skill = UserSkillReferences.Parse(draft)[0];
		var line = composer.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		var start = line.MapSourceOffsetToProjected(skill.Start);
		var end = line.MapSourceOffsetToProjected(skill.End);
		var bounds = new List<Rect>();
		line.GetRangeBounds(new TextRange(start, end - start), bounds);
		Assert.NotEmpty(bounds);

		var content = composer.TextViewportBounds;
		var origin = new Point(content.X - composer.Bounds.X, content.Y - composer.Bounds.Y);
		var point = new Point(
			origin.X + line.DocumentX + bounds[0].X + bounds[0].Width / 2,
			origin.Y + line.DocumentY + bounds[0].Y + bounds[0].Height / 2);
		// 技能 chip 不是文件引用：点击路径拿不到可打开的文件。
		Assert.Null(UserReferenceRichText.ReferenceAtPoint(composer, point));
	}

	[Fact]
	public void SkillInvocationStaysRawInTheUserBubbleAndProjectsToOneChip()
	{
		using var tab = Tab("跑 /skill:mewui 看看");
		ConversationPerformanceTests.Layout(tab.View);

		var body = Find<MultiLineTextBox>(tab.View).Single(control => control.IsReadOnly && control.Text.StartsWith("跑", StringComparison.Ordinal));
		Assert.Equal("跑 /skill:mewui 看看", body.Text);
		var skill = UserSkillReferences.Parse(body.Text)[0];
		var line = body.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(1, line.MapSourceOffsetToProjected(skill.End) - line.MapSourceOffsetToProjected(skill.Start));
		Assert.NotEmpty(body.Extensions.Projections);
		Assert.NotEmpty(body.Extensions.ElementGenerators);
	}

	[Fact]
	public void SkillBlockChipAndInlineSkillCommandCoexistWithoutDuplication()
	{
		// 展开后的技能调用只剩 <skill> 块 + 参数：顶部技能 chip 一枚，正文里的另一个技能命令仍画成行内 chip。
		const string text = "<skill name=\"mewui\" location=\"/home/test/.pi/agent/skills/mewui/SKILL.md\">\nReferences are relative to /home/test/.pi/agent/skills/mewui.\n\n# MewUI\n</skill>\n\n再跑 /skill:image-gen";
		using var tab = Tab(text);
		ConversationPerformanceTests.Layout(tab.View);

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "mewui");
		var body = Find<MultiLineTextBox>(tab.View).Single(control => control.IsReadOnly);
		Assert.Equal("再跑 /skill:image-gen", body.Text);
		var inline = Assert.Single(UserSkillReferences.Parse(body.Text));
		var line = body.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(1, line.MapSourceOffsetToProjected(inline.End) - line.MapSourceOffsetToProjected(inline.Start));
	}

	private static SessionTab Tab(string text)
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText(text) });
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "ok" }] });
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = FakeStreams.Model(), Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = static (_, _, _) => throw new InvalidOperationException(), Persist = false,
		});
		return new SessionTab(session, [session.Model], "test");
	}
}
