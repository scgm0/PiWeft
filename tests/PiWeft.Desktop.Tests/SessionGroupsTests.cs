using PiWeft.Cli;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

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
	public void SubagentSessionsNestUnderTheirParent()
	{
		var cwd = Path.Combine(Path.GetTempPath(), "piweft-nest");
		var parent = Item("parent", cwd);
		var child = Item("child", cwd) with { ParentSession = "parent", Origin = "subagent" };
		var grandchild = Item("grand", cwd) with { ParentSession = "child", Origin = "subagent" };
		var sibling = Item("sibling", cwd);

		var tree = SessionGroups.Nest([parent, sibling, child, grandchild]);

		Assert.Equal(["parent", "sibling"], tree.Select(static entry => entry.Item.Id));
		var parentNode = Assert.Single(tree, static entry => entry.Item.Id == "parent");
		var childNode = Assert.Single(parentNode.Children);
		Assert.Equal("child", childNode.Item.Id);
		Assert.Equal("grand", Assert.Single(childNode.Children).Item.Id);
	}

	[Fact]
	public void ParentSessionPathAlsoNestsUnderTheParent()
	{
		var cwd = Path.Combine(Path.GetTempPath(), "piweft-nest");
		var parent = Item("parent", cwd);
		var child = Item("child", cwd) with { ParentSession = parent.Path, Origin = "subagent" };

		var entry = Assert.Single(SessionGroups.Nest([parent, child]));
		Assert.Equal("parent", entry.Item.Id);
		Assert.Equal("child", Assert.Single(entry.Children).Item.Id);
	}

	[Fact]
	public void ChildWithoutItsParentStaysTopLevel()
	{
		var cwd = Path.Combine(Path.GetTempPath(), "piweft-nest");
		var orphan = Item("child", cwd) with { ParentSession = "missing", Origin = "subagent" };

		var entry = Assert.Single(SessionGroups.Nest([orphan]));
		Assert.Equal("child", entry.Item.Id);
		Assert.Empty(entry.Children);
	}

	[Fact]
	public void NestBreaksParentCyclesInsteadOfRecursing()
	{
		var cwd = Path.Combine(Path.GetTempPath(), "piweft-nest");
		var a = Item("a", cwd) with { ParentSession = "b", Origin = "subagent" };
		var b = Item("b", cwd) with { ParentSession = "a", Origin = "subagent" };

		var entry = Assert.Single(SessionGroups.Nest([a, b]));
		Assert.Equal("a", entry.Item.Id);
		Assert.Equal("b", Assert.Single(entry.Children).Item.Id);
		Assert.Empty(entry.Children[0].Children);
	}

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

	[Fact]
	public void ProjectPathDropsTrailingSeparatorsAndRelativeSegments()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-project-path");

		Assert.Equal(root, SessionGroups.ProjectPath(root + Path.DirectorySeparatorChar));
		Assert.Equal(root, SessionGroups.ProjectPath(Path.Combine(root, "..", "piweft-project-path")));
		Assert.True(SessionGroups.SamePath(root, root + Path.DirectorySeparatorChar + "."));
	}

	[Fact]
	public void ProjectPathKeepsTheFileSystemRoot()
	{
		var root = Path.GetPathRoot(Path.GetTempPath())!;

		Assert.Equal(root, SessionGroups.ProjectPath(root));
		Assert.Equal(root, SessionGroups.ProjectPath(root + Path.DirectorySeparatorChar));
	}

	[Fact]
	public void AddedFolderWithoutSessionsStillGetsARow()
	{
		var empty = Path.Combine(Path.GetTempPath(), "piweft-empty-project");

		var group = Assert.Single(SessionGroups.ProjectGroups([], string.Empty, [empty], []));

		Assert.Equal(SessionGroups.ProjectPath(empty), group.Cwd);
		Assert.Empty(group.Sessions);
	}

	[Fact]
	public void TheSameFolderIsOneRowHoweverItIsSpelled()
	{
		var project = Path.Combine(Path.GetTempPath(), "piweft-project");
		var session = Item("project-1", project);

		var group = Assert.Single(SessionGroups.ProjectGroups(
			[session], string.Empty, [project + Path.DirectorySeparatorChar], []));

		Assert.Equal(["project-1"], group.Sessions.Select(static item => item.Id));
	}

	[Fact]
	public void RemovedProjectsStayHiddenEvenWhenSessionsRemainOnDisk()
	{
		var removed = Path.Combine(Path.GetTempPath(), "piweft-removed");
		var kept = Path.Combine(Path.GetTempPath(), "piweft-kept");

		var group = Assert.Single(SessionGroups.ProjectGroups(
			[Item("removed-1", removed), Item("kept-1", kept)],
			string.Empty,
			[removed, kept],
			[removed]));

		Assert.Equal(SessionGroups.ProjectPath(kept), group.Cwd);
		Assert.Equal(["kept-1"], group.Sessions.Select(static item => item.Id));
	}

	[Fact]
	public void ProjectsWithSessionsSortBeforeAddedFoldersWithoutThem()
	{
		var empty = Path.Combine(Path.GetTempPath(), "piweft-empty");
		var active = Path.Combine(Path.GetTempPath(), "piweft-active");

		var groups = SessionGroups.ProjectGroups([Item("s", active)], string.Empty, [empty], []);

		Assert.Equal(
			[SessionGroups.ProjectPath(active), SessionGroups.ProjectPath(empty)],
			groups.Select(static group => group.Cwd));
	}
}
