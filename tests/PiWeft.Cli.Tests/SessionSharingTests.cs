using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// <c>/share</c> 的产物：pi 的查看器要的是**当前分支**加一条 <c>pi.share</c> 尾条目
/// （系统提示词 + 工具 schema）；整份会话文件里可能有别的分支，不能直接上传。
/// </summary>
[Collection("process-environment")]
public sealed class SessionSharingTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-share-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionSharingTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession(out SessionManager manager)
	{
		manager = SessionManager.InMemory(_root);
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = TestModels.Model(),
			StreamFn = static (model, messages, options) => throw new NotSupportedException(),
			Session = manager,
			Tools = [FileTools.Read(_root)],
			Settings = new CodingAgentSettings(),
			Persist = false,
			RecordInitialThinkingLevel = false,
			Skills = [],
			PromptTemplates = [],
		});
	}

	[Fact]
	public void ExportContainsOnlyTheCurrentBranchPlusTheShareTrailingEntry()
	{
		using var session = CreateSession(out var manager);
		var first = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("abandoned"), Timestamp = 2 });
		manager.Branch(first);
		var kept = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("kept"), Timestamp = 3 });

		var lines = SessionSharing.BuildShareJsonl(session)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => JsonNode.Parse(line)!.AsObject())
			.ToArray();

		Assert.Equal(4, lines.Length);
		Assert.Equal("session", lines[0]["type"]!.GetValue<string>());
		Assert.Equal(3, lines[0]["version"]!.GetValue<int>());
		Assert.Equal(session.Session.SessionId, lines[0]["id"]!.GetValue<string>());
		Assert.Equal(session.Session.Cwd, lines[0]["cwd"]!.GetValue<string>());

		Assert.Equal(first, lines[1]["id"]!.GetValue<string>());
		Assert.Null(lines[1]["parentId"]);
		Assert.Equal(kept, lines[2]["id"]!.GetValue<string>());
		Assert.Equal(first, lines[2]["parentId"]!.GetValue<string>());

		Assert.DoesNotContain("abandoned", string.Join('\n', lines.Select(line => line.ToJsonString())));

		var trailing = lines[3];
		Assert.Equal("custom", trailing["type"]!.GetValue<string>());
		Assert.Equal("pi.share", trailing["customType"]!.GetValue<string>());
		Assert.Equal(kept, trailing["parentId"]!.GetValue<string>());

		var data = trailing["data"]!.AsObject();
		Assert.False(string.IsNullOrWhiteSpace(data["systemPrompt"]!.GetValue<string>()));
		var tools = data["tools"]!.AsArray();
		var read = Assert.Single(tools, tool => tool!["name"]!.GetValue<string>() == "read");
		Assert.NotNull(read!["parameters"]);
	}

	[Fact]
	public void ViewerUrlUsesThePiDefaultAndHonorsTheEnvironmentOverride()
	{
		var previous = Environment.GetEnvironmentVariable("PI_SHARE_VIEWER_URL");
		try
		{
			Environment.SetEnvironmentVariable("PI_SHARE_VIEWER_URL", null);
			Assert.Equal("https://pi.dev/session/#abc123", SessionSharing.ViewerUrl("abc123"));

			Environment.SetEnvironmentVariable("PI_SHARE_VIEWER_URL", "https://viewer.example/session/");
			Assert.Equal("https://viewer.example/session/#abc123", SessionSharing.ViewerUrl("abc123"));
		}
		finally
		{
			Environment.SetEnvironmentVariable("PI_SHARE_VIEWER_URL", previous);
		}
	}
}
