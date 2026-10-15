using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PiWeft.Desktop;

/// <summary>
/// 进程内原生 X11 PRIMARY 选区：自己充当 selection owner，不依赖 xclip/xsel 等外部程序。
/// 独立 Display 连接 + 专用事件线程；用 eventfd 投递唤醒并 poll 阻塞等待，空闲时不轮询；
/// 超过单个请求块的文本按 ICCCM INCR 属性协议分块传输。只负责 PRIMARY；CLIPBOARD 仍由框架负责。
/// </summary>
internal sealed partial class X11PrimarySelection : IPrimarySelection, IDisposable
{
	private const string LibX11 = "libX11.so.6";
	private const string LibC = "libc";
	// LP64 上 XEvent 联合体为 192 字节；XNextEvent 始终写入整个联合体。
	private const int EventBufferSize = 192;
	private const int PropertyNotifyType = 28;
	private const int SelectionClearType = 29;
	private const int SelectionRequestType = 30;
	private const int SelectionNotifyType = 31;
	private const long PropertyChangeMask = 1L << 22;
	private const int PropertyNewValue = 0;
	private const int PropertyDelete = 1;
	// 预定义原子：XA_ATOM = 4，XA_STRING = 31。
	private const int AtomAtom = 4;
	private const int StringAtom = 31;
	private const int PropertyModeReplace = 0;
	private const int AnyPropertyType = 0;
	private const int Success = 0;
	private const int PublishDelayMs = 140;
	// 连接未报告最大请求大小时使用的保守回退值。
	private const int FallbackRequestSize = 256 * 1024;
	private const int MinChunkBytes = 16 * 1024;
	private const int MaxChunkBytes = 1024 * 1024;
	private const int PollIn = 0x001;
	private const int EventFdNonBlock = 0x800;
	private const int EventFdCloseOnExec = 0x80000;
	private const int Interrupted = 4;
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

	private readonly nint _display;
	private readonly int _connection;
	private readonly int _wake;
	private readonly nint _ownerWindow;
	private readonly nint _requestWindow;
	private readonly nint _primaryAtom;
	private readonly nint _utf8Atom;
	private readonly nint _targetsAtom;
	private readonly nint _propertyAtom;
	private readonly nint _incrAtom;
	private readonly int _chunkBytes;
	private readonly Thread _pump;
	private readonly ConcurrentQueue<Action> _queue = new();
	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _reader = new(1);
	// 仅在事件线程访问：登记中的出站分块传输，按 requestor 窗口索引。
	private readonly Dictionary<nint, Outgoing> _outgoing = new();
	private MemoryStream? _incoming;
	private nint _incomingProperty;
	private byte[] _owned = [];
	private int _owns;
	private CancellationTokenSource? _publish;
	private TaskCompletionSource<string?>? _pending;
	private bool _disposed;

	public Task PendingPublish { get; private set; } = Task.CompletedTask;

	/// <summary>单个 X 请求块的字节上限；超过它的文本走 ICCCM INCR。测试用它确认大数据确实分块。</summary>
	internal int TransferChunkBytes => _chunkBytes;

