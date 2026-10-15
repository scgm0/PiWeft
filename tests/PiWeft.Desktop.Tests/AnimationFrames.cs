using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI.Animation;

namespace PiWeft.Desktop.Tests;

/// <summary>Steps the real framework animation clock without sleeps or a running window.</summary>
internal static class AnimationFrames
{
	private static readonly object Manager = typeof(AnimationManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	private static readonly MethodInfo Update = typeof(AnimationManager).GetMethod("UpdateAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static long _timestamp = Stopwatch.GetTimestamp();

	public static void Advance(int milliseconds)
	{
		Update.Invoke(Manager, [_timestamp]);
		_timestamp += milliseconds * Stopwatch.Frequency / 1000;
		Update.Invoke(Manager, [_timestamp]);
	}
}
