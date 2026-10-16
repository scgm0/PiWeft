using System.IO;

using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Themes;

/// <summary>
/// 监视主题根目录（用户、项目、settings 声明与包）的 <c>*.json</c> 变化，
/// 防抖后触发一次回调。坏文件不产生事件语义变化——由调用方重新解析并决定是否换肤。
/// 文件系统不可用（目录不存在、平台不支持）时返回的实例静默失效。
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
	private const int DebounceMs = 400;

	private readonly List<FileSystemWatcher> _watchers = [];
	private readonly object _gate = new();
	private CancellationTokenSource? _debounce;
	private bool _disposed;

	private ThemeWatcher()
	{
	}

	/// <summary>防抖后的变更回调；在后台线程触发，调用方自行调度到 UI 线程。</summary>
	public event Action? Changed;

	/// <summary>
	/// 监视给定主题根目录。目录不存在的项跳过（不为其创建监视器）；
	/// 没有任何可监视目录时返回可用但空转的实例。
	/// </summary>
	public static ThemeWatcher Create(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var packages = PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		var roots = ResourceResolver.ThemeRoots(cwd, resolvedAgentDir, includeProject, packages);

		var watcher = new ThemeWatcher();
		foreach (var (root, _) in roots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}

			try
			{
				var fsWatcher = new FileSystemWatcher(root, "*.json")
				{
					IncludeSubdirectories = false,
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
					InternalBufferSize = 16 * 1024,
				};
				fsWatcher.Created += watcher.OnEvent;
				fsWatcher.Changed += watcher.OnEvent;
				fsWatcher.Deleted += watcher.OnEvent;
				fsWatcher.Renamed += watcher.OnEvent;
				fsWatcher.Error += static (_, _) => { }; // 缓冲区溢出丢事件可接受：下次保存会再触发。
				fsWatcher.EnableRaisingEvents = true;
				watcher._watchers.Add(fsWatcher);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
				or PlatformNotSupportedException or ArgumentException)
			{
				// 单个目录监视失败不影响其它目录。
			}
		}

		// 根目录本身晚建（用户现在才创建 themes 目录）也算一次变更；父目录不存在时跳过 ——
		// 此时用户还没有开始放主题，等下次启动再建立监视即可。
		var parents = roots
			.Select(static root => Path.GetDirectoryName(root.Path))
			.Where(static dir => dir is { Length: > 0 })
			.Distinct(StringComparer.Ordinal);
		foreach (var parent in parents)
		{
			if (!Directory.Exists(parent))
			{
				continue;
			}

			try
			{
				var dirWatcher = new FileSystemWatcher(parent!, ResourcePaths.ThemesDirectoryName)
				{
					IncludeSubdirectories = false,
					NotifyFilter = NotifyFilters.DirectoryName,
					InternalBufferSize = 8 * 1024,
				};
				dirWatcher.Created += watcher.OnEvent;
				dirWatcher.Deleted += watcher.OnEvent;
				dirWatcher.Renamed += watcher.OnEvent;
				dirWatcher.Error += static (_, _) => { };
				dirWatcher.EnableRaisingEvents = true;
				watcher._watchers.Add(dirWatcher);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
				or PlatformNotSupportedException or ArgumentException)
			{
			}
		}

		return watcher;
	}

	private void OnEvent(object? sender, FileSystemEventArgs e)
	{
		if (_disposed)
		{
			return;
		}

		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_debounce?.Cancel();
			_debounce?.Dispose();
			_debounce = new CancellationTokenSource();
			var token = _debounce.Token;
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(DebounceMs, token).ConfigureAwait(false);
					if (!token.IsCancellationRequested)
					{
						Changed?.Invoke();
					}
				}
				catch (OperationCanceledException)
				{
				}
			}, CancellationToken.None);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_debounce?.Cancel();
			_debounce?.Dispose();
			_debounce = null;
		}

		foreach (var watcher in _watchers)
		{
			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}

		_watchers.Clear();
	}
}
