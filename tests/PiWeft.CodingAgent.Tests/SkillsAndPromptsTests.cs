using PiWeft.Ai;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class SkillsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-skills-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public SkillsTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string WriteSkill(string skillsRoot, string directory, string content)
	{
		var path = Path.Combine(_root, skillsRoot, directory, "SKILL.md");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	private const string PdfSkill = """
		---
		name: pdf-tools
		description: Extract text and tables from PDF files. Use when reading PDFs.
		---

		# PDF tools

		Run `pdftotext` on the file.
		""";

	[Fact]
	public void Discover_FindsProjectSkills()
	{
		var path = WriteSkill(".pi/skills", "pdf-tools", PdfSkill);

		var skills = CodingAgent.Skills.Skills.Discover(_root, _agentDir);

		var skill = Assert.Single(skills, s => s.Name == "pdf-tools");
		Assert.Equal("Extract text and tables from PDF files. Use when reading PDFs.", skill.Description);
		Assert.Equal(Path.GetFullPath(path), skill.FilePath);
		Assert.Contains("Run `pdftotext`", skill.Body);
		Assert.Contains("pdf-tools", skill.BaseDirectory);
	}

	[Fact]
	public void Discover_ReadsNestedAndAgentSkillsDirectories()
	{
		WriteSkill(".pi/skills", "pdf-tools", PdfSkill);
		WriteSkill(".agents/skills", "agent-layout", """
			---
			name: agent-layout
			description: Layout helpers.
			---
			body
			""");
		WriteSkill("agent/skills", "user-skill", """
			---
			name: user-skill
			description: A user level skill.
			---
			body
			""");

		var skills = CodingAgent.Skills.Skills.Discover(_root, _agentDir);
		var names = skills.Select(static s => s.Name).ToList();

		Assert.Contains("pdf-tools", names);
		Assert.Contains("agent-layout", names);
		Assert.Contains("user-skill", names);
	}

	[Fact]
	public void Discover_SkipsSkillsWithoutADescription()
	{
		WriteSkill(".pi/skills", "no-description", """
			---
			name: no-description
			---
			body
			""");

		var skills = CodingAgent.Skills.Skills.Discover(_root, _agentDir);
		Assert.DoesNotContain(skills, static s => s.Name == "no-description");
	}

	[Fact]
	public void FormatForPrompt_MatchesPiShapeAndHidesExplicitOnlySkills()
	{
		var skills = new List<CodingAgent.Skills.Skill>
		{
			new()
			{
				Name = "pdf-tools",
				Description = "Extract text & tables",
				FilePath = "/skills/pdf-tools/SKILL.md",
				BaseDirectory = "/skills/pdf-tools",
			},
			new()
			{
				Name = "manual-only",
				Description = "hidden",
				FilePath = "/skills/manual-only/SKILL.md",
				BaseDirectory = "/skills/manual-only",
				DisableModelInvocation = true,
			},
		};

		var text = CodingAgent.Skills.Skills.FormatForPrompt(skills);

		Assert.StartsWith("\n\nThe following skills provide specialized instructions for specific tasks.", text);
		Assert.Contains("Use the read tool to load a skill's file when the task matches its description.", text);
		Assert.Contains("resolve it against the skill directory", text);
		Assert.Contains(
			"  <skill>\n    <name>pdf-tools</name>\n    <description>Extract text &amp; tables</description>\n    <location>/skills/pdf-tools/SKILL.md</location>\n  </skill>",
			text);
		Assert.DoesNotContain("manual-only", text);
		Assert.EndsWith("</available_skills>", text);
	}

	[Fact]
	public void FormatForPrompt_WithoutVisibleSkills_IsEmpty()
	{
		Assert.Equal(string.Empty, CodingAgent.Skills.Skills.FormatForPrompt([]));
	}

	[Fact]
	public void ParseFrontmatter_ReadsFieldsAndKeepsTheBody()
	{
		var (fields, body) = CodingAgent.Skills.Skills.ParseFrontmatter(PdfSkill);

		Assert.NotNull(fields);
		Assert.Equal("pdf-tools", fields!["name"]);
		Assert.StartsWith("\n# PDF tools", body);

		var (none, untouched) = CodingAgent.Skills.Skills.ParseFrontmatter("# No frontmatter");
		Assert.Null(none);
		Assert.Equal("# No frontmatter", untouched);
	}

	[Fact]
	public void Discover_ReadsTheRealUserSkillDirectory()
	{
		// The developer's own ~/.pi/agent/skills, when present: discovery must agree with the frontmatter.
		var userSkills = Path.Combine(SessionPaths.GetAgentDir(), "skills");
		if (!Directory.Exists(userSkills))
		{
			return;
		}

		var expected = Directory
			.EnumerateDirectories(userSkills)
			.Select(dir => Path.Combine(dir, "SKILL.md"))
			.Where(File.Exists)
			.Select(CodingAgent.Skills.Skills.ParseFile)
			.Where(static skill => skill is not null)
			.Select(static skill => skill!)
			.ToList();

		Assert.NotEmpty(expected);

		// No agentDir override: discovery must read the real ~/.pi/agent/skills directory.
		var discovered = CodingAgent.Skills.Skills.Discover(_root);
		foreach (var skill in expected)
		{
			Assert.Contains(discovered, candidate =>
				candidate.Name == skill.Name &&
				candidate.Description == skill.Description &&
				candidate.FilePath == skill.FilePath);
		}

		var prompt = CodingAgent.Skills.Skills.FormatForPrompt(discovered);
		Assert.Contains("<available_skills>", prompt);
		Assert.All(expected, skill => Assert.Contains($"<name>{skill.Name}</name>", prompt));
	}

	[Fact]
	public void CodingAgentSession_AddsTheSkillsSectionToThePrompt()
	{
		WriteSkill(".pi/skills", "pdf-tools", PdfSkill);

		var script = new ScriptedStreamFn();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
		});

		Assert.Contains(session.DiscoveredSkills, static s => s.Name == "pdf-tools");

		// 系统提示词在投影里（运行时构造），会话文件里没有。
		var system = Assert.IsType<SystemMessage>(session.ProjectedMessages[0]);

		Assert.True(system.Sections!.ContainsKey("skills"));
		Assert.Contains("<name>pdf-tools</name>", system.Sections["skills"]);
	}
}

