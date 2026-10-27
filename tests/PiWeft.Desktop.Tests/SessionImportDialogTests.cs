using System.Text.Json.Nodes;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 导入外部会话弹窗。来源根目录与产物目录都注入夹具，不读也不写真实的
/// <c>~/.claude</c> / <c>~/.codex</c> 与用户会话目录。
/// </summary>
[Collection("mewui-controls")]
public sealed class SessionImportDialogTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-import-ui-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _home;
	private readonly string _project;
	private readonly string _sessions;

	public SessionImportDialogTests()
	{
		_home = Path.Combine(_root, "home");
		_project = Path.Combine(_root, "project");
		_sessions = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(_home);
		Directory.CreateDirectory(_project);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	/// <summary>
	/// 夹具用 <see cref="JsonObject"/> 拼，不写内插原始字符串：JSON 的连续右花括号会和
	/// 插值定界符打架（<c>$$"""</c> 下 <c>}}</c> 直接被当成插值闭合），一写一个坑。
	/// </summary>
	private void WriteClaudeSession(string sessionId, string text, string timestamp)
	{
		var path = Path.Combine(_home, ".claude", "projects", "-tmp-project", sessionId + ".jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllLines(path,
		[
			Message(sessionId, "user", text, timestamp).ToJsonString(),
			Message(sessionId, "assistant", "好", timestamp).ToJsonString(),
		]);
	}

	private JsonObject Message(string sessionId, string role, string text, string timestamp) => new()
	{
		["type"] = role,
		["sessionId"] = sessionId,
		["cwd"] = _project,
		["timestamp"] = timestamp,
		["message"] = new JsonObject
		{
			["role"] = role,
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
		},
	};

	private void WriteCodexSession()
	{
		var path = Path.Combine(_home, ".codex", "sessions", "2026", "10", "21", "rollout-1.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllLines(path,
		[
			new JsonObject
			{
				["timestamp"] = "2026-10-21T09:00:00.000Z",
				["type"] = "session_meta",
				["payload"] = new JsonObject { ["id"] = "codex-1", ["cwd"] = _project, ["model"] = "gpt-5" },
			}.ToJsonString(),
			new JsonObject
			{
				["timestamp"] = "2026-10-21T09:00:01.000Z",
				["type"] = "response_item",
				["payload"] = new JsonObject
				{
					["type"] = "message",
					["role"] = "user",
					["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Codex 的那条" }),
				},
			}.ToJsonString(),
		]);
	}

	private SessionImportDialog Dialog(Action? afterImport = null) =>
		new(afterImport ?? (static () => { }), _home, _sessions);

	[Fact]
	public void ListsSessionsWithOneRowEach()
	{
		WriteClaudeSession("a", "Claude 的那条", "2026-10-20T10:00:00.000Z");
		WriteCodexSession();

		var dialog = Dialog();
		dialog.ReloadSync();

		Assert.Equal(2, dialog.Sessions.Count);
		Assert.Contains(Find<TextBlock>(dialog.View), block => block.Text == "Claude 的那条");
		Assert.Contains(Find<TextBlock>(dialog.View), block => block.Text == "Codex 的那条");
		// 每条会话一个「导入」按钮。
		Assert.Equal(2, Find<Button>(dialog.View).Count(button => HoverHints.Text(button) is null && ContentText(button) == Strings.ImportOne));
	}

	[Fact]
	public void SourceFilterNarrowsTheList()
	{
		WriteClaudeSession("a", "Claude 的那条", "2026-10-20T10:00:00.000Z");
		WriteCodexSession();

		var dialog = Dialog();
		dialog.ReloadSync();

		// 分段按钮：全部来源 / 每个来源一条。
		var codexFilter = Find<Button>(dialog.View).Single(button => ContentText(button)?.StartsWith("Codex CLI", StringComparison.Ordinal) == true);
		ImagePreviewTests.Click(codexFilter);

		Assert.DoesNotContain(Find<TextBlock>(dialog.View), block => block.Text == "Claude 的那条");
		Assert.Contains(Find<TextBlock>(dialog.View), block => block.Text == "Codex 的那条");
	}

	[Fact]
	public void ImportingARowWritesASessionAndCallsBack()
	{
		WriteClaudeSession("a", "要导入的", "2026-10-20T10:00:00.000Z");

		var refreshed = 0;
		var dialog = Dialog(() => refreshed++);
		dialog.ReloadSync();

		var import = Find<Button>(dialog.View).Single(button => ContentText(button) == Strings.ImportOne);
		ImagePreviewTests.Click(import);

		Assert.Equal(1, refreshed);
		Assert.Single(Directory.GetFiles(_sessions, "*.jsonl"));
	}

	[Fact]
	public void ImportAllWritesEveryListedSession()
	{
		WriteClaudeSession("a", "第一条", "2026-10-20T10:00:00.000Z");
		WriteCodexSession();

		var dialog = Dialog();
		dialog.ReloadSync();
		ImagePreviewTests.Click(Find<Button>(dialog.View).Single(button => ContentText(button) == Strings.ImportAll));

		// 导入跑在后台线程（几百条不能在 UI 线程上同步跑完），这里等它落地。
		var deadline = Environment.TickCount64 + 20_000;
		while (Environment.TickCount64 < deadline &&
			(!Directory.Exists(_sessions) || Directory.GetFiles(_sessions, "*.jsonl").Length < 2))
		{
			Thread.Sleep(20);
		}

		Assert.Equal(2, Directory.GetFiles(_sessions, "*.jsonl").Length);
	}

	[Fact]
	public void EmptyLibraryExplainsWhereToLook()
	{
		var dialog = Dialog();
		dialog.ReloadSync();

		Assert.Empty(dialog.Sessions);
		// 空态必须说清「去哪里找过」，否则用户判断不了是没装工具还是会话库在别处。
		Assert.Contains(Find<TextBlock>(dialog.View), block => block.Text == Strings.ImportEmptyHint);
		// 一个来源都没有时来源分段整行不占位。
		Assert.DoesNotContain(Find<Button>(dialog.View), button => ContentText(button) == Strings.ImportAllSources);
	}

	/// <summary>按钮内容可能是 TextBlock（普通）或 DockPanel（带图标），取其中的文本。</summary>
	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			DockPanel panel => Find<TextBlock>(panel).Select(static block => block.Text).FirstOrDefault(static text => text is { Length: > 0 }),
			_ => null,
		};
}
