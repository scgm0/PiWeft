using System.Runtime.InteropServices;

namespace PiWeft.CodingAgent.Stats;

public static partial class UsageStats
{
	/// <summary>
	/// 增量扫描状态：按会话文件缓存已聚合的用量，只重读新增或变更的文件。
	/// 时区、Since 或行长上限变化时会清空重扫。同一个实例不能并发扫描。
	/// </summary>
	public sealed class ScanState
	{
		private readonly Dictionary<string, FileScan> _files = new(StringComparer.Ordinal);
		private string? _root;
		private string? _timeZoneId;
		private DateTimeOffset? _since;
		private int _maxLineBytes;

		/// <summary>上一次 Scan 需要重读的文件数。</summary>
		public int ReparsedFiles { get; internal set; }

		/// <summary>上一次 Scan 从断点续读的文件数。</summary>
		public int ResumedFiles { get; internal set; }

		internal bool TryGetFile(string path, out FileScan scan) => _files.TryGetValue(path, out scan!);

		internal void SetFile(string path, FileScan scan) => _files[path] = scan;

		internal void Prepare(string root, TimeZoneInfo timeZone, UsageStatsOptions options)
		{
			if (!string.Equals(_root, root, StringComparison.Ordinal) ||
				!string.Equals(_timeZoneId, timeZone.Id, StringComparison.Ordinal) ||
				_since != options.Since ||
				_maxLineBytes != options.MaxLineBytes)
			{
				_files.Clear();
				_root = root;
				_timeZoneId = timeZone.Id;
				_since = options.Since;
				_maxLineBytes = options.MaxLineBytes;
			}

			ReparsedFiles = 0;
			ResumedFiles = 0;
		}

		/// <summary>清掉本次没枚举到的文件缓存；取消或按项目过滤时集合不完整，不能删。</summary>
		internal void Trim(HashSet<string>? seen, string? project, bool incomplete)
		{
			if (seen is null || incomplete || project is { Length: > 0 })
			{
				return;
			}

			List<string>? missing = null;
			foreach (var path in _files.Keys)
			{
				if (!seen.Contains(path))
				{
					(missing ??= []).Add(path);
				}
			}

			if (missing is null)
			{
				return;
			}

			foreach (var path in missing)
			{
				_files.Remove(path);
			}
		}
	}

	internal sealed class FileScan
	{
		public long Length;
		public long LastWriteTicks;

		/// <summary>已消费到且落在换行边界的字节偏移；0 表示下次只能整份重扫。</summary>
		public long Boundary;
		public ulong BoundaryHash;

		/// <summary>文件开头一段的哈希：头部（cwd）被改写时不能沿用旧 Project 与旧聚合。</summary>
		public ulong HeadHash;
		public string Project = string.Empty;
		public int Turns;
		public DateTimeOffset? Start;
		public DateTimeOffset? End;
		public bool HasDurations;

		/// <summary>本次扫描是否从断点续读；不参与缓存，Clone 不复制。</summary>
		public bool Resumed;

		/// <summary>回复时长区间保留原样：续读时新回复可能与旧回复重叠，只存总和会多算。</summary>
		public ActiveIntervalUnion Active = new();

		public Dictionary<DateOnly, Dictionary<ModelKey, Bucket>> Days = [];
		public Dictionary<ModelKey, Bucket> Undated = [];

		public double ActiveMs => Active.TotalMs;

		public FileScan Clone()
		{
			var clone = new FileScan
			{
				Length = Length,
				LastWriteTicks = LastWriteTicks,
				Boundary = Boundary,
				BoundaryHash = BoundaryHash,
				HeadHash = HeadHash,
				Project = Project,
				Turns = Turns,
				Start = Start,
				End = End,
				HasDurations = HasDurations,
				Active = Active.Clone(),
			};

			foreach (var (key, bucket) in Undated)
			{
				clone.Undated[key] = bucket.Clone();
			}

			foreach (var (date, byModel) in Days)
			{
				var copy = new Dictionary<ModelKey, Bucket>(byModel.Count);
				foreach (var (key, bucket) in byModel)
				{
					copy[key] = bucket.Clone();
				}

				clone.Days[date] = copy;
			}

			return clone;
		}

