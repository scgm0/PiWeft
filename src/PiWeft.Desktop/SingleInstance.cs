using System.Net.Sockets;
using System.Text;

namespace PiWeft.Desktop;

/// <summary>
/// 保证每个用户只有一个实例：第二次启动会立即退出（并尽量让已运行的实例把窗口带到前台），
/// 而不是让两个窗口同时编辑同一批会话。
/// </summary>
/// <remarks>
/// Unix 使用 agent 目录下的 socket：已有实例持有时绑定会失败（<c>EADDRINUSE</c>），该 socket 同时充当激活通道；
/// Windows 没有这种 socket，改用命名互斥体。
/// </remarks>
public sealed class SingleInstance : IDisposable
{
	// 继续与改名前的桌面实例共用锁，防止新旧程序同时编辑同一个项目。
	private const string MutexName = @"Local\piweft-desktop-single-instance";
	private const string ActivateMessage = "activate";

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

	/// <summary>本进程是否为第一个实例。</summary>
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

	/// <summary>请求已运行的实例把窗口带到前台。尽力而为：具体行为由界面决定。</summary>
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

	/// <summary>等待后续启动发来的激活请求。</summary>
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
				Socket accepted;
				try
				{
					accepted = await _socket.AcceptAsync(cancellation).ConfigureAwait(false);
				}
				catch (Exception)
				{
					return; // 已释放或被取消
				}

				using (accepted)
				{
					var buffer = new byte[64];
					var read = await accepted.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
					if (read > 0 && Encoding.UTF8.GetString(buffer, 0, read).StartsWith(ActivateMessage, StringComparison.Ordinal))
					{
						onActivate();
					}
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

		if (_socketPath is not null && File.Exists(_socketPath))
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

	/// <summary>该 socket 路径上是否真的有进程在监听。</summary>
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

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
			// 与另一次启动竞争，交给重试决定。
		}
		catch (UnauthorizedAccessException)
		{
			// 不是我们该删除的文件。
		}
	}

	private static string SocketPath(string instanceKey)
	{
		var directory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".pi",
			"agent");

		Directory.CreateDirectory(directory);
		return Path.Combine(directory, $"piweft-desktop-{instanceKey}.sock");
	}
}
