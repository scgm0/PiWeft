using System.Globalization;
using System.Text;
using System.Text.Json;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// 诊断文件的采集生命周期：打开、元数据、轮转、清理与关闭。
/// 与采样/聚合分开，保证写盘失败时只影响 diagnostics，不影响 UI。
internal sealed partial class Diagnostics
{
	private sealed class WriterSession(string path, FileStream stream, StreamWriter writer, int sequence) : IDisposable
	{
		public string Path { get; } = path;
		public FileStream Stream { get; } = stream;
		public StreamWriter Writer { get; } = writer;
		public int Sequence { get; } = sequence;
		public long Bytes { get; set; }

		public void Dispose() => DisposeSession(this);
	}

	private static WriterSession OpenWriter(string directory, int sequence)
	{
		var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		for (var attempt = 0; attempt < 100; attempt++)
		{
			var path = Path.Combine(
				directory,
				$"timings-{stamp}-{Environment.ProcessId}-{sequence + attempt:00}.jsonl");
			try
			{
				var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
				var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
				return new WriterSession(path, stream, writer, sequence + attempt);
			}
			catch (IOException) when (attempt < 99)
			{
				// 同一秒内重复启用：换一个序号，不覆盖上一份采集。
			}
		}

		throw new IOException("Could not create a unique diagnostics file.");
	}

	private static void WriteMetaLocked(WriterSession session)
	{
		var line = SerializeMeta();
		session.Writer.WriteLine(line);
		session.Writer.Flush();
		session.Bytes += line.Length + 1;
	}

	private static WriterSession RotateLocked(WriterSession session)
	{
		session.Dispose();
		var next = OpenWriter(_directory, session.Sequence + 1);
		Volatile.Write(ref _session, next);
		_timingsPath = next.Path;
		WriteMetaLocked(next);
		CleanupOldFiles(_directory);
		return next;
	}

	private static void CleanupOldFiles(string directory)
	{
		try
		{
			var cutoff = DateTime.UtcNow.AddDays(-7);
			var files = new DirectoryInfo(directory)
				.EnumerateFiles("timings-*.jsonl")
				.OrderByDescending(static file => file.LastWriteTimeUtc)
				.ToArray();
			for (var index = 0; index < files.Length; index++)
			{
				if (index < MaxFiles && files[index].LastWriteTimeUtc >= cutoff) continue;
				try
				{
					files[index].Delete();
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 旧文件删不掉不影响本次采集。
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Diagnostics cleanup skipped: {exception.Message}");
		}
	}

	private static string SerializeMeta()
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
		using (var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteNumber("v", 1);
			json.WriteString("kind", "meta");
			json.WriteString("name", "diagnostics.meta");
			json.WriteString("startedAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
			json.WriteNumber("pid", Environment.ProcessId);
			json.WriteString("app", typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "unknown");
			json.WriteString("runtime", Environment.Version.ToString());
			json.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
			json.WriteString("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
			json.WriteNumber("cores", Environment.ProcessorCount);
			json.WriteString("cwd", Environment.CurrentDirectory);
			json.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static void DisposeSession(WriterSession session)
	{
		try
		{
			session.Writer.Flush();
			session.Writer.Dispose();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Diagnostics close skipped: {exception.Message}");
		}
	}
}
