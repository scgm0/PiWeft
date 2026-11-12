using PiWeft.Ai;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 覆盖 C-01（系统提示词覆盖与项目上下文开关）与 C-03（技能命令/提示词模板统一展开）。
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

	private const string OnePixelPng =
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

	[Fact]
	public void PrepareInput_KeepsImagesAlongsideTheExpandedText()
	{
		var script = new ScriptedStreamFn();
		using var session = Session(script, customPrompt: "custom");

		var message = session.PrepareInput("look at this", [new ImageContent { Data = OnePixelPng, MimeType = "image/png" }]);

		var blocks = message.Content.Blocks!;
		Assert.Equal("look at this", Assert.IsType<TextContent>(blocks[0]).Text);
		Assert.Equal(OnePixelPng, Assert.IsType<ImageContent>(blocks[1]).Data);
	}

	[Fact]
	public void PrepareInput_LeavesImageProcessingToTheSendPath()
	{
		// PrepareInput 在桌面端是 UI 线程上的同步调用：不在这里解码/缩放。
		var script = new ScriptedStreamFn();
		using var session = Session(script, customPrompt: "custom");

		var message = session.PrepareInput("look at this", [new ImageContent { Data = "AAAA", MimeType = "image/png" }]);

		var blocks = message.Content.Blocks!;
		Assert.Equal("look at this", Assert.IsType<TextContent>(blocks[0]).Text);
		Assert.Equal("AAAA", Assert.IsType<ImageContent>(blocks[1]).Data);
	}

	[Fact]
	public async Task PromptAsync_NormalizesImagesAgainstTheCurrentModelLimits()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		var model = ScriptedSessions.Model() with
		{
			InputLimits = new System.Text.Json.Nodes.JsonObject
			{
				["images"] = new System.Text.Json.Nodes.JsonObject
				{
					["resize"] = new System.Text.Json.Nodes.JsonObject { ["maxWidth"] = 2, ["maxHeight"] = 2 },
				},
			},
		};
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			PromptTemplates = [],
			CustomSystemPrompt = "custom",
		});

		var fourByFour = Sdcb.SimdCodecs.Png.PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
		await session.PromptAsync(session.PrepareInput(
			"look at this",
			[new ImageContent { Data = Convert.ToBase64String(fourByFour), MimeType = "image/png" }]));

		var user = Assert.IsType<UserMessage>(script.Requests[0].Single(static m => m is UserMessage));
		var blocks = user.Content.Blocks!;
		var text = Assert.IsType<TextContent>(blocks[0]).Text;
		Assert.Contains("[Image: original 4x4, displayed at 2x2.", text, StringComparison.Ordinal);
		Assert.Contains(blocks, static block => block is ImageContent);
	}

	[Fact]
	public async Task PromptAsync_TrustsTheBytesOverTheAttachmentMime()
	{
		// 扩展名给了 image/png，内容其实是 BMP：按字节头识别并转码，不能把 BMP 当 PNG 发出去。
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = Session(script, customPrompt: "custom");

		var pixels = new byte[4 * 4 * 4];
		for (var index = 0; index < pixels.Length; index += 4)
		{
			pixels[index + 3] = 255;
		}

		var bmp = Sdcb.SimdCodecs.Bmp.BmpEncoder.Encode(pixels, 4, 4, 4);
		await session.PromptAsync(session.PrepareInput(
			"look",
			[new ImageContent { Data = Convert.ToBase64String(bmp), MimeType = "image/png" }]));

		var user = Assert.IsType<UserMessage>(script.Requests[0].Single(static m => m is UserMessage));
		var blocks = user.Content.Blocks!;
		Assert.Equal("image/png", Assert.IsType<ImageContent>(blocks[1]).MimeType);
		Assert.Contains("[Image converted from image/bmp to image/png.]",
			Assert.IsType<TextContent>(blocks[0]).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PromptAsync_ConvertsUnsupportedFormatsToPngWithAHint()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = Session(script, customPrompt: "custom");

		var pixels = new byte[4 * 4 * 4];
		for (var index = 0; index < pixels.Length; index += 4)
		{
			pixels[index] = 10;
			pixels[index + 1] = 20;
			pixels[index + 2] = 30;
			pixels[index + 3] = 255;
		}

		var bmp = Sdcb.SimdCodecs.Bmp.BmpEncoder.Encode(pixels, 4, 4, 4);
		await session.PromptAsync(session.PrepareInput(
			"look",
			[new ImageContent { Data = Convert.ToBase64String(bmp), MimeType = "image/bmp" }]));

		var user = Assert.IsType<UserMessage>(script.Requests[0].Single(static m => m is UserMessage));
		var blocks = user.Content.Blocks!;
		Assert.Contains("[Image converted from image/bmp to image/png.]", Assert.IsType<TextContent>(blocks[0]).Text);
		Assert.Equal("image/png", Assert.IsType<ImageContent>(blocks[1]).MimeType);
	}

	[Fact]
	public async Task ToolResultImagesAreNormalizedBeforeTheyEnterHistory()
	{
		// MCP/扩展/截图工具产出的图片也要按模型限制处理：否则一张超限图会让之后每个请求都被拒。
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "shot", new System.Text.Json.Nodes.JsonObject());
		script.EnqueueText("done");

		var pixels = new byte[4 * 4 * 4];
		for (var index = 0; index < pixels.Length; index += 4)
		{
			pixels[index + 3] = 255;
		}

		var png = Sdcb.SimdCodecs.Png.PngWriter.Encode(pixels, 4, 4);
		var tool = new PiWeft.Agent.AgentTool
		{
			Name = "shot",
			Label = "shot",
			Description = "take a screenshot",
			Parameters = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
			Execute = _ => Task.FromResult(new PiWeft.Agent.AgentToolResult
			{
				Content = [new ImageContent { Data = Convert.ToBase64String(png), MimeType = "image/png" }],
			}),
		};

		var model = ScriptedSessions.Model() with
		{
			InputLimits = new System.Text.Json.Nodes.JsonObject
			{
				["images"] = new System.Text.Json.Nodes.JsonObject
				{
					["resize"] = new System.Text.Json.Nodes.JsonObject { ["maxWidth"] = 2, ["maxHeight"] = 2 },
				},
			},
		};
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			PromptTemplates = [],
			CustomSystemPrompt = "custom",
			ExtraTools = [tool],
			LoadExtensions = false,
			DiscoverExtensions = false,
		});

		await session.PromptAsync("go");

		var result = Assert.IsType<PiWeft.Ai.ToolResultMessage>(
			script.Requests[1].Single(static m => m is PiWeft.Ai.ToolResultMessage));
		var image = Assert.IsType<ImageContent>(result.Content[0]);
		Assert.Equal("image/png", image.MimeType);
		Assert.NotEqual(Convert.ToBase64String(png), image.Data);
		var hint = Assert.IsType<TextContent>(result.Content[1]).Text;
		Assert.Contains("[Image: original 4x4, displayed at 2x2.", hint, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PromptAsync_LeavesImagesUntouchedWhenAutoResizeIsOff()
	{
		// pi 的 images.autoResize=false：不缩放、不重编码，也不补尺寸括注。
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		var model = ScriptedSessions.Model() with
		{
			InputLimits = new System.Text.Json.Nodes.JsonObject
			{
				["images"] = new System.Text.Json.Nodes.JsonObject
				{
					["resize"] = new System.Text.Json.Nodes.JsonObject { ["maxWidth"] = 2, ["maxHeight"] = 2 },
				},
			},
		};
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			PromptTemplates = [],
			CustomSystemPrompt = "custom",
			Settings = new CodingAgentSettings { Images = new ImagesSettings { AutoResize = false } },
		});

		var fourByFour = Sdcb.SimdCodecs.Png.PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
		var data = Convert.ToBase64String(fourByFour);
		await session.PromptAsync(session.PrepareInput(
			"look at this",
			[new ImageContent { Data = data, MimeType = "image/png" }]));

		var user = Assert.IsType<UserMessage>(script.Requests[0].Single(static m => m is UserMessage));
		var blocks = user.Content.Blocks!;
		Assert.Equal("look at this", Assert.IsType<TextContent>(blocks[0]).Text);
		Assert.Equal(data, Assert.IsType<ImageContent>(blocks[1]).Data);
	}

	[Fact]
	public async Task ToolResultImagesAreLeftAloneWhenAutoResizeIsOff()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "shot", new System.Text.Json.Nodes.JsonObject());
		script.EnqueueText("done");

		var png = Sdcb.SimdCodecs.Png.PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
		var data = Convert.ToBase64String(png);
		var tool = new PiWeft.Agent.AgentTool
		{
			Name = "shot",
			Label = "shot",
			Description = "take a screenshot",
			Parameters = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
			Execute = _ => Task.FromResult(new PiWeft.Agent.AgentToolResult
			{
				Content = [new ImageContent { Data = data, MimeType = "image/png" }],
			}),
		};

		var model = ScriptedSessions.Model() with
		{
			InputLimits = new System.Text.Json.Nodes.JsonObject
			{
				["images"] = new System.Text.Json.Nodes.JsonObject
				{
					["resize"] = new System.Text.Json.Nodes.JsonObject { ["maxWidth"] = 2, ["maxHeight"] = 2 },
				},
			},
		};
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			PromptTemplates = [],
			CustomSystemPrompt = "custom",
			ExtraTools = [tool],
			LoadExtensions = false,
			DiscoverExtensions = false,
			Settings = new CodingAgentSettings { Images = new ImagesSettings { AutoResize = false } },
		});

		await session.PromptAsync("go");

		var result = Assert.IsType<PiWeft.Ai.ToolResultMessage>(
			script.Requests[1].Single(static m => m is PiWeft.Ai.ToolResultMessage));
		var image = Assert.IsType<ImageContent>(Assert.Single(result.Content));
		Assert.Equal(data, image.Data);
	}

	[Fact]
	public async Task BlockImages_StripsHistoryImagesFromTheRequest()
	{
		// 设置打开前就进了历史的图片不走发送路径的归一化；请求投影时仍要换成括注（pi 在 convertToLlm 里做同一件事）。
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			PromptTemplates = [],
			CustomSystemPrompt = "custom",
			Settings = new CodingAgentSettings { Images = new ImagesSettings { BlockImages = true } },
		});

		session.Session.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromBlocks([
				new TextContent { Text = "earlier" },
				new ImageContent { Data = OnePixelPng, MimeType = "image/png" },
			]),
		});

		await session.PromptAsync("next");

		var earlier = Assert.IsType<UserMessage>(script.Requests[0].First(static m => m is UserMessage));
		var blocks = earlier.Content.Blocks!;
		Assert.DoesNotContain(blocks, static block => block is ImageContent);
		Assert.Contains(
			blocks.OfType<TextContent>(),
			static text => text.Text.Contains("(image omitted", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PromptAsync_DropsUndecodableImagesAndSaysSoInTheText()
	{
		// 与 pi 的 _normalizePromptImages 一致：处理失败的那张不进上下文，
		// 只在正文里补一句括注，模型仍能继续回答。
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = Session(script, customPrompt: "custom");

		await session.PromptAsync(session.PrepareInput(
			"look at this",
			[new ImageContent { Data = "AAAA", MimeType = "image/png" }]));

		var user = Assert.IsType<UserMessage>(script.Requests[0].Single(static m => m is UserMessage));
		var text = Assert.IsType<TextContent>(Assert.Single(user.Content.Blocks!)).Text;
		Assert.StartsWith("look at this", text, StringComparison.Ordinal);
		Assert.Contains("[Image omitted", text, StringComparison.Ordinal);
	}
}