public class PromptTemplateTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-prompts-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public PromptTemplateTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void WriteTemplate(string relative, string content)
	{
		var path = Path.Combine(_root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact]
	public void Discover_LoadsProjectAndUserTemplatesWithProjectWinning()
	{
		WriteTemplate(".pi/prompts/review.md", "project review: $ARGUMENTS");
		WriteTemplate("agent/prompts/review.md", "user review");
		WriteTemplate("agent/prompts/other.md", "other");

		var templates = PromptTemplates.Discover(_root, _agentDir);

		Assert.Equal(2, templates.Count);
		Assert.Equal("project review: $ARGUMENTS", templates.Single(static t => t.Name == "review").Content);
		Assert.Contains(templates, static t => t.Name == "other");
	}

	[Fact]
	public void Expand_SubstitutesArguments()
	{
		var templates = new List<PromptTemplate>
		{
			new() { Name = "review", Content = "Review $1 with $2. All: $ARGUMENTS", FilePath = "x" },
		};

		var expanded = PromptTemplates.Expand("/review src main extra", templates);

		Assert.Equal("Review src with main. All: src main extra", expanded);
	}

	[Fact]
	public void Expand_LeavesUnknownCommandsAlone()
	{
		var templates = new List<PromptTemplate> { new() { Name = "review", Content = "x", FilePath = "x" } };

		Assert.Equal("/nope arg", PromptTemplates.Expand("/nope arg", templates));
		Assert.Equal("plain text", PromptTemplates.Expand("plain text", templates));
	}

	[Fact]
	public void SubstituteArgs_SupportsDefaultsAndSlices()
	{
		var args = new[] { "one", "two", "three" };

		Assert.Equal("one", PromptTemplates.SubstituteArgs("$1", args));
		Assert.Equal("fallback", PromptTemplates.SubstituteArgs("${1:-fallback}", []));
		Assert.Equal("one", PromptTemplates.SubstituteArgs("${1:-fallback}", args));
		Assert.Equal("one two three", PromptTemplates.SubstituteArgs("$@", args));
		Assert.Equal("two three", PromptTemplates.SubstituteArgs("${@:2}", args));
		Assert.Equal("two", PromptTemplates.SubstituteArgs("${@:2:1}", args));
		Assert.Equal(string.Empty, PromptTemplates.SubstituteArgs("$9", args));
	}

	[Fact]
	public void ParseArgs_HonorsQuotes()
	{
		Assert.Equal(["a", "b c", "d"], PromptTemplates.ParseArgs("a \"b c\" 'd'"));
		Assert.Empty(PromptTemplates.ParseArgs("   "));
	}
}

