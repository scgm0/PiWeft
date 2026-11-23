using Xunit;

namespace PiWeft.Desktop.Tests;

/// 窗口级会话活动：侧栏父行的「运行中 / 3 文件」。
public sealed class SessionActivityIndexTests
{
	private static ChatFileChange Change(string path) => new(path, 1, 2, 0, string.Empty);

	[Fact]
	public void FirstUpdateChangesAndRepeatingItDoesNot()
	{
		var index = new SessionActivityIndex();
		Assert.True(index.Update("session-1", false, [Change("/repo/a.cs")]));
		Assert.False(index.Update("session-1", false, [Change("/repo/a.cs")]));
	}

	[Fact]
	public void StreamingFlipsTheEntry()
	{
		var index = new SessionActivityIndex();
		index.Update("session-1", false, [Change("/repo/a.cs")]);

		Assert.True(index.Update("session-1", true, [Change("/repo/a.cs")]));
		Assert.True(index.Get("session-1") is { Streaming: true, ChangedFiles: 1 });
	}

	[Fact]
	public void RemoveDropsTheEntry()
	{
		var index = new SessionActivityIndex();
		index.Update("session-1", false, [Change("/repo/a.cs")]);

		Assert.True(index.Remove("session-1"));
		Assert.Null(index.Get("session-1"));
		Assert.False(index.Remove("session-1"));
	}

	[Fact]
	public void EmptySessionIdIsIgnored()
	{
		var index = new SessionActivityIndex();
		Assert.False(index.Update(string.Empty, true, []));
		Assert.Null(index.Get(string.Empty));
	}
}
