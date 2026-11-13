using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ToolCallDetailsTests
{
	private static ChatEntry Tool(string name, string? arguments, string? patch = null, string text = "done") => new()
	{
		Role = ChatRole.Tool,
		ToolName = name,
		ToolArguments = arguments,
		Patch = patch,
		Text = text,
	};

	private static string Json(Action<JsonObject> build)
	{
		var root = new JsonObject();
		build(root);
		return root.ToJsonString(PiJson.NodeOptions);
	}

	private static string? Field(ToolCallDetails details, string label) =>
		details.Fields.FirstOrDefault(field => field.Label == label).Value;

	[Fact]
	public void EditKeepsPathAndDiffInsteadOfRepeatingOldAndNewText()
	{
		var details = ToolCallDetails.For(Tool(
			"edit",
			"""{"path":"src/App.cs","edits":[{"oldText":"old","newText":"new"}]}""",
			patch: "--- a/src/App.cs\n+++ b/src/App.cs\n@@ -1 +1 @@\n-old\n+new\n"));

		Assert.Equal("src/App.cs", Field(details, Strings.ToolFieldPath));
		Assert.Equal("1 处", Field(details, Strings.ToolFieldEdits));
		Assert.Null(details.BlockText);
		Assert.Null(details.Fallback);
	}

	[Fact]
	public void EditWithoutPatchFallsBackToRawArguments()
	{
		var details = ToolCallDetails.For(Tool(
			"edit",
			"""{"path":"src/App.cs","edits":[{"oldText":"old","newText":"new"}]}"""));

		Assert.Contains("\"oldText\"", details.Fallback, StringComparison.Ordinal);
	}

	[Fact]
	public void WriteShowsPlainContentAndLineCount()
	{
		const string content = "# Note\n\nbody\n";
		var details = ToolCallDetails.For(Tool("write", Json(root =>
		{
			root["path"] = "docs/note.md";
			root["content"] = content;
		})));

		Assert.Equal("docs/note.md", Field(details, Strings.ToolFieldPath));
		Assert.Equal("3 行", Field(details, Strings.ToolFieldLines));
		Assert.Equal(Strings.ToolContentLabel, details.BlockLabel);
		Assert.Equal(content, details.BlockText);
	}

	[Fact]
	public void ShellShowsCommandAndTimeout()
	{
		var details = ToolCallDetails.For(Tool("bash",
			"""{"command":"dotnet build","timeout":120}"""));

		Assert.Equal(Strings.ToolCommandLabel, details.BlockLabel);
		Assert.Equal("dotnet build", details.BlockText);
		Assert.Equal("120 秒", Field(details, Strings.ToolFieldTimeout));
	}

	[Fact]
	public void ReadShowsRequestedRange()
	{
		var details = ToolCallDetails.For(Tool("read", """{"path":"src/App.cs","offset":10,"limit":20}"""));

		Assert.Equal("src/App.cs", Field(details, Strings.ToolFieldPath));
		Assert.Equal("第 10 行起，最多 20 行", Field(details, Strings.ToolFieldRange));
	}

	[Fact]
	public void GrepSurfacesSemanticArguments()
	{
		var details = ToolCallDetails.For(Tool("grep",
			"""{"pattern":"ToolMessageView","path":"src","glob":"*.cs","ignoreCase":true,"context":2,"limit":50}"""));

		Assert.Equal("ToolMessageView", Field(details, Strings.ToolFieldPattern));
		Assert.Equal("src", Field(details, Strings.ToolFieldPath));
		Assert.Equal("*.cs", Field(details, Strings.ToolFieldGlob));
		Assert.Equal(Strings.AskYes, Field(details, Strings.ToolFieldIgnoreCase));
		Assert.Equal("2 行", Field(details, Strings.ToolFieldContext));
		Assert.Equal("50", Field(details, Strings.ToolFieldLimit));
	}

	[Fact]
	public void TodoRendersPlanRowsAndHeaderPreview()
	{
		var arguments = Json(root =>
		{
			root["action"] = "replace";
			root["items"] = new JsonArray(
				new JsonObject { ["text"] = "已完成项", ["status"] = "completed" },
				new JsonObject { ["text"] = "进行项", ["status"] = "in_progress" },
				new JsonObject { ["text"] = "待处理项" });
		});
		var details = ToolCallDetails.For(Tool("todo", arguments));

		Assert.Equal(Strings.ToolFieldAction, details.Fields[0].Label);
		Assert.Equal("重写计划", details.Fields[0].Value);
		Assert.Equal(3, details.Todos.Count);
		Assert.Equal("completed", details.Todos[0].Status);
		Assert.Equal("pending", details.Todos[2].Status);
		Assert.Equal($"{Strings.ToolTodoAction("replace")} 3 项", ToolCallDetails.TodoPreview(arguments));
	}

	[Fact]
	public void UnknownFlatArgumentsBecomeFields()
	{
		var details = ToolCallDetails.For(Tool("mcp_search", """{"query":"piweft","limit":5,"strict":false}"""));

		Assert.Equal("piweft", Field(details, "query"));
		Assert.Equal("5", Field(details, "limit"));
		Assert.Equal("false", Field(details, "strict"));
		Assert.Null(details.Fallback);
	}

	[Fact]
	public void UnknownNestedArgumentsStayIndentedJson()
	{
		var details = ToolCallDetails.For(Tool("mcp_search", """{"query":"piweft","filters":["a","b"]}"""));

		Assert.Empty(details.Fields);
		Assert.Contains("\n", details.Fallback, StringComparison.Ordinal);
		Assert.Contains("\"filters\"", details.Fallback, StringComparison.Ordinal);
	}

	[Fact]
	public void NestedFallbackKeepsChineseReadable()
	{
		var details = ToolCallDetails.For(Tool("mcp_search", """{"query":"工具","filters":["中文"]}"""));

		Assert.Contains("工具", details.Fallback, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u", details.Fallback, StringComparison.Ordinal);
	}

	[Fact]
	public void ResultUsedAsArgumentsFallbackIsNotShownTwice()
	{
		const string output = "plain tool output";
		var details = ToolCallDetails.For(new ChatEntry
		{
			Role = ChatRole.Tool,
			ToolName = "read",
			ToolArguments = output,
			Text = output,
		});

		Assert.Empty(details.Fields);
		Assert.Null(details.Fallback);
	}

	[Fact]
	public void InvalidArgumentsFallBackToTheRawText()
	{
		var details = ToolCallDetails.For(Tool("bash", "not json"));

		Assert.Equal("not json", details.Fallback);
	}

	private static ChatEntry Answered(ChatEntry entry, params (string Id, string Value)[] answers) => entry with
	{
		ToolDetails = new JsonObject
		{
			["cancelled"] = false,
			["answers"] = new JsonArray([.. answers.Select(static answer => (JsonNode?)new JsonObject
			{
				["id"] = answer.Id,
				["type"] = "select",
				["value"] = answer.Value,
			})]),
		},
	};

	[Fact]
	public void AskQuestionPairsAnswersWithQuestions()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(
			new JsonObject { ["question"] = "第一题？" },
			new JsonObject { ["question"] = "第二题？" }));
		var details = ToolCallDetails.For(Answered(
			Tool("ask_question", arguments, text: "q1: diff\nq2: one-row"),
			("q1", "diff"), ("q2", "one-row")));

		Assert.Equal(2, details.Exchanges.Count);
		Assert.Equal("第一题？", details.Exchanges[0].Question);
		Assert.Equal("diff", details.Exchanges[0].Answer);
		Assert.Equal("第二题？", details.Exchanges[1].Question);
		Assert.Equal("one-row", details.Exchanges[1].Answer);
		Assert.Null(details.BlockText);
	}

	[Fact]
	public void AskQuestionWithoutAnswersKeepsQuestionsListed()
	{
		var details = ToolCallDetails.For(Tool("ask_question", Json(root =>
			root["questions"] = new JsonArray(new JsonObject { ["question"] = "第一题？" }))));

		var exchange = Assert.Single(details.Exchanges);
		Assert.Equal("第一题？", exchange.Question);
		Assert.Null(exchange.Answer);
		Assert.Null(details.BlockText);
	}

	[Fact]
	public void AskQuestionCustomAndMultiLineAnswersKeepTheirShape()
	{
		var entry = Tool("ask_question", Json(root =>
			root["questions"] = new JsonArray(new JsonObject { ["question"] = "写点什么？" })),
			text: "q1: (自行输入) 第一行\n第二行");
		var details = ToolCallDetails.For(entry with
		{
			ToolDetails = new JsonObject
			{
				["answers"] = new JsonArray(new JsonObject
				{
					["id"] = "q1", ["type"] = "editor", ["value"] = "第一行\n第二行", ["wasCustom"] = true,
				}),
			},
		});

		Assert.Equal("(自行输入) 第一行\n第二行", Assert.Single(details.Exchanges).Answer);
	}

	[Fact]
	public void AskQuestionPartialAnswersOnlyPairTheAnsweredQuestion()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(
			new JsonObject { ["question"] = "第一题？" },
			new JsonObject { ["question"] = "第二题？" }));
		var details = ToolCallDetails.For(Answered(Tool("ask_question", arguments, text: "q1: diff"), ("q1", "diff")));

		// 关闭卡片时只有部分题有答案：已答的照常显示，未答的留空。
		Assert.Equal("diff", details.Exchanges[0].Answer);
		Assert.Null(details.Exchanges[1].Answer);
	}

	[Fact]
	public void AskQuestionShowsTheOptionLabelInsteadOfItsValue()
	{
		var entry = Tool("ask_question", Json(root =>
			root["questions"] = new JsonArray(new JsonObject { ["question"] = "点击文件 chip 时做什么？" })),
			text: "q1: diff") with
		{
			ToolDetails = new JsonObject
			{
				["answers"] = new JsonArray(new JsonObject
				{
					["id"] = "q1", ["type"] = "select", ["value"] = "diff",
					["label"] = "弹层看 diff（本轮该文件的补丁）",
				}),
			},
		};
		var details = ToolCallDetails.For(entry);

		Assert.Equal("弹层看 diff（本轮该文件的补丁）", Assert.Single(details.Exchanges).Answer);
	}

	[Fact]
	public void AskQuestionEmptyAnswerIsTreatedAsUnanswered()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(new JsonObject { ["question"] = "第一题？" }));
		var details = ToolCallDetails.For(Answered(Tool("ask_question", arguments, text: "q1: "), ("q1", "")));

		Assert.Null(Assert.Single(details.Exchanges).Answer);
	}

	[Fact]
	public void AskQuestionMatchesAnswersByExplicitId()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(
			new JsonObject { ["id"] = "color", ["question"] = "颜色？" },
			new JsonObject { ["id"] = "logo", ["question"] = "图标？" }));
		var details = ToolCallDetails.For(Answered(
			Tool("ask_question", arguments, text: "color: blue\nlogo: by-ext"),
			("color", "blue"), ("logo", "by-ext")));

		Assert.Equal("blue", details.Exchanges[0].Answer);
		Assert.Equal("by-ext", details.Exchanges[1].Answer);
	}

	[Fact]
	public void CodeModeShowsSourceAsCodeBlock()
	{
		var arguments = Json(root => root["code"] = "const value = await tools.read({ path: 'a.cs' });");
		var details = ToolCallDetails.For(Tool("codemode", arguments));

		Assert.Equal(Strings.ToolCodeLabel, details.BlockLabel);
		Assert.Equal("const value = await tools.read({ path: 'a.cs' });", details.BlockText);
		Assert.Empty(details.Fields);
	}
}