		public void Add(
			DateOnly? date,
			ModelKey key,
			long input,
			long output,
			long cacheRead,
			long cacheWrite,
			long reportedTokens,
			long messages,
			double cost)
		{
			if (date is not { } day)
			{
				AddTo(Undated, key, input, output, cacheRead, cacheWrite, reportedTokens, messages, cost);
				return;
			}

			if (!Days.TryGetValue(day, out var byModel))
			{
				byModel = new Dictionary<ModelKey, Bucket>();
				Days[day] = byModel;
			}

			AddTo(byModel, key, input, output, cacheRead, cacheWrite, reportedTokens, messages, cost);
		}

		private static void AddTo(
			Dictionary<ModelKey, Bucket> map,
			ModelKey key,
			long input,
			long output,
			long cacheRead,
			long cacheWrite,
			long reportedTokens,
			long messages,
			double cost)
		{
			ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
			bucket ??= new Bucket();
			bucket.Add(input, output, cacheRead, cacheWrite, reportedTokens, messages, cost);
		}
	}

	private const int BoundaryProbeBytes = 64;
	private const int HeadProbeBytes = 256;

	private static (long Length, long WriteTicks) Snapshot(string file)
	{
		try
		{
			var info = new FileInfo(file);
			return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return (-1, 0);
		}
	}

	/// <summary>断点前的 64 字节与文件头 256 字节都与上次一致才算纯追加；否则整份重扫。</summary>
	private static bool BoundaryMatches(string file, long boundary, ulong expectedTail, ulong expectedHead)
	{
		try
		{
			using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			Span<byte> head = stackalloc byte[HeadProbeBytes];
			var headCount = (int)Math.Min(HeadProbeBytes, boundary);
			if (!TryReadExact(handle, head[..headCount], 0) || BoundaryHash(head[..headCount]) != expectedHead)
			{
				return false;
			}

			Span<byte> tail = stackalloc byte[BoundaryProbeBytes];
			var start = Math.Max(0, boundary - tail.Length);
			var count = (int)(boundary - start);
			return TryReadExact(handle, tail[..count], start) && BoundaryHash(tail[..count]) == expectedTail;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>读文件尾部：只有以换行收尾的 EOF 才能当续读边界。</summary>
	private static bool TryCaptureBoundary(string file, long length, out ulong tailHash, out ulong headHash)
	{
		tailHash = 0;
		headHash = 0;
		try
		{
			using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			Span<byte> probe = stackalloc byte[BoundaryProbeBytes];
			var start = Math.Max(0, length - probe.Length);
			var count = (int)(length - start);
			if (!TryReadExact(handle, probe[..count], start) || probe[count - 1] != (byte)'\n')
			{
				return false;
			}

			tailHash = BoundaryHash(probe[..count]);

			Span<byte> head = stackalloc byte[HeadProbeBytes];
			var headCount = (int)Math.Min(HeadProbeBytes, length);
			if (!TryReadExact(handle, head[..headCount], 0))
			{
				return false;
			}

			headHash = BoundaryHash(head[..headCount]);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool TryReadExact(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long offset)
	{
		var read = 0;
		while (read < buffer.Length)
		{
			var count = RandomAccess.Read(handle, buffer[read..], offset + read);
			if (count <= 0)
			{
				return false;
			}

			read += count;
		}

		return true;
	}

	private static ulong BoundaryHash(ReadOnlySpan<byte> bytes)
	{
		var hash = 14695981039346656037UL;
		foreach (var value in bytes)
		{
			hash ^= value;
			hash *= 1099511628211UL;
		}

		return hash;
	}
}