public class BranchSummarizationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-branch-" + Guid.NewGuid().ToString("N")[..8]);

	public BranchSummarizationTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static SystemMessage SystemMessage() => new()
	{
		Content = MessageContent.FromText("You are a coding assistant."),
		Timestamp = 0,
	};

	private static UserMessage User(string text, long timestamp) =>
		new() { Content = MessageContent.FromText(text), Timestamp = timestamp };

	private static AssistantMessage Assistant(string text, long timestamp) => new()
	{
		Api = "test",
		Provider = "test",
		Model = "m",
		Content = [new TextContent { Text = text }],
		StopReason = StopReason.Stop,
		Timestamp = timestamp,
	};

	[Fact]
	public void CollectAbandonedEntries_ReturnsThePathLeftBehind()
	{
		var session = SessionManager.InMemory(_root);
		session.AppendMessage(SystemMessage());
		var root = session.AppendMessage(User("root", 1));
		var rootAnswer = session.AppendMessage(Assistant("root answer", 2));
		session.AppendMessage(User("left", 3));
		session.AppendMessage(Assistant("left answer", 4));

		var abandoned = BranchSummarizer.CollectAbandonedEntries(session, session.LeafId!, rootAnswer);

		Assert.Equal(2, abandoned.Count);
		Assert.Equal("left", Assert.IsType<UserMessage>(
			SessionManager.MessagesForEntry(abandoned[0])[0]).Content.Text);
	}

	[Fact]
	public async Task NavigateWithSummary_AppendsABranchSummaryAndMovesTheLeaf()
	{
		var session = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		session.AppendMessage(SystemMessage());
		var root = session.AppendMessage(User("root", 1));
		var rootAnswer = session.AppendMessage(Assistant("root answer", 2));
		var abandonedLeaf = session.AppendMessage(User("left", 3));

		var script = new ScriptedStreamFn();
		script.EnqueueText("## Goal\nleft branch summary");

		var result = await BranchSummarizer.NavigateWithSummaryAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings { ReserveTokens = 4096 },
			rootAnswer);

		Assert.NotNull(result);
		Assert.Equal(1, result!.SummarizedEntries);
		Assert.Equal("## Goal\nleft branch summary", result.Summary);

		var summary = Assert.IsType<BranchSummaryEntry>(session.Entries[^1]);
		Assert.Equal(abandonedLeaf, summary.FromId);
		Assert.Equal(rootAnswer, summary.ParentId);

		// The leaf moved to the summary entry, which hangs off the target.
		Assert.Equal(summary.Id, session.LeafId);

		// The branch summary reaches the model as a prefixed user message.
		var context = session.BuildSessionContext();
		Assert.Contains(
			context.Messages,
			static m => m is UserMessage user &&
				user.Content.Text is not null &&
				user.Content.Text.Contains("summary of a branch", StringComparison.Ordinal) &&
				user.Content.Text.Contains("left branch summary", StringComparison.Ordinal));

		// The abandoned entries are no longer on the active path.
		Assert.DoesNotContain(session.GetBranch(), entry => entry.Id == abandonedLeaf);
	}

	[Fact]
	public async Task NavigateWithSummary_WhenAlreadyAtTheTarget_DoesNothing()
	{
		var session = SessionManager.InMemory(_root);
		session.AppendMessage(SystemMessage());
		var root = session.AppendMessage(User("root", 1));

		var script = new ScriptedStreamFn();
		var result = await BranchSummarizer.NavigateWithSummaryAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings(),
			root);

		Assert.Null(result);
		Assert.Empty(script.Requests);
		Assert.Equal(root, session.LeafId);
	}

	[Fact]
	public async Task CodingAgentSession_NavigatesAndRefreshesItsTranscript()
	{
		var session = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		session.AppendMessage(SystemMessage());
		var root = session.AppendMessage(User("root", 1));
		var rootAnswer = session.AppendMessage(Assistant("root answer", 2));
		session.AppendMessage(User("left", 3));
		session.AppendMessage(Assistant("left answer", 4));

		var script = new ScriptedStreamFn();
		script.EnqueueText("branch summary text");

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = session,
			Settings = new CodingAgentSettings { Compaction = new CompactionSettings { ReserveTokens = 4096 } },
		});

		var result = await coding.NavigateWithSummaryAsync(rootAnswer, "focus on the API design");

		Assert.NotNull(result);
		var prompt = Assert.IsType<UserMessage>(script.Requests[0][1]).Content.Text!;
		Assert.Contains("Additional focus: focus on the API design", prompt);
		Assert.Contains("Create a structured summary of this conversation branch", prompt);

		// The agent transcript follows the session projection.
		Assert.Contains(coding.Messages, static m => m is UserMessage u &&
			u.Content.Text is not null && u.Content.Text.Contains("branch summary text", StringComparison.Ordinal));
	}
}
