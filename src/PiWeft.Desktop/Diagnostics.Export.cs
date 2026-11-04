using System.Globalization;
using System.Text.Json;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// Chrome Trace Event Format 导出：不自己造查看器，直接复用 Perfetto / Chrome DevTools /
/// speedscope 这类成熟工具。timings JSONL 仍保留为机器可读的原始数据。
internal sealed partial class Diagnostics
{
	public static string? ExportChromeTrace(string? destination = null)
	{
		var directory = Volatile.Read(ref _directory);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return null;
		}

		// 导出前把队列里最后一批写掉，否则刚刚复现的慢路径可能还停在内存里。
		if (_enabled) Drain();
		var files = Directory.EnumerateFiles(directory, "timings-*.jsonl")
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
		if (files.Length == 0) return null;

		destination ??= Path.Combine(
			directory,
			$"trace-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");

		try
		{
			using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
			using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
			json.WriteStartObject();
			json.WriteString("displayTimeUnit", "ms");
			json.WriteString("name", "PiWeft diagnostics");
			json.WriteStartArray("traceEvents");
			WriteProcessMetadata(json);

			foreach (var file in files)
			{
				foreach (var line in File.ReadLines(file))
				{
					if (line.Length == 0 || line[0] != '{') continue;
					try
					{
						using var document = JsonDocument.Parse(line);
						WriteTraceEvent(json, document.RootElement);
					}
					catch (JsonException)
					{
						// 采集期间被截断的尾行跳过，整份 trace 仍然可打开。
					}
				}
			}

			json.WriteEndArray();
			json.WriteEndObject();
			return destination;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Export Chrome trace", exception);
			return null;
		}
	}

	private static void WriteProcessMetadata(Utf8JsonWriter json)
	{
		json.WriteStartObject();
		json.WriteString("name", "process_name");
		json.WriteString("ph", "M");
		json.WriteNumber("pid", Environment.ProcessId);
		json.WriteStartObject("args");
		json.WriteString("name", "PiWeft");
		json.WriteEndObject();
		json.WriteEndObject();

		json.WriteStartObject();
		json.WriteString("name", "process_sort_index");
		json.WriteString("ph", "M");
		json.WriteNumber("pid", Environment.ProcessId);
		json.WriteStartObject("args");
		json.WriteNumber("sort_index", 1);
		json.WriteEndObject();
		json.WriteEndObject();
	}

	private static void WriteTraceEvent(Utf8JsonWriter json, JsonElement record)
	{
		var name = record.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
		if (string.IsNullOrEmpty(name)) return;
		var kind = record.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : "span";
		if (string.Equals(kind, "meta", StringComparison.Ordinal)) return;

		var category = record.TryGetProperty("cat", out var catValue) ? catValue.GetString() : null;
		var pid = record.TryGetProperty("pid", out var pidValue) && pidValue.TryGetInt32(out var processId)
			? processId
			: Environment.ProcessId;
		var tid = record.TryGetProperty("tid", out var tidValue) && tidValue.TryGetInt32(out var threadId)
			? threadId
			: 0;
		var durationMs = record.TryGetProperty("durationMs", out var durationValue) && durationValue.TryGetDouble(out var ms)
			? ms
			: 0;
		var timestampUs = DateTimeOffset.Now.ToUnixTimeMilliseconds() * 1000L;
		if (record.TryGetProperty("startedAt", out var startedValue) &&
			DateTimeOffset.TryParse(startedValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
		{
			timestampUs = started.ToUnixTimeMilliseconds() * 1000L;
		}

		var detail = record.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;

		json.WriteStartObject();
		json.WriteString("name", name);
		json.WriteString("cat", string.IsNullOrEmpty(category) ? CategoryOf(name) : category);
		json.WriteString("ph", string.Equals(kind, "counter", StringComparison.Ordinal) ? "C" : "X");
		json.WriteNumber("ts", timestampUs);
		if (!string.Equals(kind, "counter", StringComparison.Ordinal))
		{
			json.WriteNumber("dur", (long)Math.Round(durationMs * 1000));
		}

		json.WriteNumber("pid", pid);
		json.WriteNumber("tid", tid);
		json.WriteStartObject("args");
		if (!string.IsNullOrEmpty(detail))
		{
			json.WriteString("detail", detail);
			WriteDetailArguments(json, detail);
		}

		json.WriteEndObject();
		json.WriteEndObject();
	}

	/// 把 `p95=18.2 dropped=3` 这类 detail 拆成 Chrome Trace 的 args，数值项可被图表直接读取。
	private static void WriteDetailArguments(Utf8JsonWriter json, string detail)
	{
		var remaining = detail.AsSpan();
		while (!remaining.IsEmpty)
		{
			var space = remaining.IndexOf(' ');
			var token = space < 0 ? remaining : remaining[..space];
			remaining = space < 0 ? default : remaining[(space + 1)..];
			if (token.IsEmpty) continue;
			var equals = token.IndexOf('=');
			if (equals <= 0 || equals == token.Length - 1) continue;
			var key = token[..equals];
			var value = token[(equals + 1)..];
			if (!IsTraceKey(key)) continue;
			if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
			{
				json.WriteNumber(key, number);
			}
			else
			{
				json.WriteString(key, value);
			}
		}
	}

	private static bool IsTraceKey(ReadOnlySpan<char> key)
	{
		if (key.IsEmpty || !char.IsLetter(key[0])) return false;
		foreach (var c in key)
		{
			if (!char.IsLetterOrDigit(c) && c is not ('_' or '.' or '/')) return false;
		}

		return true;
	}
}
