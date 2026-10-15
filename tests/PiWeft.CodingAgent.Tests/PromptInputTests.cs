using PiWeft.Ai;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 覆盖 C-01（系统提示词覆盖与项目上下文开关）与 C-03（技能命令/提示词模板统一展开）。
/// </summary>
public class PromptInputTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-promptinput-" + Guid.NewGuid().ToString("N")[..8]);

	public PromptInputTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession Session(
		ScriptedStreamFn script,
		string? customPrompt = null,
		string? append = null,
		bool noContextFiles = false,
		IReadOnlyList<PiWeft.CodingAgent.Skills.Skill>? skills = null,
		IReadOnlyList<PromptTemplate>? templates = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = skills ?? [],
			PromptTemplates = templates ?? [],
			CustomSystemPrompt = customPrompt,
			AppendSystemPrompt = append,
			NoContextFiles = noContextFiles,
		});

	private static SystemMessage Prompt(CodingAgentSession session) =>
		Assert.IsType<SystemMessage>(session.ProjectedMessages[0]);

	[Fact]
	public void CustomSystemPrompt_ReplacesThePreambleAndDropsToolsAndRules()
	{
		using var session = Session(new ScriptedStreamFn(), customPrompt: "You are a terse reviewer.");

		var sections = Prompt(session).Sections!;

		Assert.Equal("You are a terse reviewer.", sections["preamble"]);
		Assert.False(sections.ContainsKey("tools"), "自定义提示词不应再生成 tools 章节");
		Assert.False(sections.ContainsKey("rules"), "自定义提示词不应再生成 rules 章节");
		// cwd 仍然附加，方便模型了解工作目录。
		Assert.Contains("<cwd>", sections["cwd"]);
	}

	[Fact]
	public void AppendSystemPrompt_AddsAnAddendumSection()
	{
		using var session = Session(new ScriptedStreamFn(), append: "Always answer in Chinese.");

		var sections = Prompt(session).Sections!;

		Assert.Equal(SystemPrompt.Preamble, sections["preamble"]);
		Assert.Equal("Always answer in Chinese.", sections["addendum"]);
	}

	[Fact]
	public void NoContextFiles_SuppressesProjectContext()
	{
		File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Project rules\nkeep it secret");

		using var withContext = Session(new ScriptedStreamFn());
		Assert.Contains("keep it secret", Prompt(withContext).Sections!["project_context"]);

		using var withoutContext = Session(new ScriptedStreamFn(), noContextFiles: true);
		Assert.False(Prompt(withoutContext).Sections!.ContainsKey("project_context"));
	}

	[Fact]
	public void ExpandInput_ExpandsSkillCommandsThenTemplates()
	{
		var skill = new PiWeft.CodingAgent.Skills.Skill
		{
			Name = "pdf-tools",
			Description = "Extract text from PDFs",
			FilePath = "/skills/pdf-tools/SKILL.md",
			BaseDirectory = "/skills/pdf-tools",
			Body = "# PDF tools\n\nRun pdftotext.",
		};
		var template = new PromptTemplate { Name = "review", Content = "Review $1 closely", FilePath = "x" };

		using var session = Session(new ScriptedStreamFn(), skills: [skill], templates: [template]);

		var expanded = session.ExpandInput("/skill:pdf-tools focus on tables");

		Assert.Contains("<skill name=\"pdf-tools\" location=\"/skills/pdf-tools/SKILL.md\">", expanded);
		Assert.Contains("References are relative to /skills/pdf-tools.", expanded);
		Assert.Contains("Run pdftotext.", expanded);
		Assert.EndsWith("focus on tables", expanded);
		// 前置元数据不应出现在正文里。
		Assert.DoesNotContain("description:", expanded);

		Assert.Equal("Review src closely", session.ExpandInput("/review src"));
		// 普通文本与未知命令原样返回。
		Assert.Equal("plain text", session.ExpandInput("plain text"));
		Assert.Equal("/skill:missing", session.ExpandInput("/skill:missing"));
	}

	[Fact]
	public async Task PromptAsync_ExpandsSkillAndTemplateBeforeSending()
	{
		var template = new PromptTemplate { Name = "review", Content = "Review $1 closely", FilePath = "x" };
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");

		using var session = Session(script, templates: [template]);
		await session.PromptAsync("/review src/main.cs");

		var user = Assert.IsType<UserMessage>(Assert.Single(script.Requests[0], static m => m is UserMessage));
		Assert.Equal("Review src/main.cs closely", user.Content.Text);
	}

	[Fact]
	public void PrepareInput_KeepsImagesAlongsideTheExpandedText()
	{
		var script = new ScriptedStreamFn();
		using var session = Session(script, customPrompt: "custom");

		var message = session.PrepareInput("look at this", [new ImageContent { Data = "AAAA", MimeType = "image/png" }]);

		var blocks = message.Content.Blocks!;
		Assert.Equal("look at this", Assert.IsType<TextContent>(blocks[0]).Text);
		Assert.Equal("AAAA", Assert.IsType<ImageContent>(blocks[1]).Data);
	}
}
