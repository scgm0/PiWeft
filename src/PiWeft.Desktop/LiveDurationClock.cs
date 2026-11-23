using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 所有运行中步骤秒表共用一个 100ms 心跳。
/// 每个控件各建 DispatcherTimer 时，长会话里十几个步骤就有十几个定时器；
/// 这里只保留一个，Tick 时遍历仍在跑的控件，摘树的控件顺手从列表清掉。
internal static class LiveDurationClock
{
	private static readonly List<LiveDurationText> Targets = [];
	private static DispatcherTimer? _timer;

	internal static void Register(LiveDurationText target)
	{
		if (target.IsTicking) return;
		Targets.Remove(target);
		target.MarkRegistered(true);
		Targets.Add(target);
		if (_timer is not null) return;
		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100));
		_timer.OnTick(Tick);
		_timer.Start();
	}

	internal static void Unregister(LiveDurationText target)
	{
		target.MarkRegistered(false);
	}

	private static void Tick()
	{
		var any = false;
		for (var index = Targets.Count - 1; index >= 0; index--)
		{
			var target = Targets[index];
			if (!target.IsTicking)
			{
				Targets.RemoveAt(index);
				continue;
			}

			target.TickFromClock();
			any |= target.IsTicking;
			if (!target.IsTicking)
			{
				Targets.RemoveAt(index);
			}
		}

		if (any || Targets.Count > 0) return;
		_timer?.Stop();
		_timer?.Dispose();
		_timer = null;
	}
}
