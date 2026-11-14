using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// Windows 上的命令容器：每次 shell 调用建一个 Job，设 <c>KILL_ON_JOB_CLOSE</c>。
/// 句柄一关，Job 里所有进程一起死，被 reparent 到 explorer 的后代也在内。
/// taskkill /T 只能顺着还活着的父子链找，孤儿它看不见。
/// </summary>
internal sealed partial class WindowsJobObject : IDisposable
{
	/// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE。</summary>
	private const uint LimitKillOnJobClose = 0x2000;

	private const int ExtendedLimitInformationClass = 9;

	private nint _handle;

	private WindowsJobObject(nint handle) => _handle = handle;

	/// <summary>
	/// 给刚启动的 <paramref name="process"/> 建 Job 并放进去。失败返回 <c>null</c>，
	/// 调用方退回 taskkill 与整树 kill。
	/// </summary>
	public static WindowsJobObject? TryAssign(Process process)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		WindowsJobObject? job = null;
		try
		{
			var handle = CreateJobObjectW(nint.Zero, null);
			if (handle == nint.Zero)
			{
				return null;
			}

			job = new WindowsJobObject(handle);
			var limits = new JobObjectExtendedLimitInformation
			{
				BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = LimitKillOnJobClose },
			};
			if (!SetInformationJobObject(
				handle,
				ExtendedLimitInformationClass,
				ref limits,
				(uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
				// 子进程可能已经被父进程所在的 Job 套住；Windows 8 起允许嵌套。
				!AssignProcessToJobObject(handle, process.Handle))
			{
				job.Dispose();
				return null;
			}

			return job;
		}
		catch (Exception)
		{
			// 进程刚好退出、句柄已释放，或父 Job 不允许嵌套。没 Job 就只能靠 taskkill。
			job?.Dispose();
			return null;
		}
	}

	/// <summary>立刻终止 Job 里的所有进程（取消、超时）。句柄已关闭或调用失败时返回 <c>false</c>。</summary>
	public bool Terminate() => _handle != nint.Zero && TerminateJobObject(_handle, 1);

	/// <summary>关闭句柄。Job 里还有进程时，<c>KILL_ON_JOB_CLOSE</c> 会把它们一起杀掉。</summary>
	public void Dispose()
	{
		var handle = Interlocked.Exchange(ref _handle, nint.Zero);
		if (handle != nint.Zero)
		{
			_ = CloseHandle(handle);
		}
	}

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetInformationJobObject(
		nint job,
		int informationClass,
		ref JobObjectExtendedLimitInformation information,
		uint informationLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AssignProcessToJobObject(nint job, nint process);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TerminateJobObject(nint job, uint exitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		// 只为与 Win32 结构对齐；Job 不用这些计数。
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}
}
