using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI.Animation;

namespace PiWeft.Desktop.Tests;

/// <summary>Steps the real framework animation clock without sleeps or a running window.</summary>
internal static class AnimationFrames
{
	/// <summary>名义帧长（ms）：与真实 60fps 一致，但由用例自己决定，不再取决于机器负载。</summary>
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

	/// <summary>一帧 <see cref="FrameMs"/>。</summary>
	public static void Advance() => Advance(FrameMs);

	/// <summary>
	/// 把这个控制器的每帧 dt 钉成名义帧长。<see cref="SmoothScroll"/> 的曲线按真实流逝时间推进
	/// （故意不钳制 dt），所以用 <c>Thread.Sleep</c> 喂帧的用例在满载机器上会抖出几十毫秒的间隔，
	/// 「这段滚轮手势走了多少 DIP」就跟着抖成 flake。钉住之后同样的断言可以收到 ±2%。
	/// 只对用例生效：生产从不赋值，dt 仍是真实时间。
	/// </summary>
	public static void Pin(SmoothScroll scroll, int milliseconds = FrameMs) =>
		scroll.FrameDtSeconds = milliseconds / 1000.0;

	/// <summary>钉住并推进一帧（配合用例自己的布局节奏）。</summary>
	public static void Frame(SmoothScroll scroll, int milliseconds = FrameMs)
	{
		Pin(scroll, milliseconds);
		Advance(milliseconds);
	}
}
