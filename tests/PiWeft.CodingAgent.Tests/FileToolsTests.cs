using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 并行工具调用里的同文件写入：每次 <c>edit</c> 都是「读—改—写」，
/// 不串行化时后写的那次会拿着旧内容，前一次修改被静默覆盖。
/// </summary>
public class FileToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-files-" + Guid.NewGuid().ToString("N")[..8]);

	public FileToolsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static AgentToolResult Execute(AgentTool tool, JsonObject arguments) =>
		tool.Execute(new AgentToolInvocation
		{
			ToolCallId = Guid.NewGuid().ToString("N"),
			ToolName = tool.Name,
			Arguments = arguments,
		}).GetAwaiter().GetResult();

	private static JsonObject EditArguments(string path, string marker) => new()
	{
		["path"] = path,
		["edits"] = new JsonArray(new JsonObject
		{
			["oldText"] = "anchor",
			["newText"] = "anchor " + marker,
		}),
	};

	[Fact]
	public async Task ConcurrentEditsToTheSameFileAllApply()
	{
		var file = Path.Combine(_root, "shared.txt");
		File.WriteAllText(file, "anchor");
		var tool = FileTools.Edit(_root);

		var edits = Enumerable.Range(1, 16)
			.Select(index => Task.Run(() => Execute(tool, EditArguments("shared.txt", "#" + index))))
			.ToArray();

		await Task.WhenAll(edits);

		var text = File.ReadAllText(file);
		for (var index = 1; index <= 16; index++)
		{
			Assert.Contains($"#{index}", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task CurrentContentIsReadAfterThePreviousEditWrote()
	{
		// 每次 edit 都建立在上一份内容之上：把唯一标记逐个追加，最后一次必须包含全部。
		var file = Path.Combine(_root, "chain.txt");
		File.WriteAllText(file, "anchor");
		var tool = FileTools.Edit(_root);

		for (var index = 0; index < 8; index++)
		{
			await Task.Run(() => Execute(tool, new JsonObject
			{
				["path"] = "chain.txt",
				["edits"] = new JsonArray(new JsonObject
				{
					["oldText"] = "anchor",
					["newText"] = "anchor " + index,
				}),
			}));
		}

		var text = File.ReadAllText(file);
		Assert.StartsWith("anchor", text, StringComparison.Ordinal);
		Assert.Equal(8, text.Split(' ').Length - 1);
	}

	[Fact]
	public async Task SymlinkAliasesShareTheSameLock()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 创建符号链接需要额外权限，Windows 上不作为回归用例。
		}

		var real = Path.Combine(_root, "real.txt");
		var alias = Path.Combine(_root, "alias.txt");
		File.WriteAllText(real, "anchor");
		File.CreateSymbolicLink(alias, real);
		var tool = FileTools.Edit(_root);

		var edits = Enumerable.Range(1, 8)
			.Select(index => Task.Run(() => Execute(
				tool,
				EditArguments(index % 2 == 0 ? "real.txt" : "alias.txt", "#" + index))))
			.ToArray();

		await Task.WhenAll(edits);

		var text = File.ReadAllText(real);
		for (var index = 1; index <= 8; index++)
		{
			Assert.Contains($"#{index}", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WriteFollowsSymlinksInsteadOfReplacingThem()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var real = Path.Combine(_root, "link-real.txt");
		var alias = Path.Combine(_root, "link-alias.txt");
		File.WriteAllText(real, "old");
		File.CreateSymbolicLink(alias, real);

		Execute(FileTools.Write(_root), new JsonObject
		{
			["path"] = "link-alias.txt",
			["content"] = "new",
		});

		Assert.Equal("new", File.ReadAllText(real));
		Assert.NotNull(new FileInfo(alias).LinkTarget);
	}

	[Fact]
	public void EditStillFailsOnMissingFile()
	{
		var tool = FileTools.Edit(_root);

		Assert.Throws<FileNotFoundException>(() => Execute(tool, EditArguments("missing.txt", "#1")));
	}
}
