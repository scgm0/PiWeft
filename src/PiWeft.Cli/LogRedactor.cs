using System.Globalization;
using System.Text;

namespace PiWeft.Cli;

/// 日志出口的脱敏器：家目录、agent 目录、工作目录换成占位符，常见凭据换成 <c>***</c>。
/// 落盘与导出各过一次。一次配置、逐行调用，避免每行重算路径前缀。
/// <remarks>
/// 直接复用 <see cref="SessionRedaction"/> 的正则集合：日志导出的隐私边界和 <c>/share --safe</c>
/// 是同一件事，两套规则迟早会走偏。
/// </remarks>
public sealed class LogRedactor
{
	/// 单条记录上限。一条日志撑到几 MB 时，后面的内容多半是整段 prompt 或响应体，
	/// 留在文件里只会挤掉真正有用的相邻记录。
	public const int MaxLineChars = 8192;

	private readonly RedactionScope _scope;

	/// <param name="cwd">工作目录，可空。</param>
	/// <param name="agentDir">agent 目录，可空。</param>
	public LogRedactor(string? cwd = null, string? agentDir = null) =>
		_scope = RedactionScope.From(cwd, agentDir);

	/// 按当前目录推断范围：日志出口不知道会话 cwd 时用它。
	public static LogRedactor ForCurrentDirectory() =>
		new(Environment.CurrentDirectory, PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir());

	/// 只脱敏，保留换行。
	public string Text(string? value) =>
		string.IsNullOrEmpty(value) ? value ?? string.Empty : SessionRedaction.Text(value, _scope);

	/// 脱敏 + 压成一行 + 截断。<c>AppLog</c> 的文件格式是「一行一条记录」，
	/// 异常栈里的换行会把它打散，解析器只能靠续行规则拼回来。
	public string Line(string? value) => Line(value, MaxLineChars);

	/// 同上，指定上限。
	public string Line(string? value, int maxChars)
	{
		var text = Collapse(Text(value));
		return text.Length <= maxChars
			? text
			: string.Create(CultureInfo.InvariantCulture, $"{text.AsSpan(0, maxChars)}…(+{text.Length - maxChars})");
	}

	/// 把多行压成一行：逐行去空白、丢掉空行、单空格连接。
	/// 不走 <c>Split</c>：每条导出记录都会过一遍这里，切出来的数组纯属白扔。
	private static string Collapse(string text)
	{
		var span = text.AsSpan();
		if (span.IndexOfAny('\r', '\n') < 0)
		{
			return text;
		}

		var builder = new StringBuilder(text.Length);
		var start = 0;
		while (true)
		{
			var end = span[start..].IndexOfAny('\r', '\n');
			var line = (end < 0 ? span[start..] : span.Slice(start, end)).Trim();
			if (!line.IsEmpty)
			{
				if (builder.Length > 0)
				{
					builder.Append(' ');
				}

				builder.Append(line);
			}

			if (end < 0)
			{
				return builder.ToString();
			}

			start += end + 1;
		}
	}
}
