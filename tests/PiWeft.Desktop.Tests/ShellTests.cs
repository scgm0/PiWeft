using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// The sidebar and the single-instance guard. Both run headless: the catalog reads the same session files the CLI
/// writes, and the guard is exercised by acquiring the same key twice.
/// </summary>
[Collection("process-environment")]
public class ShellTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktop-shell-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ShellTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void CatalogListsSessionsNewestFirstWithTheirNameAndCwd()
	{
		var work = Path.Combine(_root, "project");
		Directory.CreateDirectory(work);

		var older = SessionManager.Create(work);
		older.AppendMessage(new UserMessage { Content = MessageContent.FromText("first question") });
		older.AppendSessionInfo("named session");

		var newer = SessionManager.Create(work);
		newer.AppendMessage(new UserMessage { Content = MessageContent.FromText("another question") });

		var catalog = new SessionCatalog();
		var items = catalog.List(work);

		Assert.Equal(2, items.Count);

		var named = items.Single(static item => item.Name == "named session");
		Assert.Equal(work, named.Cwd);
		Assert.Equal(1, named.MessageCount);
		Assert.Equal(older.SessionId, named.Id);
		Assert.Equal("named session", named.Display);

		// 首条用户消息也作为摘要留档，供列表与标题使用。
		Assert.Equal("first question", named.Preview);

		// 没有命名的会话用第一条用户消息当标题，而不是文件名。
		var unnamed = items.Single(static item => item.Name.Length == 0);
		Assert.Equal("another question", unnamed.Display);
	}

	[Fact]
	public void CatalogReusesUnchangedSummariesAndInvalidatesAppendsRenamesAndDeletedFiles()
	{
		var manager = SessionManager.Create(_root);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("preview") });
		var catalog = new SessionCatalog();
		var first = Assert.Single(catalog.List(_root));
		Assert.Same(first, Assert.Single(catalog.List(_root)));
		manager.AppendSessionInfo("new name");
		var renamed = Assert.Single(catalog.List(_root));
		Assert.Equal("new name", renamed.Name);
		Assert.NotSame(first, renamed);
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = new string('x', 100_000) }] });
		Assert.Equal(2, Assert.Single(catalog.List(_root)).MessageCount);
		var trash = catalog.Delete(manager.SessionFile!);
		Assert.Empty(catalog.List());
		Assert.Single(catalog.ListDeleted());
		catalog.Restore(trash);
		Assert.Single(catalog.List(_root));
		Assert.Empty(catalog.ListDeleted());
		File.Delete(manager.SessionFile!);
		Assert.Empty(catalog.List());
	}

	[Fact]
	public void CatalogReadsTextBlocksAndLatestNameClearWithoutMaterializingToolPayloads()
	{
		var manager = SessionManager.Create(_root);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromBlocks([new TextContent { Text = "block preview" }]) });
		manager.AppendMessage(new ToolResultMessage { ToolName = "read", ToolCallId = "call", Content = [new TextContent { Text = new string('x', 100_000) }] });
		manager.AppendSessionInfo("temporary name");
		manager.AppendSessionInfo(null);
		var item = Assert.Single(new SessionCatalog().List(_root));
		Assert.Equal("block preview", item.Preview);
		Assert.Equal(string.Empty, item.Name);
		Assert.Equal(2, item.MessageCount);
	}

	[Fact]
	public void CatalogFiltersByWorkingDirectory()
	{
		var first = Path.Combine(_root, "one");
		var second = Path.Combine(_root, "two");
		Directory.CreateDirectory(first);
		Directory.CreateDirectory(second);

		SessionManager.Create(first).AppendMessage(new UserMessage { Content = MessageContent.FromText("a") });
		SessionManager.Create(second).AppendMessage(new UserMessage { Content = MessageContent.FromText("b") });

		var catalog = new SessionCatalog();

		Assert.Single(catalog.List(first));
		Assert.Single(catalog.List(second));
		Assert.Equal(2, catalog.List().Count);
	}

	[Fact]
	public void CatalogOpensASessionAndResumesIt()
	{
		var work = Path.Combine(_root, "resume");
		Directory.CreateDirectory(work);

		var created = SessionManager.Create(work);
		created.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello") });
		created.AppendMessage(new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "test",
			Model = "test-model",
			Content = [new TextContent { Text = "hi" }],
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		});

		var item = Assert.Single(new SessionCatalog().List(work));
		var opened = new SessionCatalog().Open(item.Path);

		Assert.Equal(created.SessionId, opened.SessionId);
		Assert.Equal(2, opened.BuildProjectedMessages().Count);
		Assert.Contains(
			opened.BuildProjectedMessages(),
			static message => message is AssistantMessage assistant &&
				assistant.Content.OfType<TextContent>().Any(static block => block.Text == "hi"));
	}

	[Fact]
	public async Task MessagePathClickLaunchesConfiguredEditorWithDecodedPathAndLocation()
	{
		if (OperatingSystem.IsWindows()) return;
		var work = Path.Combine(_root, "project with spaces");
		var bin = Path.Combine(_root, "bin");
		Directory.CreateDirectory(work);
		Directory.CreateDirectory(bin);
		var file = Path.Combine(work, "sample file.cs");
		var capture = Path.Combine(_root, "editor-arguments.txt");
		var editor = Path.Combine(bin, "code");
		File.WriteAllText(file, "sample");
		File.WriteAllText(editor, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$PIWEFT_OPEN_CAPTURE\"\n");
		File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var visual = Environment.GetEnvironmentVariable("VISUAL");
		var previousCapture = Environment.GetEnvironmentVariable("PIWEFT_OPEN_CAPTURE");
		try
		{
			Environment.SetEnvironmentVariable("VISUAL", editor);
			Environment.SetEnvironmentVariable("PIWEFT_OPEN_CAPTURE", capture);
			Assert.True(Shell.OpenMessageLink(new Uri(file).AbsoluteUri + "#L12C3", work));
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while (!File.Exists(capture)) await Task.Delay(10, timeout.Token);
			Assert.Equal(new[] { "--goto", $"{file}:12:3" }, File.ReadAllLines(capture));
		}
		finally
		{
			Environment.SetEnvironmentVariable("VISUAL", visual);
			Environment.SetEnvironmentVariable("PIWEFT_OPEN_CAPTURE", previousCapture);
		}
	}

	[Fact]
	public void OnlyTheFirstInstanceHoldsTheKey()
	{
		var key = "test-" + Guid.NewGuid().ToString("N")[..8];

		using (var first = SingleInstance.Acquire(key))
		{
			Assert.True(first.IsFirstInstance);

			using var second = SingleInstance.Acquire(key);
			Assert.False(second.IsFirstInstance);

			// Signalling a running instance must not throw when one is listening.
			SingleInstance.SignalRunningInstance(key);
		}

		// The socket is released, so a later launch becomes the first instance again.
		using var third = SingleInstance.Acquire(key);
		Assert.True(third.IsFirstInstance);
	}

	[Fact]
	public async Task TheRunningInstanceIsNotifiedWhenAnotherLaunchSignals()
	{
		var key = "test-" + Guid.NewGuid().ToString("N")[..8];
		using var instance = SingleInstance.Acquire(key);
		Assert.True(instance.IsFirstInstance);

		var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		instance.Listen(() => activated.TrySetResult());

		SingleInstance.SignalRunningInstance(key);

		Assert.True(
			await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(10))) == activated.Task,
			"the running instance was never notified");
	}

	[Fact]
	public void RenameWritesASessionInfoEntryAndDeleteMovesTheFile()
	{
		var work = Path.Combine(_root, "manage");
		Directory.CreateDirectory(work);

		var manager = SessionManager.Create(work);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello") });

		var catalog = new SessionCatalog();
		var path = Assert.Single(catalog.List(work)).Path;

		// 重命名只是追加一条 session_info，历史条目保持不变。
		catalog.Rename(path, "renamed session");
		var renamed = Assert.Single(catalog.List(work));
		Assert.Equal("renamed session", renamed.Name);
		Assert.Equal(1, renamed.MessageCount);

		// 名称留空即清除。
		catalog.Rename(path, null);
		Assert.Equal(string.Empty, Assert.Single(catalog.List(work)).Name);

		// 删除是移动到 .trash，原始文件消失但内容保留。
		catalog.Rename(path, "to delete");
		var trashPath = catalog.Delete(path);

		Assert.False(File.Exists(path));
		Assert.True(File.Exists(trashPath));
		Assert.Contains(".trash", trashPath);
		Assert.Empty(catalog.List(work));

		var moved = SessionManager.Open(trashPath);
		Assert.Equal("to delete", moved.CurrentName());
		Assert.Single(moved.BuildProjectedMessages());
	}

	[Fact]
	public void SearchMatchesNameIdDirectoryAndFileName()
	{
		var work = Path.Combine(_root, "search-project");
		Directory.CreateDirectory(work);

		var named = SessionManager.Create(work);
		named.AppendMessage(new UserMessage { Content = MessageContent.FromText("first") });
		named.AppendSessionInfo("checkout fix");

		var other = SessionManager.Create(work);
		other.AppendMessage(new UserMessage { Content = MessageContent.FromText("second") });

		var items = new SessionCatalog().List(work);
		Assert.Equal(2, items.Count);

		// 名称匹配（忽略大小写）。
		var byName = SessionCatalog.Search(items, "CHECKOUT");
		Assert.Equal(named.SessionId, Assert.Single(byName).Id);

		// id 匹配。
		Assert.Equal(other.SessionId, Assert.Single(SessionCatalog.Search(items, other.SessionId)).Id);

		// 目录匹配：两个会话都在同一个目录里。
		Assert.True(
			SessionCatalog.Search(items, "search-project").Count == 2,
			$"cwd=[{string.Join("|", items.Select(static item => item.Cwd))}] path=[{items[0].Path}]");

		// 文件名匹配（含时间戳与 id，因此唯一）。
		var fileName = Path.GetFileNameWithoutExtension(other.SessionFile!);
		Assert.Equal(other.SessionId, Assert.Single(SessionCatalog.Search(items, fileName)).Id);

		// 空前缀不过滤，未命中返回空。
		Assert.Equal(2, SessionCatalog.Search(items, "  ").Count);
		Assert.Empty(SessionCatalog.Search(items, "no-such-session"));
	}

	[Fact]
	public void DisplayPrefersNameThenFirstMessageThenAPlaceholder()
	{
		var path = Path.Combine(_root, "2026-01-01T00-00-00-000Z_abc.jsonl");

		// 文件名（时间戳 + id）对用户没有意义，不该当标题。
		Assert.Equal(Strings.UntitledSession, new SessionListItem { Path = path, Id = "abc" }.Display);
		Assert.Equal(
			"修一下登录",
			new SessionListItem { Path = path, Id = "abc", Preview = "修一下登录" }.Display);
		Assert.Equal(
			"命名过的会话",
			new SessionListItem { Path = path, Id = "abc", Preview = "修一下登录", Name = "命名过的会话" }.Display);
	}

	[Fact]
	public void CatalogIgnoresCorruptLines()
	{
		var work = Path.Combine(_root, "corrupt");
		Directory.CreateDirectory(work);

		var manager = SessionManager.Create(work);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("survives") });

		// Append a truncated line, as an interrupted write would leave behind.
		File.AppendAllText(manager.SessionFile!, "{\"type\":\"message\",\"id\":\"x\"\n");

		var item = Assert.Single(new SessionCatalog().List(work));
		Assert.Equal(1, item.MessageCount);
	}
}

