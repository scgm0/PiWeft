using Aprillz.MewUI.Controls;
using PiWeft.Agent;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 输入区上方的团队任务条：任务标题、状态与 owner。
[Collection("mewui-controls")]
public sealed class TeamTasksCardTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"piweft-team-card-" + Guid.NewGuid().ToString("N")[..8]);

	public TeamTasksCardTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void CardListsTasksWithStatusAndOwner()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "结论。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});

		var board = new TeamBoard(session, static name => name is "lead" or "worker-1");
		Assert.True(board.Create("lead", "写登录流程", "改 Auth.cs 并跑测试。", null, ["src/Auth.cs"]).Ok);
		var task = Assert.Single(board.Tasks);
		Assert.True(board.Update("worker-1", task.Id, task.Revision, "claim").Ok);
		Assert.True(board.Create("lead", "补回归测试", "加一个失败用例。", null, null).Ok);

		var card = new TeamTasksCard();
		card.Render(board);
		ConversationPerformanceTests.Layout(card.View, 800, 600);

		try
		{
			Assert.True(card.IsShown);
			var blocks = Find<TextBlock>(card.View).ToArray();
			Assert.Contains(blocks, block => block.Text == "写登录流程");
			Assert.Contains(blocks, block => block.Text == "补回归测试");
			Assert.Contains(blocks, block => block.Text.Contains("进行中 · worker-1", StringComparison.Ordinal));
			Assert.Contains(blocks, block => block.Text.Contains("待领取 · 待领取", StringComparison.Ordinal));

			card.ToggleForTests("task-1");
			ConversationPerformanceTests.Layout(card.View, 800, 600);
			var expanded = Find<TextBlock>(card.View).ToArray();
			Assert.Contains(expanded, block => block.Text.Contains("改 Auth.cs", StringComparison.Ordinal));
			Assert.Contains(expanded, block => block.Text.Contains(Strings.TeamTaskRevision(2), StringComparison.Ordinal));
			Assert.Contains(expanded, block => block.Text.Contains(Strings.TeamTaskWriteScopes("src/Auth.cs"), StringComparison.Ordinal));
			Assert.Contains(expanded, block => block.Text == Strings.TeamTaskCopy);

			card.FilterOwnerForTests("worker-1");
			ConversationPerformanceTests.Layout(card.View, 800, 600);
			var filtered = Find<TextBlock>(card.View).ToArray();
			Assert.Contains(filtered, block => block.Text == "写登录流程");
			Assert.DoesNotContain(filtered, block => block.Text == "补回归测试");

			card.FilterOwnerForTests("nobody");
			ConversationPerformanceTests.Layout(card.View, 800, 600);
			Assert.Contains(
				Find<TextBlock>(card.View),
				block => block.Text == Strings.TeamTasksOwnerEmpty("nobody"));

			card.FilterOwnerForTests(null);
			ConversationPerformanceTests.Layout(card.View, 800, 600);
			Assert.Contains(Find<TextBlock>(card.View), block => block.Text == "补回归测试");
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void CardHidesWhenThereIsNoBoardOrNoTask()
	{
		var card = new TeamTasksCard();
		card.Render(null);
		Assert.False(card.IsShown);

		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "结论。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});
		card.Render(new TeamBoard(session, static _ => true));
		Assert.False(card.IsShown);
	}
}
