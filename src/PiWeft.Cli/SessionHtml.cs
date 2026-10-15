using System.Net;
using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

using PiWeft.Ai.Json;
namespace PiWeft.Cli;

/// <summary>
/// 为 <c>export_html</c> 把会话渲染成自包含的 HTML：先是会话元数据，
/// 然后是当前分支的每条消息。不含脚本与外部资源，因此文件在任何地方都能打开。
/// </summary>
public static class SessionHtml
{
	public static string Render(CodingAgentSession session)
	{
		var builder = new StringBuilder();
		builder.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
		builder.Append("<title>").Append(Escape(SessionTitle(session))).Append("</title>\n");
		builder.Append("<style>\n").Append(Styles).Append("</style>\n</head>\n<body>\n");

		builder.Append("<header>\n<h1>").Append(Escape(SessionTitle(session))).Append("</h1>\n<dl>\n");
		Row(builder, "Session", session.Session.SessionId);
		Row(builder, "File", session.Session.SessionFile ?? "(not persisted)");
		Row(builder, "Working directory", session.Cwd);
		Row(builder, "Model", $"{session.Model.Provider}/{session.Model.Id}");
		Row(builder, "Thinking level", session.ThinkingLevel);
		builder.Append("</dl>\n</header>\n<main>\n");

		foreach (var message in session.Session.BuildProjectedMessages())
		{
			RenderMessage(builder, message);
		}

		builder.Append("</main>\n</body>\n</html>\n");
		return builder.ToString();
	}

	private static string SessionTitle(CodingAgentSession session) =>
		session.SessionName
		?? session.Session.FirstUserMessageText()
		?? $"Session {session.Session.SessionId}";

	private static void RenderMessage(StringBuilder builder, Message message)
	{
		switch (message)
		{
			case UserMessage user:
				builder.Append("<article class=\"user\">\n<h2>user</h2>\n");
				RenderContent(builder, user.Content);
				builder.Append("</article>\n");
				break;

			case AssistantMessage assistant:
				builder.Append("<article class=\"assistant\">\n<h2>assistant</h2>\n");
				foreach (var block in assistant.Content)
				{
					switch (block)
					{
						case TextContent text:
							builder.Append("<pre>").Append(Escape(text.Text)).Append("</pre>\n");
							break;

						case ThinkingContent thinking:
							builder.Append("<details><summary>thinking</summary><pre>")
								.Append(Escape(thinking.Thinking))
								.Append("</pre></details>\n");
							break;

						case ToolCall call:
							builder.Append("<pre class=\"tool\">→ ").Append(Escape(call.Name)).Append(' ')
								.Append(Escape(call.Arguments.ToJsonString(PiJson.NodeOptions))).Append("</pre>\n");
							break;

						case ImageContent:
							builder.Append("<p class=\"image\">[image]</p>\n");
							break;
					}
				}

				if (assistant.ErrorMessage is { Length: > 0 } error)
				{
					builder.Append("<pre class=\"error\">").Append(Escape(error)).Append("</pre>\n");
				}

				builder.Append("</article>\n");
				break;

			case ToolResultMessage result:
				builder.Append("<article class=\"tool-result\">\n<h2>tool</h2>\n");
				builder.Append("<p class=\"tool\">").Append(Escape(result.ToolName)).Append("</p>\n");
				RenderBlocks(builder, result.Content);
				builder.Append("</article>\n");
				break;

			case SystemMessage system:
				builder.Append("<details class=\"system\"><summary>system</summary><pre>")
					.Append(Escape(Transcript.GetText(system)))
					.Append("</pre></details>\n");
				break;

			case CustomMessage custom:
				// 压缩与分支摘要以自己的角色作为自定义消息出现。
				var role = custom.CustomRole is "compaction" or "branchSummary" ? "summary" : "custom";
				builder.Append("<article class=\"").Append(role).Append("\">\n<h2>")
					.Append(Escape(custom.CustomRole))
					.Append("</h2>\n");
				RenderContent(builder, custom.Content);
				builder.Append("</article>\n");
				break;
		}
	}

	private static void RenderContent(StringBuilder builder, MessageContent content)
	{
		if (content.Blocks is { Count: > 0 } blocks)
		{
			RenderBlocks(builder, blocks);
			return;
		}

		builder.Append("<pre>").Append(Escape(content.Text ?? string.Empty)).Append("</pre>\n");
	}

	private static void RenderBlocks(StringBuilder builder, IReadOnlyList<ContentBlock> blocks)
	{
		foreach (var block in blocks)
		{
			switch (block)
			{
				case TextContent text:
					builder.Append("<pre>").Append(Escape(text.Text)).Append("</pre>\n");
					break;

				case ImageContent image:
					builder.Append("<img alt=\"image\" src=\"data:")
						.Append(Escape(image.MimeType))
						.Append(";base64,")
						.Append(Escape(image.Data))
						.Append("\">\n");
					break;
			}
		}
	}

	private static void Row(StringBuilder builder, string label, string value) =>
		builder.Append("<dt>").Append(Escape(label)).Append("</dt><dd>").Append(Escape(value)).Append("</dd>\n");

	private static string Escape(string value) => WebUtility.HtmlEncode(value);

	private const string Styles = """
		:root { color-scheme: light dark; }
		body { font-family: system-ui, sans-serif; margin: 0 auto; max-width: 60rem; padding: 1.5rem; line-height: 1.5; }
		header h1 { font-size: 1.3rem; margin-bottom: .5rem; }
		dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; font-size: .85rem; opacity: .8; }
		dt { font-weight: 600; }
		dd { margin: 0; overflow-wrap: anywhere; }
		article { border-left: 3px solid #8884; padding: .5rem 0 .5rem 1rem; margin: 1rem 0; }
		article h2 { font-size: .75rem; text-transform: uppercase; letter-spacing: .05em; opacity: .6; margin: 0 0 .35rem; }
		article.user { border-color: #4a9; }
		article.assistant { border-color: #48c; }
		article.tool-result { border-color: #c94; }
		pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: .35rem 0; font-size: .9rem; }
		pre.tool { opacity: .85; }
		pre.error { color: #c33; }
		details { margin: .75rem 0; }
		img { max-width: 100%; }
		""";
}
