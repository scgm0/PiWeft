using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Reflection;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ToolMessageViewTests
{
	private static ChatEntry Tool(string name, string arguments, string text = "done", string? patch = null) => new()
	{
		Role = ChatRole.Tool,
		ToolName = name,
		ToolArguments = arguments,
		Text = text,
		Patch = patch,
		DurationMs = 12,
	};

	private static string Json(Action<JsonObject> build)
	{
		var root = new JsonObject();
		build(root);
		return root.ToJsonString(PiJson.NodeOptions);
	}

	private static void WithExpanded(ChatEntry entry, Action<ToolMessageView> assert)
	{
		using var scroll = new ScrollViewer();
		using var focus = new TextFocusScrollGuard(scroll);
		var view = new ToolMessageView(entry, focus);
		Find<Expander>(view.View).Single().IsExpanded = true;
		try
		{
			assert(view);
		}
		finally
		{
			UiLifetime.Release(view.View);
		}
	}

	private static void Click(Button button) =>
		typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

	private static string[] Texts(ToolMessageView view) =>
		[.. Find<MultiLineTextBox>(view.View).Select(text => text.Text)];

	private static string[] Labels(ToolMessageView view) =>
		[.. Find<TextBlock>(view.View).Select(block => block.Text)];

	[Fact]
	public void AskQuestionBodyPairsAnswersAndHidesRawIds()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(
			new JsonObject { ["question"] = "第一题？" },
			new JsonObject { ["question"] = "第二题？" }));
		var details = new JsonObject
		{
			["answers"] = new JsonArray(
				new JsonObject
				{
					["id"] = "q1", ["type"] = "select", ["value"] = "diff",
					["label"] = "弹层看 diff",
				},
				new JsonObject
				{
					["id"] = "q2", ["type"] = "select", ["value"] = "one-row",
					["label"] = "只占一行，放不下的收进 +N",
				}),
		};

		WithExpanded(Tool("ask_question", arguments, text: "q1: diff\nq2: one-row") with { ToolDetails = details }, view =>
		{
			Assert.Contains("第一题？", Texts(view));
			Assert.Contains("弹层看 diff", Texts(view));
			Assert.Contains("第二题？", Texts(view));
			Assert.Contains("只占一行，放不下的收进 +N", Texts(view));
			Assert.DoesNotContain("diff", Texts(view));
			Assert.DoesNotContain("one-row", Texts(view));
			Assert.DoesNotContain(Texts(view), text => text.Contains("q1:", StringComparison.Ordinal));
			Assert.Contains(Strings.ToolAnswersLabel, Labels(view));
			Assert.DoesNotContain(Strings.ToolResultLabel, Labels(view));
		});
	}

	[Fact]
	public void AskQuestionWithoutAnswerStillShowsTheRawResult()
	{
		var arguments = Json(root => root["questions"] = new JsonArray(new JsonObject { ["question"] = "第一题？" }));
		WithExpanded(Tool("ask_question", arguments, text: "用户取消了提问"), view =>
		{
			Assert.Contains("第一题？", Texts(view));
			Assert.Contains(Strings.ToolQuestionLabel, Labels(view));
			Assert.Contains(Strings.ToolResultLabel, Labels(view));
			Assert.Contains("用户取消了提问", Texts(view));
		});
	}

	[Fact]
	public void WriteBodyShowsFileContentAsPlainText()
	{
		const string content = "# Note\n\nbody\n";
		var arguments = Json(root =>
		{
			root["path"] = "docs/note.md";
			root["content"] = content;
		});

		WithExpanded(Tool("write", arguments), view =>
		{
			Assert.Contains(content, Texts(view));
			Assert.DoesNotContain(Texts(view), text =>
				text.Contains("\\n", StringComparison.Ordinal) && text.Contains("\"content\"", StringComparison.Ordinal));
			Assert.Contains(Strings.ToolContentLabel, Labels(view));
			Assert.Contains(Strings.ToolFieldPath, Labels(view));
			Assert.Contains(Strings.ToolFieldLines, Labels(view));
		});
	}

	[Fact]
	public void TodoItemsAreSelectable()
	{
		var arguments = Json(root =>
		{
			root["action"] = "replace";
			root["items"] = new JsonArray(new JsonObject { ["text"] = "检查计划", ["status"] = "in_progress" });
		});
		WithExpanded(Tool("todo", arguments), view =>
		{
			var text = Find<MultiLineTextBox>(view.View).Single(block => block.Text == "检查计划");
			text.Select(0, 2);
			Assert.Equal("检查", text.SelectedText);
		});
	}

	[Fact]
	public void FieldLabelAndValueShareTheSameRowCenter()
	{
		WithExpanded(Tool("read", """{"path":"src/App.cs"}"""), view =>
		{
			ConversationPerformanceTests.Layout(view.View, 700, 800);
			var label = Find<TextBlock>(view.View).Single(block => block.Text == Strings.ToolFieldPath);
			var value = Find<MultiLineTextBox>(view.View).Single(text => text.Text == "src/App.cs");
			Assert.InRange(Math.Abs(label.Bounds.Center.Y - value.Bounds.Center.Y), 0, 1);
		});
	}

	[Fact]
	public void FieldValueIsSelectable()
	{
		WithExpanded(Tool("read", """{"path":"src/App.cs"}"""), view =>
		{
			var value = Find<MultiLineTextBox>(view.View).Single(text => text.Text == "src/App.cs");
			value.Select(0, 3);
			Assert.Equal("src", value.SelectedText);
		});
	}

	[Fact]
	public void LargeWriteContentUsesBoundedViewport()
	{
		var content = string.Join('\n', Enumerable.Range(0, MessageText.LargeTextLines + 1).Select(index => $"line {index}"));
		var arguments = Json(root =>
		{
			root["path"] = "docs/large.md";
			root["content"] = content;
		});
		WithExpanded(Tool("write", arguments), view =>
		{
			var body = Find<MultiLineTextBox>(view.View).Single(text => text.Text == content);
			Assert.Equal(MessageText.LargeTextMaxHeight, body.MaxHeight);
			ConversationPerformanceTests.Layout(view.View, 700, 4000);
			Assert.InRange(body.ActualHeight, 1, MessageText.LargeTextMaxHeight + 1);
		});
	}

	[Fact]
	public void LargeTextOnlyConstructsVisibleLines()
	{
		const int lineCount = 5000;
		var text = string.Join('\n', Enumerable.Range(0, lineCount).Select(index => $"line {index}"));
		var view = MessageText.Create(text, mono: true, fontSize: AppTheme.FontSizeCaption);
		view.MaxHeight(MessageText.LargeTextMaxHeight);
		var constructed = new HashSet<int>();
		view.LineConstructionStarting += (_, line) => constructed.Add(line);
		var panel = new StackPanel().Children(view);
		ConversationPerformanceTests.Layout(panel, 700, 4000);
		Assert.InRange(constructed.Count, 1, 200);
		UiLifetime.Release(panel);
	}

	[Fact]
	public void StreamingPlaceholderIsNotRepeatedAsResult()
	{
		const string arguments = """{"command":"dotnet build"}""";
		WithExpanded(Tool("bash", arguments, text: $"→ bash {arguments}") with { IsStreaming = true }, view =>
		{
			Assert.DoesNotContain(Strings.ToolResultLabel, Labels(view));
		});
	}

	[Fact]
	public void CopyButtonCopiesRawArguments()
	{
		const string arguments = """{"path":"src/App.cs","offset":3}""";
		string? copied = null;
		using var scroll = new ScrollViewer();
		using var focus = new TextFocusScrollGuard(scroll);
		var view = new ToolMessageView(Tool("read", arguments), focus, copy: text => copied = text);
		Find<Expander>(view.View).Single().IsExpanded = true;
		try
		{
			var button = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.CopyToolArguments);
			Click(button);
			Assert.Equal(arguments, copied);
		}
		finally
		{
			UiLifetime.Release(view.View);
		}
	}

	[Fact]
	public void DiffModeButtonsCanBeToggledRepeatedly()
	{
		const string patch = "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-old\n+new\n";
		WithExpanded(Tool("edit", """{"path":"a.cs"}""", patch: patch), view =>
		{
			var unified = Find<Button>(view.View).Single(button =>
				Find<TextBlock>(button).Any(block => block.Text == Strings.DiffUnified));
			var sideBySide = Find<Button>(view.View).Single(button =>
				Find<TextBlock>(button).Any(block => block.Text == Strings.DiffSideBySide));
			for (var index = 0; index < 3; index++)
			{
				Click(sideBySide);
				Click(unified);
			}
		});
	}

	[Fact]
	public void StoppedToolWithoutResultKeepsArgumentsOutOfResult()
	{
		const string arguments = """{"path":"src/App.cs"}""";
		WithExpanded(Tool("read", arguments, text: arguments) with { IsStopped = true }, view =>
		{
			Assert.Contains(Strings.ToolFieldPath, Labels(view));
			Assert.DoesNotContain(Strings.ToolResultLabel, Labels(view));
		});
	}

	[Fact]
	public void CompletedResultEqualToCommandIsStillShown()
	{
		WithExpanded(Tool("bash", """{"command":"ls"}""", text: "ls"), view =>
		{
			Assert.Contains(Strings.ToolResultLabel, Labels(view));
		});
	}

	[Fact]
	public void ResultTrailingNewlinesAreTrimmed()
	{
		WithExpanded(Tool("bash", """{"command":"ls"}""", text: "file-a\nfile-b\n\n"), view =>
		{
			Assert.Contains("file-a\nfile-b", Texts(view));
			Assert.DoesNotContain("file-a\nfile-b\n\n", Texts(view));
		});
	}

	[Fact]
	public void UnknownNestedArgumentsKeepIndentedJson()
	{
		WithExpanded(Tool("mcp_search", """{"query":"piweft","filters":["a","b"]}"""), view =>
		{
			Assert.Contains(Texts(view), text =>
				text.Contains("\n", StringComparison.Ordinal) && text.Contains("\"filters\"", StringComparison.Ordinal));
			Assert.Contains(Strings.ToolArgsLabel, Labels(view));
		});
	}

	[Fact]
	public void EditBodyUsesDiffWithoutRawJson()
	{
		const string patch = "--- a/src/App.cs\n+++ b/src/App.cs\n@@ -1 +1 @@\n-old value\n+new value\n";
		var arguments = """{"path":"src/App.cs","edits":[{"oldText":"old value","newText":"new value"}]}""";

		WithExpanded(Tool("edit", arguments, patch: patch), view =>
		{
			Assert.Contains(Strings.ToolFieldPath, Labels(view));
			Assert.Contains(Strings.ToolDiffLabel, Labels(view));
			Assert.DoesNotContain(Texts(view), text => text.Contains("\"oldText\"", StringComparison.Ordinal));
		});
	}
}
