using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 子代理「多久以前」共用一秒心跳；与秒表一样，避免每个运行行一个 DispatcherTimer。
internal static class LiveActivityClock
{
	private static readonly List<LiveActivityText> Targets = [];
	private static DispatcherTimer? _timer;

	internal static void Register(LiveActivityText target)
	{
		if (target.IsTicking) return;
		Targets.Remove(target);
		target.MarkRegistered(true);
		Targets.Add(target);
		if (_timer is not null) return;
		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1_000));
		_timer.OnTick(Tick);
		_timer.Start();
	}

	internal static void Unregister(LiveActivityText target) => target.MarkRegistered(false);

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
