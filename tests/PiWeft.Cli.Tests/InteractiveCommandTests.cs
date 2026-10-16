using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>交互模式的 pi 专属斜杠命令：/import 与 /copy（NEW-06）。</summary>
public class InteractiveCommandTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-interactive-" + Guid.NewGuid().ToString("N")[..8]);

	public InteractiveCommandTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession()
	{
		var script = new ScriptedStreamFn();
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = TestModels.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = new CodingAgentSettings(),
		});
	}

	[Fact]
	public void ImportSessionAppendsSourceBranchMessagesAndRebuildsTheContext()
	{
		// 源会话带一段完整的对话。
		var source = SessionManager.Create(_root, Path.Combine(_root, "source-sessions"));
		source.AppendMessage(new UserMessage { Content = MessageContent.FromText("源里的问题") });
		source.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "源里的回答" }] });
		var sourceFile = source.SessionFile!;

		using var target = CreateSession();
		target.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("既有消息") });

		var imported = CliRunner.ImportSession(target, sourceFile);

		Assert.Equal(2, imported);
		Assert.Equal(3, target.Session.BuildProjectedMessages().Count);
		Assert.Contains(
			"源里的回答",
			target.Session.BuildProjectedMessages().OfType<AssistantMessage>().Single().Content.OfType<TextContent>().Single().Text);

		// 源文件不被改动。
		Assert.Equal(2, SessionManager.Open(sourceFile).BuildProjectedMessages().Count);
	}

	[Fact]
	public void ImportSessionRejectsMissingFiles()
	{
		using var target = CreateSession();
		Assert.Throws<InvalidOperationException>(
			() => CliRunner.ImportSession(target, Path.Combine(_root, "absent.jsonl")));
	}

	[Fact]
	public void CopyUsesTheLastAssistantText()
	{
		// /copy 的取数路径：LastAssistantText 取当前分支最后一条助手回复。
		using var session = CreateSession();
		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("q") });
		session.Session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "要复制的内容" }] });

		Assert.Equal("要复制的内容", session.Session.LastAssistantText());
	}
}
