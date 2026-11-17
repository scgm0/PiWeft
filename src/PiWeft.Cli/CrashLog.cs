using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Cli;

/// 崩溃类别。
public enum CrashKind
{
	/// 未处理的异常终止了进程。
	UnhandledException,

	/// UI 线程上的未处理异常（已拦住，进程继续）。
	DispatcherException,

	/// 启动或主循环抛出的致命错误。
	FatalError,
}

/// 一次崩溃的现场记录。
/// <param name="Timestamp">发生时间（UTC）。</param>
/// <param name="Version">写入方的程序集版本。</param>
/// <param name="Application">应用名（cli / desktop）。</param>
/// <param name="Kind">崩溃类别。</param>
/// <param name="Message">脱敏后的异常链摘要。</param>
/// <param name="Stack">脱敏后的栈。</param>
/// <param name="SessionFile">出事时的会话文件，可能为空。</param>
/// <param name="Cwd">出事时的工作目录。</param>
/// <param name="Notified">是否已经在下次启动时提示过。</param>
public sealed record CrashRecord(
	DateTimeOffset Timestamp,
	string Version,
	string Application,
	CrashKind Kind,
	string Message,
	string? Stack,
	string? SessionFile,
	string? Cwd,
	bool Notified);

/// 崩溃记录：<c>&lt;agentDir&gt;/crashes.json</c>。
/// <remarks>
/// 日志文件只在用户主动打开时才被读到，进程崩掉这件事本身没人看得到。这里留一条能在下次启动
/// 读出来的记录：桌面启动后提示一次，用户才知道去找日志或诊断包。参考 pi 的 crashes.json。
/// </remarks>
public static class CrashLog
{
	/// 文件名。
	public const string FileName = "crashes.json";

	/// 保留的记录条数。
	public const int MaxRecords = 5;

	/// 记录保留时长。
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

	private static readonly Lock Gate = new();

	// 默认序列化器会把 <workspace> 里的尖括号转成 \u003C，文件对用户就不可读了。
	private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// 某个 agent 目录下的崩溃文件路径。
	public static string PathFor(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), FileName);

	/// 读取记录，损坏或缺失时返回空表。
	public static IReadOnlyList<CrashRecord> Read(string? agentDir = null)
	{
		var path = PathFor(agentDir);
		try
		{
			if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
			{
				return [];
			}

			var records = new List<CrashRecord>(array.Count);
			foreach (var node in array)
			{
				if (Parse(node) is { } record)
				{
					records.Add(record);
				}
			}

			return records;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return [];
		}
	}

	/// 记一条崩溃。写盘失败只返回 <see langword="null" />：崩溃路径上不能再抛。
	public static CrashRecord? Record(
		CrashKind kind,
		Exception error,
		string? sessionFile = null,
		string? cwd = null,
		string? agentDir = null,
		string? application = null)
	{
		lock (Gate)
		{
			agentDir ??= SessionPaths.GetAgentDir();
			try
			{
				var now = DateTimeOffset.UtcNow;
				var redactor = new LogRedactor(cwd, agentDir);
				var record = new CrashRecord(
					now,
					typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "0",
					application ?? AppLog.Application,
					kind,
					redactor.Line(AppLog.Chain(error)),
					error.StackTrace is { Length: > 0 } stack ? redactor.Line(stack) : null,
					sessionFile,
					redactor.Text(cwd),
					Notified: false);
				var kept = Read(agentDir)
					.Where(existing => now - existing.Timestamp <= MaxAge)
					.Append(record)
					.TakeLast(MaxRecords)
					.ToArray();
				Write(agentDir, kept);
				return record;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
				or System.Text.Json.JsonException or InvalidOperationException)
			{
				return null;
			}
		}
	}

	/// 取最新一条未提示过的崩溃，并把全部记录标记为已提示。
	/// <remarks>
	/// 提示一次就够了：反复弹同一个崩溃会盖住真正的当前问题。标记写盘失败时下次会再提示一遍，
	/// 那比「永远不提示」好。
	/// </remarks>
	public static CrashRecord? TakeUnnotified(string? agentDir = null, DateTimeOffset? now = null)
	{
		lock (Gate)
		{
			var records = Read(agentDir);
			if (records.Count == 0)
			{
				return null;
			}

			var current = now ?? DateTimeOffset.UtcNow;
			var pending = records
				.Where(record => !record.Notified && current - record.Timestamp <= MaxAge)
				.LastOrDefault();
			if (pending is null)
			{
				return null;
			}

			try
			{
				Write(agentDir, [.. records.Select(record => record with { Notified = true })]);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 标记写不进去时下次启动再提示一次，不做别的补救。
			}

			return pending;
		}
	}

	/// 删除崩溃记录文件。
	public static void Clear(string? agentDir = null)
	{
		lock (Gate)
		{
			try
			{
				File.Delete(PathFor(agentDir));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 删不掉就留着，下次启动会再提示一次。
			}
		}
	}

	private static void Write(string? agentDir, IEnumerable<CrashRecord> records)
	{
		var path = PathFor(agentDir);
		var directory = Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var array = new JsonArray();
		foreach (var record in records)
		{
			// 先落到 JsonNode 再 Add：泛型重载会走 JsonValue 的运行时反射，AOT 下不可用。
			JsonNode entry = new JsonObject
			{
				["timestamp"] = record.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
				["version"] = record.Version,
				["app"] = record.Application,
				["kind"] = KindName(record.Kind),
				["message"] = record.Message,
				["stack"] = record.Stack,
				["sessionFile"] = record.SessionFile,
				["cwd"] = record.Cwd,
				["notified"] = record.Notified,
			};
			array.Add(entry);
		}

		// 先写临时文件再替换：崩溃瞬间断电也不能留下半份 JSON，下次启动会把它当损坏丢掉。
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, array.ToJsonString(JsonOptions), new UTF8Encoding(false));
		File.Move(temporary, path, overwrite: true);
	}

	private static CrashRecord? Parse(JsonNode? node)
	{
		if (node is not JsonObject obj ||
			Text(obj, "timestamp") is not { Length: > 0 } timestamp ||
			!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ||
			Text(obj, "message") is not { Length: > 0 } message)
		{
			return null;
		}

		return new CrashRecord(
			time,
			Text(obj, "version") ?? "0",
			Text(obj, "app") ?? "pi",
			KindOf(Text(obj, "kind")),
			message,
			Text(obj, "stack"),
			Text(obj, "sessionFile"),
			Text(obj, "cwd"),
			obj["notified"] is JsonValue notified && notified.TryGetValue<bool>(out var done) && done);
	}

	private static string? Text(JsonObject obj, string key) =>
		obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static string KindName(CrashKind kind) => kind switch
	{
		CrashKind.DispatcherException => "dispatcher_exception",
		CrashKind.FatalError => "fatal_error",
		_ => "unhandled_exception",
	};

	private static CrashKind KindOf(string? name) => name switch
	{
		"dispatcher_exception" => CrashKind.DispatcherException,
		"fatal_error" => CrashKind.FatalError,
		_ => CrashKind.UnhandledException,
	};
}
