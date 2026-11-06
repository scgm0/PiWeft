using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// Windows Git Bash 把 `> nul` 当普通文件（保留设备名，结束后删不掉），
/// pi 侧的 PiDeck 扩展会在 bash 调用前改写成 `/dev/null`；这里保留同一语义。
internal static partial class ShellCommandNormalizer
{
	/// 只处理引号外、未被转义的重定向目标；调用方负责按平台启用（仅 Windows）。
	public static string NormalizeNulRedirects(string command)
	{
		if (!command.Contains("nul", StringComparison.OrdinalIgnoreCase))
		{
			return command;
		}

		var result = new StringBuilder(command.Length);
		var inSingleQuotes = false;
		var inDoubleQuotes = false;
		var trailingBackslashes = 0;
		var index = 0;

		while (index < command.Length)
		{
			var current = command[index];
			if (current == '\\')
			{
				trailingBackslashes++;
				result.Append(current);
				index++;
				continue;
			}

			var escaped = trailingBackslashes % 2 == 1;
			trailingBackslashes = 0;

			if (current == '\'' && !inDoubleQuotes)
			{
				if (!inSingleQuotes && escaped)
				{
					result.Append(current);
					index++;
					continue;
				}

				inSingleQuotes = !inSingleQuotes;
				result.Append(current);
				index++;
				continue;
			}

			if (current == '"' && !inSingleQuotes && !escaped)
			{
				inDoubleQuotes = !inDoubleQuotes;
				result.Append(current);
				index++;
				continue;
			}

			if (!inSingleQuotes && !inDoubleQuotes && !escaped)
			{
				var match = RedirectNul().Match(command, index);
				if (match.Success && match.Index == index)
				{
					result.Append(match.Groups[1].Value).Append("/dev/null");
					index += match.Length;
					continue;
				}
			}

			result.Append(current);
			index++;
		}

		return result.ToString();
	}

	/// 保留 fd 前缀与操作符，只换目标；前瞻排除 `nul.txt` 这类文件名。
	[GeneratedRegex(@"\G([12]?(?:&?>>?|>&))\s*nul(?=\s|$|[|&;()<>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RedirectNul();
}
