using System.Net.Sockets;
using System.Text;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// 保证每个用户只有一个实例：第二次启动会立即退出（并尽量让已运行的实例把窗口带到前台），
/// 不让两个窗口同时编辑同一批会话。
/// Unix 使用 agent 目录下的 socket：已有实例持有时绑定会失败（<c>EADDRINUSE</c>），该 socket 同时充当激活通道；
/// Windows 没有这种 socket，改用命名互斥体。
public sealed class SingleInstance : IDisposable
{
	// 继续与改名前的桌面实例共用锁，防止新旧程序同时编辑同一个项目。
	private const string MutexName = @"Local\piweft-desktop-single-instance";
	private const string ActivateMessage = "activate";
	private const string SocketPrefix = "piweft-desktop-";
	// 改名前的版本留下这个名字的 socket，新版本从不访问它，只有清扫能回收。
	private const string LegacySocketPrefix = "pi-desktop-";

	private readonly Mutex? _mutex;
	private readonly Socket? _socket;
	private readonly string? _socketPath;
	private readonly CancellationTokenSource _listenCancellation = new();
	private bool _disposed;

	private SingleInstance(Mutex? mutex, Socket? socket, string? socketPath)
	{
		_mutex = mutex;
		_socket = socket;
		_socketPath = socketPath;
	}

	public bool IsFirstInstance { get; private init; }

	public static SingleInstance Acquire(string instanceKey)
	{
		if (OperatingSystem.IsWindows())
		{
			var mutex = new Mutex(initiallyOwned: true, MutexName + "-" + instanceKey, out var created);
			return new SingleInstance(mutex, socket: null, socketPath: null) { IsFirstInstance = created };
		}

		var path = SocketPath(instanceKey);
		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				socket.Bind(new UnixDomainSocketEndPoint(path));
				socket.Listen(1);
				return new SingleInstance(mutex: null, socket, path) { IsFirstInstance = true };
			}
			catch (SocketException)
			{
				// 路径已存在：可能是活着的实例，也可能是被强杀的进程留下的 socket 文件；
				// 只有连接失败才能证明是后者，因为对残留文件绑定也一定失败。
				if (IsLive(path))
				{
					socket.Dispose();
					return new SingleInstance(mutex: null, socket: null, path) { IsFirstInstance = false };
				}

				TryDelete(path);
			}
		}

		socket.Dispose();
		return new SingleInstance(mutex: null, socket: null, path) { IsFirstInstance = false };
	}

	public static void SignalRunningInstance(string instanceKey)
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 互斥体只能报告归属，无法传递消息
		}

		try
		{
			using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			client.Connect(new UnixDomainSocketEndPoint(SocketPath(instanceKey)));
			client.Send(Encoding.UTF8.GetBytes(ActivateMessage));
		}
		catch (SocketException)
		{
			// 另一个实例正在退出，无需激活。
		}
	}

	/// <summary>删掉 agent 目录里没人监听的 socket 文件，返回删除个数。</summary>
	/// <remarks>
	/// 进程被强杀或崩溃时 <see cref="Dispose"/> 不会执行，socket 文件就留在目录里。
	/// 每个工作目录一个键，临时目录跑出来的那些永远等不到下一次启动，只能在这里统一回收。
	/// </remarks>
	public static int CleanupStaleSockets()
	{
		if (OperatingSystem.IsWindows())
		{
			return 0; // Windows 用互斥体，没有 socket 文件
		}

		var directory = SessionPaths.GetAgentDir();
		if (!Directory.Exists(directory))
		{
			return 0;
		}

		var removed = 0;
		foreach (var prefix in new[] { SocketPrefix, LegacySocketPrefix })
		{
			string[] candidates;
			try
			{
				candidates = Directory.GetFiles(directory, prefix + "*.sock");
			}
			catch (IOException)
			{
				continue; // 目录正在被别的实例改写，下次启动再清
			}

			foreach (var path in candidates)
			{
				if (!IsLive(path) && TryDelete(path))
				{
					removed++;
				}
			}
		}

		return removed;
	}

	public void Listen(Action onActivate)
	{
		if (_socket is null)
		{
			return;
		}

		var cancellation = _listenCancellation.Token;
		_ = Task.Run(async () =>
		{
			while (!cancellation.IsCancellationRequested)
			{
				try
				{
					using var accepted = await _socket.AcceptAsync(cancellation).ConfigureAwait(false);
					var buffer = new byte[64];
					var read = await accepted.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
					if (read > 0 && Encoding.UTF8.GetString(buffer, 0, read).StartsWith(ActivateMessage, StringComparison.Ordinal))
					{
						onActivate();
					}
				}
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
				{
					return;
				}
				catch (ObjectDisposedException)
				{
					return; // socket 已释放
				}
				catch (Exception ex)
				{
					// 一个客户端用 RST 断开或 onActivate 抛错，不该让整条激活通道死掉。
					AppLog.Warning(AppLog.For<SingleInstance>(), "Single-instance listener", ex);
				}
			}
		});
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_listenCancellation.Cancel();
		_socket?.Dispose();
		_mutex?.Dispose();

		// 只有真正持有监听 socket 的实例才能删文件：非首实例的路径指向正在运行的实例，
		// 它退出时删掉文件会让第二次启动把那个实例误判成残留，单实例保证随之失效。
		if (_socket is not null && _socketPath is not null && File.Exists(_socketPath))
		{
			try
			{
				File.Delete(_socketPath);
			}
			catch (IOException)
			{
				// 残留的 socket 文件无碍：下一次绑定会替换它。
			}
		}

		_listenCancellation.Dispose();
	}

	private static bool IsLive(string path)
	{
		using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		try
		{
			probe.Connect(new UnixDomainSocketEndPoint(path));
			return true;
		}
		catch (SocketException)
		{
			return false; // 没有进程在监听：这是残留文件
		}
	}

	private static bool TryDelete(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch (IOException)
		{
			// 与另一次启动竞争，交给重试决定。
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			// 不是我们该删除的文件。
			return false;
		}
	}

	private static string SocketPath(string instanceKey)
	{
		// 与其余状态同放 agent 目录：PI_CODING_AGENT_DIR 指到别处时，socket 也跟着走。
		var directory = SessionPaths.GetAgentDir();

		Directory.CreateDirectory(directory);
		return Path.Combine(directory, $"{SocketPrefix}{instanceKey}.sock");
	}
}
