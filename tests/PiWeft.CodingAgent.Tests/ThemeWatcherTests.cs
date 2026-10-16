using PiWeft.CodingAgent.Themes;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>PI-10：主题目录监视热重载的触发与防抖。</summary>
public class ThemeWatcherTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-themewatch-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ThemeWatcherTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ThemeFileChangeTriggersDebouncedCallback()
	{
		var themes = Directory.CreateDirectory(Path.Combine(_agentDir, "themes"));
		var file = Path.Combine(themes.FullName, "forest.json");
		File.WriteAllText(file, """{"name":"forest","appearance":"dark","colors":{"background":"#101010"}}""");

		var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watcher = ThemeWatcher.Create(_root, _agentDir);
		watcher.Changed += () => fired.TrySetResult();

		File.WriteAllText(file, """{"name":"forest","appearance":"dark","colors":{"background":"#202020"}}""");

		var completed = await Task.WhenAny(fired.Task, Task.Delay(5_000)).ConfigureAwait(false);
		Assert.Same(fired.Task, completed);
	}

	[Fact]
	public async Task NewlyCreatedThemesDirectoryTriggersCallback()
	{
		var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watcher = ThemeWatcher.Create(_root, _agentDir);
		watcher.Changed += () => fired.TrySetResult();

		Directory.CreateDirectory(Path.Combine(_agentDir, "themes"));

		var completed = await Task.WhenAny(fired.Task, Task.Delay(5_000)).ConfigureAwait(false);
		Assert.Same(fired.Task, completed);
	}

	[Fact]
	public void MissingDirectoriesAreTolerated()
	{
		// 没有任何主题目录：创建可用但空转的监视器，不抛异常。
		using var watcher = ThemeWatcher.Create(Path.Combine(_root, "absent"), Path.Combine(_root, "absent-agent"));
		Assert.NotNull(watcher);
	}
}
