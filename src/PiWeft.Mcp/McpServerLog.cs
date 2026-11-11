using System.Text;
using PiWeft.Mcp;

namespace PiWeft.Mcp;

/// 服务器日志：<c>&lt;agentDir&gt;/mcp.log</c>，每行
/// <c>&lt;time&gt; [&lt;server&gt;] &lt;level&gt; &lt;logger&gt;: &lt;message&gt;</c>，超过 5 MB 时滚动到 <c>mcp.log.1</c>。
/// 写入失败静默忽略：日志不该影响 MCP 连接。
public sealed class McpServerLog
{
	public const string FileName = "mcp.log";

	private const long MaxBytes = 5 * 1024 * 1024;

	private readonly string _path;
	private readonly Lock _gate = new();

	public McpServerLog(string? agentDir = null) =>
		_path = Path.Combine(McpConfig.GetAgentDir(agentDir), FileName);

	/// 日志文件路径。名字避开 <see cref="System.IO.Path"/> 类型，否则同一文件里的
	/// <c>Path.Combine</c> 会解析到这个属性。
	public string FilePath => _path;

	/// 记录一条 <c>notifications/message</c>：<c>{ level, logger?, data }</c>。
	public void Write(string server, System.Text.Json.Nodes.JsonObject? parameters)
	{
		if (parameters is null)
		{
			return;
		}

		var level = Text(parameters, "level") ?? "info";
		var logger = Text(parameters, "logger");
		var data = parameters["data"];
		var message = data switch
		{
			null => string.Empty,
			System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) => text,
			_ => data.ToJsonString(),
		};

		var line = new StringBuilder()
			.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
			.Append(" [").Append(server).Append("] ")
			.Append(level);
		if (logger is { Length: > 0 })
		{
			line.Append(' ').Append(logger).Append(':');
		}

		line.Append(' ').Append(message.ReplaceLineEndings(" ")).Append('\n');
		Append(line.ToString());
	}

	public void Append(string line)
	{
		lock (_gate)
		{
			try
			{
				var directory = System.IO.Path.GetDirectoryName(_path);
				if (directory is { Length: > 0 })
				{
					Directory.CreateDirectory(directory);
				}

				RotateIfNeeded();
				File.AppendAllText(_path, line, new UTF8Encoding(false));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 日志不可写不影响连接。
			}
		}
	}

	private void RotateIfNeeded()
	{
		var info = new FileInfo(_path);
		if (!info.Exists || info.Length < MaxBytes)
		{
			return;
		}

		var archived = _path + ".1";
		try
		{
			File.Move(_path, archived, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 滚动失败时继续追加：文件变大总比丢日志好。
		}
	}

	private static string? Text(System.Text.Json.Nodes.JsonObject parameters, string key) =>
		parameters[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
			? text
			: null;
}
