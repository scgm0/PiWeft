using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI.Animation;

namespace PiWeft.Desktop.Tests;

internal static class AnimationFrames
{
	internal const int FrameMs = 16;

	private static readonly object Manager = typeof(AnimationManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	private static readonly MethodInfo Update = typeof(AnimationManager).GetMethod("UpdateAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static long _timestamp = Stopwatch.GetTimestamp();

	public static void Advance(int milliseconds)
	{
		Update.Invoke(Manager, [_timestamp]);
		_timestamp += milliseconds * Stopwatch.Frequency / 1000;
		Update.Invoke(Manager, [_timestamp]);
	}

	public static void Advance() => Advance(FrameMs);

	/// 把这个控制器的每帧 dt 钉成名义帧长。<see cref="SmoothScroll"/> 的曲线按真实流逝时间推进
	/// （故意不钳制 dt），所以用 <c>Thread.Sleep</c> 喂帧的用例在满载机器上会抖出几十毫秒的间隔，
	/// 「这段滚轮手势走了多少 DIP」就跟着抖成 flake。钉住之后同样的断言可以收到 ±2%。
	/// 只对用例生效：生产从不赋值，dt 仍是真实时间。
	public static void Pin(SmoothScroll scroll, int milliseconds = FrameMs) =>
		scroll.FrameDtSeconds = milliseconds / 1000.0;

	public static void Frame(SmoothScroll scroll, int milliseconds = FrameMs)
	{
		Pin(scroll, milliseconds);
		Advance(milliseconds);
	}
}
