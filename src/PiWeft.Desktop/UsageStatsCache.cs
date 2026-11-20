using PiWeft.Ai;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

internal readonly record struct UsageStatsCacheResult(UsageStatsReport Report, DateTimeOffset RefreshedAt);

/// <summary>
/// 设置页使用统计的扫描缓存：按会话文件做增量扫描，未变的文件不重读。
/// 每次进入页面都核对文件长度与写入时间，外部进程追加的会话也能被发现。
/// </summary>
internal sealed class UsageStatsCache
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly UsageStats.ScanState _state = new();
	private string? _root;
	private UsageStatsReport? _report;
	private DateTimeOffset _refreshedAt;
	private Func<string, string, ModelCostRates?>? _rates;
	private long _completed;

	/// <summary>实际执行 Scan 的次数（含增量扫描）；仅供测试观察缓存是否命中。</summary>
	internal int Scans { get; private set; }

	/// <summary>最近一次扫描需要重读的文件数；仅供测试观察增量。</summary>
	internal int ReparsedFiles => _state.ReparsedFiles;

	public async Task<UsageStatsCacheResult> GetAsync(
		string root,
		Func<string, string, ModelCostRates?> rates,
		bool force,
		CancellationToken cancellationToken)
	{
		var observed = Volatile.Read(ref _completed);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// 等锁期间另一个请求刚扫完同一份数据：直接复用，不必再核对一遍文件。
			if (!force && _report is not null && string.Equals(_root, root, StringComparison.Ordinal) &&
				Volatile.Read(ref _completed) != observed && Equals(rates, _rates))
			{
				return new UsageStatsCacheResult(_report, _refreshedAt);
			}

			var report = await Task
				.Run(() => UsageStats.Scan(root, _state, new UsageStatsOptions { Rates = rates }, cancellationToken), cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();

			Scans++;
			_root = root;
			_report = report;
			_rates = rates;
			_refreshedAt = DateTimeOffset.Now;
			Interlocked.Increment(ref _completed);
			return new UsageStatsCacheResult(report, _refreshedAt);
		}
		finally
		{
			_gate.Release();
		}
	}
}