	private X11PrimarySelection()
	{
		_display = XOpenDisplay(0);
		if (_display == 0) throw new InvalidOperationException("XOpenDisplay failed");
		_wake = -1;
		try
		{
			_primaryAtom = XInternAtom(_display, "PRIMARY", 0);
			_utf8Atom = XInternAtom(_display, "UTF8_STRING", 0);
			_targetsAtom = XInternAtom(_display, "TARGETS", 0);
			_propertyAtom = XInternAtom(_display, "PIWEFT_PRIMARY", 0);
			_incrAtom = XInternAtom(_display, "INCR", 0);
			var root = XDefaultRootWindow(_display);
			_ownerWindow = XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, 0, 0);
			_requestWindow = XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, 0, 0);
			if (_primaryAtom == 0 || _utf8Atom == 0 || _incrAtom == 0 || _propertyAtom == 0 ||
				root == 0 || _ownerWindow == 0 || _requestWindow == 0)
				throw new InvalidOperationException("X11 PRIMARY resources unavailable");
			// 只在自己的窗口上订阅属性变化；服务 INCR 时再单独订阅 requestor 窗口。
			XSelectInput(_display, _requestWindow, PropertyChangeMask);
			// ICCCM：分块大小相对握手得到的 maximum-request-size，而不是固定常数。
			var requestSize = XExtendedMaxRequestSize(_display);
			if (requestSize <= 0) requestSize = FallbackRequestSize;
			_chunkBytes = (int)Math.Clamp(requestSize / 2, MinChunkBytes, MaxChunkBytes);
			_wake = eventfd(0, EventFdCloseOnExec | EventFdNonBlock);
			if (_wake < 0) throw new InvalidOperationException("eventfd failed");
			_connection = XConnectionNumber(_display);
			XFlush(_display);
		}
		catch
		{
			if (_wake >= 0) close(_wake);
			XCloseDisplay(_display);
			throw;
		}
		_pump = new Thread(Pump) { IsBackground = true, Name = "piweft-primary" };
		_pump.Start();
	}

	/// <summary>仅在 X11/XWayland 会话可用时返回实例；失败时调用方回退到外部程序。</summary>
	public static X11PrimarySelection? Create()
	{
		if (!OperatingSystem.IsLinux()) return null;
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
		try { return new X11PrimarySelection(); }
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
		{
			return null;
		}
	}

	public void Publish(string text)
	{
		lock (_gate)
		{
			if (_disposed) return;
			_publish?.Cancel();
			// 清空选区保留上一次 PRIMARY，只是取消尚未落地的中间拖选。
			if (text.Length == 0 || text.Length > LinuxPrimarySelection.MaxCharacters) return;
			var cancellation = new CancellationTokenSource();
			_publish = cancellation;
			PendingPublish = PublishAsync(text, cancellation);
		}
	}

	private async Task PublishAsync(string text, CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(PublishDelayMs, cancellation.Token).ConfigureAwait(false);
			var bytes = Encoding.UTF8.GetBytes(text);
			if (bytes.Length > LinuxPrimarySelection.MaxBytes) return;
			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Post(() =>
			{
				if (_disposed || cancellation.IsCancellationRequested) { completion.TrySetResult(); return; }
				lock (_gate) { _owned = bytes; _owns = 1; }
				XSetSelectionOwner(_display, _primaryAtom, _ownerWindow, 0);
				XFlush(_display);
				completion.TrySetResult();
			});
			await completion.Task.ConfigureAwait(false);
		}
		catch (OperationCanceledException) { }
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_publish, cancellation)) _publish = null;
				cancellation.Dispose();
			}
		}
	}

	public async Task<string?> ReadAsync()
	{
		if (_disposed) return null;
		await PendingPublish.ConfigureAwait(false);
		if (_disposed) return null;
		await _reader.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_disposed) return null;
			lock (_gate)
				if (_owns == 1) return _owned.Length == 0 ? null : Encoding.UTF8.GetString(_owned);
			var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			Post(() => BeginConversion(pending));
			using var timeout = new CancellationTokenSource(ReadTimeout);
			var expiry = Task.Delay(Timeout.Infinite, timeout.Token);
			if (await Task.WhenAny(pending.Task, expiry).ConfigureAwait(false) != pending.Task)
			{
				// 迟到的 SelectionNotify / PropertyNotify 由事件线程按未登记状态丢弃。
				lock (_gate) if (ReferenceEquals(_pending, pending)) _pending = null;
				return null;
			}
			return await pending.Task.ConfigureAwait(false);
		}
		finally
		{
			_reader.Release();
		}
	}

	/// <summary>投递到事件线程并唤醒它；唤醒信号重复时 eventfd 计数器合并，不会丢失。</summary>
	private void Post(Action operation)
	{
		_queue.Enqueue(operation);
		Wake();
	}

	private unsafe void Wake()
	{
		ulong signal = 1;
		// 计数器饱和时 write 返回 EAGAIN，说明唤醒信号已存在，忽略即可。
		_ = write(_wake, (nint)(&signal), 8);
	}

	private unsafe void DrainWake()
	{
		ulong value;
		while (read(_wake, (nint)(&value), 8) == 8) { }
	}

	private unsafe void Pump()
	{
		var buffer = Marshal.AllocHGlobal(EventBufferSize);
		var fds = stackalloc PollFd[2];
		try
		{
			while (true)
			{
				while (XPending(_display) > 0)
				{
					XNextEvent(_display, buffer);
					Dispatch(buffer);
				}
				while (_queue.TryDequeue(out var operation)) operation();
				if (_disposed) break;
				fds[0] = new PollFd(_connection, PollIn, 0);
				fds[1] = new PollFd(_wake, PollIn, 0);
				// 阻塞直到 X 连接可读或有跨线程投递；空闲时不消耗 CPU。
				if (poll(fds, 2, -1) < 0 && Marshal.GetLastPInvokeError() != Interrupted) break;
				if ((fds[1].Revents & PollIn) != 0) DrainWake();
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
		{
			if (!_disposed) throw;
		}
		finally
		{
			XDestroyWindow(_display, _ownerWindow);
			XDestroyWindow(_display, _requestWindow);
			XCloseDisplay(_display);
			Marshal.FreeHGlobal(buffer);
		}
	}

	private void Dispatch(nint buffer)
	{
		switch (Marshal.ReadInt32(buffer, 0))
		{
			case SelectionRequestType: ServeRequest(buffer); break;
			case SelectionNotifyType: CompleteConversion(buffer); break;
			case PropertyNotifyType: HandlePropertyNotify(buffer); break;
			case SelectionClearType: lock (_gate) { _owns = 0; _owned = []; } break;
		}
	}

	private unsafe void ServeRequest(nint buffer)
	{
		var request = ReadEvent<SelectionRequestEvent>(buffer);
		// 旧客户端可能不填 property，此时用 target 当 property。
		var property = request.Property != 0 ? request.Property : request.Target;
		nint served = 0;
		if (request.Selection == _primaryAtom)
		{
			if (request.Target == _targetsAtom)
			{
				ReadOnlySpan<nint> targets = [_utf8Atom, StringAtom];
				fixed (nint* data = targets)
					XChangeProperty(_display, request.Requestor, property, AtomAtom, 32, PropertyModeReplace, (nint)data, targets.Length);
				served = property;
			}
			else if (request.Target == _utf8Atom || request.Target == StringAtom)
			{
				byte[] owned;
				lock (_gate) owned = _owned;
				if (owned.Length > _chunkBytes)
				{
					// 超过一个请求块：按 ICCCM 用 INCR 应答，属性值为字节数下界，随后由该窗口的
					// PropertyNotify 驱动分块追加。ICCCM 原文用 Append mode，但 requestor 每读一块
					// 即删除同名属性，Replace 与 Append 在此等价，且能容忍不删除属性的客户端。
					XSelectInput(_display, request.Requestor, PropertyChangeMask);
					var lowerBound = owned.Length;
					XChangeProperty(_display, request.Requestor, property, _incrAtom, 32, PropertyModeReplace, (nint)(&lowerBound), 1);
					_outgoing[request.Requestor] = new Outgoing(property, owned);
					served = property;
				}
				else
				{
					fixed (byte* data = owned)
						XChangeProperty(_display, request.Requestor, property, request.Target, 8, PropertyModeReplace, (nint)data, owned.Length);
					served = property;
				}
			}
		}
		SendNotify(request.Requestor, request.Selection, request.Target, served, request.Time);
		XFlush(_display);
	}

	/// <summary>仅用于构造 SelectionNotify；字段偏移按 LP64 XSelectionEvent 布局。</summary>
	private unsafe void SendNotify(nint requestor, nint selection, nint target, nint property, nint time)
	{
		Span<byte> buffer = stackalloc byte[EventBufferSize];
		buffer.Clear();
		var notify = new SelectionNotifyEvent
		{
			Type = SelectionNotifyType, Serial = 0, SendEvent = 1, Display = _display,
			Requestor = requestor, Selection = selection, Target = target, Property = property, Time = time,
		};
		MemoryMarshal.Write(buffer, in notify);
		fixed (byte* data = buffer) XSendEvent(_display, requestor, 0, 0, (nint)data);
	}

	private void BeginConversion(TaskCompletionSource<string?> pending)
	{
		// 上一轮超时或失败可能留下未收尾的分块状态。
		_incoming = null;
		lock (_gate)
		{
			if (_disposed) { pending.TrySetResult(null); return; }
			_pending = pending;
		}
		if (XGetSelectionOwner(_display, _primaryAtom) == 0)
		{
			// 无 owner 时不必等服务器回应，直接短路并解除登记。
			ResolveConversion(null);
			return;
		}
		XConvertSelection(_display, _primaryAtom, _utf8Atom, _propertyAtom, _requestWindow, 0);
		XFlush(_display);
	}

	private unsafe void CompleteConversion(nint buffer)
	{
		var notify = ReadEvent<SelectionNotifyEvent>(buffer);
		if (notify.Requestor != _requestWindow) return;
		if (notify.Property == 0) { ResolveConversion(null); return; }
		if (PropertyType(_requestWindow, notify.Property) == _incrAtom)
		{
			// ICCCM：requestor 删除 INCR 属性即启动传输，随后由 PropertyNotify 驱动接收。
			XDeleteProperty(_display, _requestWindow, notify.Property);
			XFlush(_display);
			_incoming = new MemoryStream();
			_incomingProperty = notify.Property;
			return;
		}
		if (!TakeProperty(_requestWindow, notify.Property, LinuxPrimarySelection.MaxBytes, out var data, out var length)) { ResolveConversion(null); return; }
		ResolveConversion(length > 0 ? Encoding.UTF8.GetString(data, 0, length) : null);
	}

	private unsafe void HandlePropertyNotify(nint buffer)
	{
		var notify = ReadEvent<PropertyNotifyEvent>(buffer);
		if (notify.Window == _requestWindow) ReceiveChunk(notify.Atom, notify.State);
		else SendChunk(notify.Window, notify.Atom, notify.State);
	}

	/// <summary>接收端：owner 追加了一块数据（或零长度结束标志）。</summary>
	private void ReceiveChunk(nint property, int state)
	{
		if (state != PropertyNewValue || _incoming is null || property != _incomingProperty) return;
		lock (_gate) if (_pending is null) return;
		if (!TakeProperty(_requestWindow, property, LinuxPrimarySelection.MaxBytes, out var data, out var length)) { ResolveConversion(null); _incoming = null; return; }
		if (length == 0)
		{
			// 零长度属性是 ICCCM 定义的结束标志。
			var stream = _incoming;
			_incoming = null;
			if (stream.Length == 0) { ResolveConversion(null); return; }
			ResolveConversion(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
			stream.Dispose();
			return;
		}
		if (_incoming.Length + length > LinuxPrimarySelection.MaxBytes) { ResolveConversion(null); _incoming = null; return; }
		_incoming.Write(data, 0, length);
	}

	/// <summary>服务端：requestor 读走了一块，追加下一块或写结束标志。</summary>
	private unsafe void SendChunk(nint requestor, nint property, int state)
	{
		if (state != PropertyDelete) return;
		if (!_outgoing.TryGetValue(requestor, out var transfer) || transfer.Property != property) return;
		if (transfer.Offset >= transfer.Data.Length)
		{
			XChangeProperty(_display, requestor, property, _utf8Atom, 8, PropertyModeReplace, 0, 0);
			_outgoing.Remove(requestor);
		}
		else
		{
			var count = Math.Min(transfer.Data.Length - transfer.Offset, _chunkBytes);
			fixed (byte* data = transfer.Data)
				XChangeProperty(_display, requestor, property, _utf8Atom, 8, PropertyModeReplace, (nint)(data + transfer.Offset), count);
			transfer.Offset += count;
		}
		XFlush(_display);
	}

	private void ResolveConversion(string? text)
	{
		TaskCompletionSource<string?>? pending;
		lock (_gate) { pending = _pending; _pending = null; }
		pending?.TrySetResult(text);
	}

	/// <summary>只取属性类型，不读取也不删除内容。</summary>
	private unsafe nint PropertyType(nint window, nint property)
	{
		if (XGetWindowProperty(_display, window, property, 0, 0, 0, AnyPropertyType,
			out var type, out _, out _, out _, out var data) != Success) return 0;
		if (data != 0) XFree(data);
		return type;
	}

	/// <summary>读取并删除属性内容；长度为零是 INCR 的结束标志，不是失败。</summary>
	private unsafe bool TakeProperty(nint window, nint property, int maxBytes, out byte[] content, out int length)
	{
		content = [];
		length = 0;
		if (XGetWindowProperty(_display, window, property, 0, maxBytes / 4 + 1, 1, AnyPropertyType,
			out _, out var format, out var count, out var remaining, out var data) != Success) return false;
		try
		{
			if (format != 8 || remaining != 0) return false;
			length = (int)count;
			if (length < 0 || length > maxBytes) return false;
			if (data != 0 && length > 0)
			{
				content = new byte[length];
				Marshal.Copy(data, content, 0, length);
			}
			return true;
		}
		finally
		{
			if (data != 0) XFree(data);
		}
	}

	private static unsafe T ReadEvent<T>(nint buffer) where T : unmanaged => Unsafe.Read<T>((void*)buffer);

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_publish?.Cancel();
		}
		// 唤醒事件线程，让它自行销毁 X 资源（所有 X 调用都必须在该线程上）。
		Post(static () => { });
		if (_pump.Join(ShutdownTimeout)) close(_wake);
	}

	private sealed class Outgoing
	{
		public Outgoing(nint property, byte[] data) { Property = property; Data = data; }
		public nint Property { get; }
		public byte[] Data { get; }
		public int Offset { get; set; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollFd
	{
		public int Fd;
		public short Events;
		public short Revents;
		public PollFd(int fd, short events, short revents) { Fd = fd; Events = events; Revents = revents; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SelectionRequestEvent
	{
		public int Type;
		public nint Serial;
		public int SendEvent;
		public nint Display;
		public nint Owner;
		public nint Requestor;
		public nint Selection;
		public nint Target;
		public nint Property;
		public nint Time;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SelectionNotifyEvent
	{
		public int Type;
		public nint Serial;
		public int SendEvent;
		public nint Display;
		public nint Requestor;
		public nint Selection;
		public nint Target;
		public nint Property;
		public nint Time;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PropertyNotifyEvent
	{
		public int Type;
		public nint Serial;
		public int SendEvent;
		public nint Display;
		public nint Window;
		public nint Atom;
		public nint Time;
		public int State;
	}

	[LibraryImport(LibC, SetLastError = true)]
	private static partial int eventfd(uint initialValue, int flags);

	[LibraryImport(LibC, SetLastError = true)]
	private static unsafe partial int poll(PollFd* descriptors, nuint count, int timeout);

	[LibraryImport(LibC, SetLastError = true)]
	private static partial nint read(int fd, nint buffer, nuint count);

	[LibraryImport(LibC, SetLastError = true)]
	private static partial nint write(int fd, nint buffer, nuint count);

	[LibraryImport(LibC, SetLastError = true)]
	private static partial int close(int fd);

	[LibraryImport(LibX11)]
	private static partial nint XOpenDisplay(nint name);

	[LibraryImport(LibX11)]
	private static partial int XCloseDisplay(nint display);

	[LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

	[LibraryImport(LibX11)]
	private static partial nint XDefaultRootWindow(nint display);

	[LibraryImport(LibX11)]
	private static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nint border, nint background);

	[LibraryImport(LibX11)]
	private static partial int XDestroyWindow(nint display, nint window);

	[LibraryImport(LibX11)]
	private static partial int XSelectInput(nint display, nint window, long eventMask);

	[LibraryImport(LibX11)]
	private static partial int XConnectionNumber(nint display);

	[LibraryImport(LibX11)]
	private static partial nint XExtendedMaxRequestSize(nint display);

	[LibraryImport(LibX11)]
	private static partial int XSetSelectionOwner(nint display, nint selection, nint owner, nint time);

	[LibraryImport(LibX11)]
	private static partial nint XGetSelectionOwner(nint display, nint selection);

	[LibraryImport(LibX11)]
	private static partial int XConvertSelection(nint display, nint selection, nint target, nint property, nint requestor, nint time);

	[LibraryImport(LibX11)]
	private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, nint data, int elements);

	[LibraryImport(LibX11)]
	private static partial int XDeleteProperty(nint display, nint window, nint property);

	[LibraryImport(LibX11)]
	private static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete, nint requiredType,
		out nint actualType, out int actualFormat, out nint itemCount, out nint bytesAfter, out nint data);

	[LibraryImport(LibX11)]
	private static partial int XSendEvent(nint display, nint window, int propagate, nint eventMask, nint eventSend);

	[LibraryImport(LibX11)]
	private static partial int XFlush(nint display);

	[LibraryImport(LibX11)]
	private static partial int XPending(nint display);

	[LibraryImport(LibX11)]
	private static partial int XNextEvent(nint display, nint eventReturn);

	[LibraryImport(LibX11)]
	private static partial int XFree(nint data);
}
