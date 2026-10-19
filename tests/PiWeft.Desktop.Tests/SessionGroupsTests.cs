using PiWeft.Cli;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>侧栏两个分段的归属：聊天目录里的会话只出现在「对话」，不会在「项目」里重复出现。</summary>
public class SessionGroupsTests
{
	private static SessionListItem Item(string id, string cwd) => new()
	{
		Path = Path.Combine(Path.GetTempPath(), id + ".jsonl"),
		Id = id,
		Cwd = cwd,
		Modified = DateTimeOffset.UtcNow,
	};

	[Fact]
	public void ChatAndProjectSessionsAreSplitByWorkingDirectory()
	{
		var chat = Path.Combine(Path.GetTempPath(), "piweft-chat-workspace");
		var project = Path.Combine(Path.GetTempPath(), "piweft-project");
		var sessions = new[] { Item("chat-1", chat), Item("project-1", project) };

		var chats = SessionGroups.Chats(sessions, chat);
		var projects = SessionGroups.Projects(sessions, chat);

		Assert.Equal(["chat-1"], chats.Select(static item => item.Id));
		Assert.Equal(["project-1"], projects.Select(static item => item.Id));
	}

	[Fact]
	public void TrailingSeparatorAndRelativeSegmentsStillCountAsTheSameDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-workspace");
		var chat = Path.Combine(root, "chat");
		var session = Item("chat-1", chat + Path.DirectorySeparatorChar);

		Assert.True(SessionGroups.IsChat(session, chat));
		Assert.True(SessionGroups.IsChat(Item("chat-2", Path.Combine(root, "chat", "..", "chat")), chat));
	}

	[Fact]
	public void EmptyChatWorkspaceNeverSwallowsSessions()
	{
		var session = Item("project-1", Path.Combine(Path.GetTempPath(), "piweft-project"));

		Assert.False(SessionGroups.IsChat(session, string.Empty));
		Assert.Empty(SessionGroups.Chats([session], string.Empty));
		Assert.Single(SessionGroups.Projects([session], string.Empty));
	}

	[Fact]
	public void SessionsWithoutWorkingDirectoryStayInProjects()
	{
		var chat = Path.Combine(Path.GetTempPath(), "piweft-chat-workspace");
		var session = Item("orphan", string.Empty);

		Assert.False(SessionGroups.IsChat(session, chat));
		Assert.Equal(["orphan"], SessionGroups.Projects([session], chat).Select(static item => item.Id));
	}
}
