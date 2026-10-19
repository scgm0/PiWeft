using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>运行中的 Split Send 菜单依赖这两条队列入口。</summary>
public sealed class AgentQueueTests
{
	[Fact]
	public void SteeringAndFollowUpLandInTheirOwnQueues()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var chat = new ChatViewModel();
		chat.Attach(session);

		Assert.True(chat.Steer("先看左边"));
		Assert.True(chat.FollowUp("下一轮再看右边"));

		var (steering, followUp) = session.PendingQueues();
		Assert.Single(steering);
		Assert.Single(followUp);
	}
}
