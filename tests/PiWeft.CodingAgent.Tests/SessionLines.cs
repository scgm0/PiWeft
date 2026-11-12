using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 会话行是 union，断言前要先取出内容。直接 IsType 拿到的是 <see cref="SessionLine"/> 本身，
/// 不是里面的头部或条目。
internal static class SessionLines
{
	public static SessionEntry Entry(SessionLine? line) => Assert.IsAssignableFrom<SessionEntry>(line?.Entry);

	public static SessionHeader Header(SessionLine? line) => Assert.IsAssignableFrom<SessionHeader>(line?.Header);
}