/// <summary>跨会话内容搜索：只扫最近若干文件、命中即停，返回围绕关键字的摘要。</summary>
[Collection("process-environment")]
public class SessionContentSearchTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktop-grep-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public SessionContentSearchTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string NewSession(string userText, string assistantText)
	{
		var work = Path.Combine(_root, "work");
		Directory.CreateDirectory(work);

		var manager = SessionManager.Create(work);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText(userText) });
		manager.AppendMessage(new AssistantMessage
		{
			Api = "test",
			Provider = "test",
			Model = "m",
			Content = [new TextContent { Text = assistantText }],
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		});

		return manager.SessionFile!;
	}

	[Fact]
	public void FindsSessionsByBodyTextAndSkipsOthers()
	{
		var hit = NewSession("请问怎么处理 diff 渲染", "用统一 diff");
		NewSession("完全无关的话题", "也没什么关系");

		var sessions = new SessionCatalog().List();
		var matches = SessionCatalog.SearchContent(sessions, "difference");

		Assert.Empty(matches); // 关键字不匹配任何人

		var real = SessionCatalog.SearchContent(sessions, "diff 渲染");
		var match = Assert.Single(real);
		Assert.Equal(hit, match.Session.Path);
		Assert.Contains("diff 渲染", match.Excerpt);
	}

	[Fact]
	public void SearchIsCaseInsensitiveAndRespectsTheFileLimit()
	{
		NewSession("HELLO world", "ok");
		NewSession("hello again", "ok");

		var sessions = new SessionCatalog().List();

		Assert.Equal(2, SessionCatalog.SearchContent(sessions, "hello").Count);

		// 只扫一个文件时最多返回一条。
		Assert.Single(SessionCatalog.SearchContent(sessions, "hello", maxFiles: 1));
	}

	[Fact]
	public void EmptyQueryAndMissingFilesAreTolerated()
	{
		NewSession("abc", "def");
		var sessions = new SessionCatalog().List();

		Assert.Empty(SessionCatalog.SearchContent(sessions, "   "));
		Assert.Empty(SessionCatalog.SearchContent([new SessionListItem { Path = Path.Combine(_root, "missing.jsonl"), Id = "x" }], "abc"));
	}

	[Fact]
	public void ContentSearchStopsAtTheByteBudgetEvenInsideOneLargeLine()
	{
		var path = Path.Combine(_root, "long.jsonl");
		File.WriteAllText(path, new string('中', 100) + "needle");
		var rows = new[] { new SessionListItem { Id = "long", Path = path } };
		Assert.Empty(SessionCatalog.SearchContent(rows, "needle", maxBytesPerFile: 200));
		Assert.Single(SessionCatalog.SearchContent(rows, "needle", maxBytesPerFile: 306));
	}

	[Fact]
	public void ContentSearchHonorsCancellationBeforeOpeningFiles()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var rows = new[] { new SessionListItem { Id = "x", Path = Path.Combine(_root, "missing.jsonl") } };
		Assert.Throws<OperationCanceledException>(() => SessionCatalog.SearchContent(rows, "needle", cancellationToken: cancellation.Token));
	}

	[Fact]
	public void SummaryPrefersTheMessageTextOverRawJson()
	{
		// 真实会话行是 JSONL；摘要应当取自消息文本，而不是把字段名一起端出来。
		var line = """{"type":"message","id":"ab","message":{"role":"user","content":"请把 diff 渲染成统一格式"}}""";
		var summary = SessionCatalog.Summarize(line, "diff 渲染");

		Assert.Contains("diff 渲染成统一格式", summary);
		Assert.DoesNotContain("\"role\"", summary);
		Assert.DoesNotContain("\"type\"", summary);
	}

	[Fact]
	public void SummaryFallsBackToTheWholeLineWhenTheJsonHasNoText()
	{
		var line = """{"type":"message","id":"ab"}""";
		var summary = SessionCatalog.Summarize(line, "ab");

		Assert.Contains("ab", summary);
	}

	[Fact]
	public void SummaryIsCenteredOnTheKeywordAndBounded()
	{
		var line = new string('a', 200) + "NEEDLE" + new string('b', 200);
		var summary = SessionCatalog.Summarize(line, "NEEDLE");

		Assert.Contains("NEEDLE", summary);
		Assert.StartsWith("…", summary);
		Assert.EndsWith("…", summary);
		Assert.True(summary.Length < 160, $"摘要过长：{summary.Length}");
	}
}

