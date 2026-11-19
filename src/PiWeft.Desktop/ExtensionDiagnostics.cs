using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Extensions;

namespace PiWeft.Desktop;

/// 桌面进程内收集的扩展诊断。设置页没有活会话，扩展加载期与运行期的失败只能在会话处转存一份，
/// 否则用户只在日志里看得到，界面上什么都看不到。
internal static class ExtensionDiagnostics
{
	private const int Limit = 20;

	private static readonly List<Entry> Items = [];
	private static readonly Lock Gate = new();

	internal sealed record Entry(DateTimeOffset Timestamp, string ExtensionPath, string? Event, string Message);

	/// 取走会话里未上报的诊断并转存，返回新增条数。
	public static int Absorb(CodingAgentSession session)
	{
		var pending = session.TakeUnreportedExtensionDiagnostics();
		if (pending.Count == 0)
		{
			return 0;
		}

		lock (Gate)
		{
			foreach (var diagnostic in pending)
			{
				Items.Add(new Entry(DateTimeOffset.Now, diagnostic.ExtensionPath, diagnostic.Event, diagnostic.Message));
			}

			if (Items.Count > Limit)
			{
				Items.RemoveRange(0, Items.Count - Limit);
			}
		}

		return pending.Count;
	}

	public static IReadOnlyList<Entry> Snapshot()
	{
		lock (Gate)
		{
			return [.. Items];
		}
	}

	public static void Clear()
	{
		lock (Gate)
		{
			Items.Clear();
		}
	}

	public static int Count
	{
		get
		{
			lock (Gate)
			{
				return Items.Count;
			}
		}
	}
}
