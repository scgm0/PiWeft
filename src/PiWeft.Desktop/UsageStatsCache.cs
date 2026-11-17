using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

internal readonly record struct UsageStatsCacheResult(UsageStatsReport Report, DateTimeOffset RefreshedAt);

/// <summary>设置页与页签共享的使用统计缓存：按会话文件指纹和进程内写入代数判断是否重扫。</summary>
internal sealed class UsageStatsCache
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Func<long> _generationProvider;
	private string? _root;
	private UsageStatsReport? _report;
	private DateTimeOffset _refreshedAt;
	private UsageStatsFingerprint _fingerprint;
	private long _generation;

	/// <summary>实际执行扫描的次数；仅供测试观察缓存是否命中。</summary>
	internal int Scans { get; private set; }

	/// <summary>创建缓存；测试可注入自己的写入代数。</summary>
	public UsageStatsCache(Func<long>? generationProvider = null)
	{
		_generationProvider = generationProvider ?? (static () => UsageStats.Generation);
	}

	public async Task<UsageStatsCacheResult> GetAsync(
		string root,
		Func<string, string, ModelCostRates?> rates,
		bool force,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var fingerprint = default(UsageStatsFingerprint);
			var hasFingerprint = false;
			if (!force && _report is not null && string.Equals(_root, root, StringComparison.Ordinal) &&
				_generation == _generationProvider())
			{
				fingerprint = await Task
					.Run(() => UsageStatsFingerprint.Capture(root), cancellationToken)
					.ConfigureAwait(false);
				if (fingerprint.Valid && fingerprint == _fingerprint)
				{
					return new UsageStatsCacheResult(_report, _refreshedAt);
				}

				hasFingerprint = true;
			}

			// 指纹和写入代数在扫描前取：扫描期间有新写入时，下一次请求会发现并重扫。
			if (!hasFingerprint)
			{
				fingerprint = await Task
					.Run(() => UsageStatsFingerprint.Capture(root), cancellationToken)
					.ConfigureAwait(false);
			}

			var generation = _generationProvider();
			var report = await Task
				.Run(() => UsageStats.Scan(root, new UsageStatsOptions { Rates = rates }, cancellationToken), cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();

			Scans++;
			_root = root;
			_report = report;
			_refreshedAt = DateTimeOffset.Now;
			_fingerprint = fingerprint;
			_generation = generation;
			return new UsageStatsCacheResult(report, _refreshedAt);
		}
		finally
		{
			_gate.Release();
		}
	}
}

/// <summary>会话文件元数据指纹；内容不变时不必重新解析 JSONL。</summary>
internal readonly record struct UsageStatsFingerprint(int Files, long Bytes, long MaxWriteTicks, ulong Hash, bool Valid)
{
	private static readonly UsageStatsFingerprint Invalid = new(0, 0, 0, 0, false);

	public static UsageStatsFingerprint Capture(string root)
	{
		if (!Directory.Exists(root))
		{
			return new UsageStatsFingerprint(0, 0, 0, 0, true);
		}

		var files = 0;
		long bytes = 0;
		long maxWriteTicks = 0;
		ulong hash = 0;
		try
		{
			foreach (var file in Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories))
			{
				long length;
				long writeTicks;
				try
				{
					var info = new FileInfo(file);
					if (!info.Exists)
					{
						continue;
					}

					length = info.Length;
					writeTicks = info.LastWriteTimeUtc.Ticks;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return Invalid;
				}

				files++;
				bytes += length;
				maxWriteTicks = Math.Max(maxWriteTicks, writeTicks);
				hash = unchecked(hash + FileHash(file, length, writeTicks));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return Invalid;
		}

		return new UsageStatsFingerprint(files, bytes, maxWriteTicks, hash, true);
	}

	private static ulong FileHash(string path, long length, long writeTicks)
	{
		var hash = 14695981039346656037UL;
		foreach (var character in path)
		{
			hash ^= character;
			hash *= 1099511628211UL;
		}

		hash ^= (ulong)length;
		hash *= 1099511628211UL;
		hash ^= (ulong)writeTicks;
		hash *= 1099511628211UL;
		return hash;
	}
}