/// <summary>
/// 图标资源与源码的一致性：界面代码里写错一个图标名，MewUI 会在构建界面时抛异常并被 UI 线程吞掉 ——
/// 表现是「某个标签页打不开」这种极难定位的故障（设置面板就曾因为缺 rotate-ccw.svg 而整个打不开）。
/// </summary>
public class IconResourceTests
{
	[Fact]
	public void EveryIconUsedInSourceHasAnEmbeddedResource()
	{
		var desktop = FindDesktopProject();
		var icons = Path.Combine(desktop, "Resources", "icons");
		Assert.True(Directory.Exists(icons), $"找不到图标目录：{icons}");

		var used = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
		{
			foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
				File.ReadAllText(file),
				@"AppIcons\.(?:Button|Get|Icon)\(\s*""(?<name>[a-z0-9-]+)"""))
			{
				used.Add(match.Groups["name"].Value);
			}
		}

		Assert.NotEmpty(used);

		var missing = used.Where(name => !File.Exists(Path.Combine(icons, name + ".svg"))).ToList();
		Assert.True(
			missing.Count == 0,
			$"这些图标被代码引用但缺少 Resources/icons/*.svg：{string.Join("、", missing)}");
	}

	/// <summary>测试进程在 bin 目录下运行，向上找到仓库里的 src/PiWeft.Desktop。</summary>
	private static string FindDesktopProject()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "src", "PiWeft.Desktop");
			if (Directory.Exists(Path.Combine(candidate, "Resources", "icons")))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("没有找到 src/PiWeft.Desktop");
	}
}
