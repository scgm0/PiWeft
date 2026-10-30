using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 旧版会话升级：pi 在打开会话时把 v1/v2 就地迁移到 v3，本端口必须做同样的事，
/// 否则 v1（没有 id/parentId）的分支投影只能看到最后一条。
/// </summary>
public class SessionMigrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-migration-" + Guid.NewGuid().ToString("N")[..8]);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string TempFile(string name)
	{
		Directory.CreateDirectory(_root);
		return Path.Combine(_root, name);
	}

	private static string Text(MessageContent content) =>
		content.Text ?? content.Blocks?.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;

	private static string MessageLine(string role, string text, int timestamp) =>
		"{\"type\":\"message\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"" + role +
		"\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"timestamp\":" + timestamp + "}}";

	private static string V1Header(string id = "v1-session") =>
		"{\"type\":\"session\",\"id\":\"" + id +
		"\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"cwd\":\"/workspace/legacy\"}";

	private string WriteV1File()
	{
		var path = TempFile("v1.jsonl");
		File.WriteAllLines(path,
		[
			V1Header(),
			MessageLine("user", "one", 1),
			MessageLine("assistant", "two", 2),
			// v1 的压缩边界是文件行下标（含头部），第 1 行就是第一条消息。
			"""{"type":"compaction","timestamp":"2026-01-01T00:00:00.000Z","summary":"earlier","firstKeptEntryIndex":1,"tokensBefore":42}""",
			MessageLine("user", "three", 3),
		]);
		return path;
	}

	[Fact]
	public void Open_MigratesV1TreeAndCompactionIndexAndRewritesFile()
	{
		var path = WriteV1File();

		var manager = SessionManager.Open(path);

		var entries = manager.Entries;
		Assert.Equal(4, entries.Count);
		Assert.All(entries, entry => Assert.False(string.IsNullOrEmpty(entry.Id)));
		Assert.Equal(entries.Count, entries.Select(static entry => entry.Id).Distinct().Count());
		Assert.Null(entries[0].ParentId);
		for (var index = 1; index < entries.Count; index++)
		{
			Assert.Equal(entries[index - 1].Id, entries[index].ParentId);
		}

		var compaction = Assert.IsType<CompactionEntry>(entries[2]);
		Assert.Equal(entries[0].Id, compaction.FirstKeptEntryId);
		Assert.Null(compaction.Extra);

		// 压缩摘要 + 保留区（user one、assistant two）+ 压缩之后的消息。
		var messages = manager.BuildProjectedMessages();
		Assert.Equal(4, messages.Count);
		Assert.Contains("<summary>", Assert.IsType<UserMessage>(messages[0]).Content.Text);
		Assert.Equal("one", Text(Assert.IsType<UserMessage>(messages[1]).Content));
		Assert.Equal("two", Assert.IsType<AssistantMessage>(messages[2]).Content.OfType<TextContent>().Single().Text);
		Assert.Equal("three", Text(Assert.IsType<UserMessage>(messages[^1]).Content));

		var lines = File.ReadAllLines(path);
		var header = JsonNode.Parse(lines[0])!.AsObject();
		Assert.Equal(3, header["version"]!.GetValue<int>());
		Assert.All(lines.Skip(1), line =>
		{
			var entry = JsonNode.Parse(line)!.AsObject();
			Assert.Equal(JsonValueKind.String, entry["id"]!.GetValueKind());
			Assert.True(entry["id"]!.GetValue<string>().Length > 0);
		});

		var compactionLine = JsonNode.Parse(lines[3])!.AsObject();
		Assert.False(compactionLine.ContainsKey("firstKeptEntryIndex"));
		Assert.Equal(entries[0].Id, compactionLine["firstKeptEntryId"]!.GetValue<string>());

		// 迁移是一次性的：再次打开不应再改文件。
		var before = File.ReadAllText(path);
		SessionManager.Open(path);
		Assert.Equal(before, File.ReadAllText(path));
	}

	[Fact]
	public void Open_MigratesV2HookMessageRoleToCustom()
	{
		var path = TempFile("v2.jsonl");
		File.WriteAllLines(path,
		[
			"""{"type":"session","version":2,"id":"v2-session","timestamp":"2026-01-01T00:00:00.000Z","cwd":"/workspace/legacy"}""",
			"""{"type":"message","id":"aaaaaaa1","parentId":null,"timestamp":"2026-01-01T00:00:00.000Z","message":{"role":"hookMessage","content":[{"type":"text","text":"injected"}],"timestamp":1}}""",
		]);

		var manager = SessionManager.Open(path);

		var message = Assert.IsType<CustomMessage>(
			Assert.IsType<SessionMessageEntry>(Assert.Single(manager.Entries)).Message);
		Assert.Equal("custom", message.CustomRole);
		Assert.NotNull(message.Raw);
		Assert.Equal("custom", message.Raw!["role"]!.GetValue<string>());

		var line = JsonNode.Parse(File.ReadAllLines(path)[1])!.AsObject();
		Assert.Equal("custom", line["message"]!["role"]!.GetValue<string>());
	}

	[Fact]
	public void Open_WithoutUpgradeKeepsLegacyBytesButReadsFullTree()
	{
		var path = WriteV1File();
		var before = File.ReadAllText(path);

		var manager = SessionManager.Open(path, upgradeFile: false);

		Assert.Equal(4, manager.Entries.Count);
		Assert.Equal(4, manager.BuildProjectedMessages().Count);
		Assert.Equal(before, File.ReadAllText(path));
	}

	[Fact]
	public void Open_LeavesCurrentVersionFileByteIdentical()
	{
		var manager = SessionManager.Create("/workspace/current", Path.Combine(_root, "sessions"));
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello"), Timestamp = 1 });
		var before = File.ReadAllText(manager.SessionFile!);

		SessionManager.Open(manager.SessionFile!);

		Assert.Equal(before, File.ReadAllText(manager.SessionFile!));
	}

	[Fact]
	public void ForkFrom_MigratesLegacySourceIntoNewCurrentVersionFile()
	{
		var source = WriteV1File();
		var expected = SessionManager.Open(source, upgradeFile: false).BuildProjectedMessages();

		var forked = SessionManager.ForkFrom(source, "/workspace/fork", Path.Combine(_root, "forks"));

		Assert.Equal(4, forked.Entries.Count);
		Assert.All(forked.Entries, entry => Assert.False(string.IsNullOrEmpty(entry.Id)));
		Assert.Equal(
			expected.Select(static message => message.Role),
			forked.BuildProjectedMessages().Select(static message => message.Role));

		var header = JsonNode.Parse(File.ReadAllLines(forked.SessionFile!)[0])!.AsObject();
		Assert.Equal(3, header["version"]!.GetValue<int>());
		Assert.Equal(Path.GetFullPath(source), header["parentSession"]!.GetValue<string>());
	}

	[Fact]
	public void Open_KeepsCompactionIndexOfUnknownEntriesAndMigratesTheirRawIds()
	{
		var path = TempFile("v1-unknown.jsonl");
		File.WriteAllLines(path,
		[
			V1Header("v1-unknown"),
			MessageLine("user", "one", 1),
			"""{"type":"future_entry","timestamp":"2026-01-01T00:00:00.000Z","payload":{"kept":true}}""",
			"""{"type":"compaction","timestamp":"2026-01-01T00:00:00.000Z","summary":"earlier","firstKeptEntryIndex":2,"tokensBefore":5}""",
		]);

		var manager = SessionManager.Open(path);

		var unknown = Assert.IsType<UnknownSessionEntry>(manager.Entries[1]);
		Assert.Equal(manager.Entries[0].Id, unknown.ParentId);
		Assert.Equal(unknown.Id, unknown.Raw["id"]!.GetValue<string>());
		Assert.Equal(unknown.ParentId, unknown.Raw["parentId"]!.GetValue<string>());

		var compaction = Assert.IsType<CompactionEntry>(manager.Entries[2]);
		Assert.Equal(unknown.Id, compaction.FirstKeptEntryId);
	}
}
