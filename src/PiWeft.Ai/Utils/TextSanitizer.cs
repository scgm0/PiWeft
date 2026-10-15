using System.Text;

namespace PiWeft.Ai.Utils;

/// <summary>各 provider 共用的文本辅助方法。</summary>
public static class TextSanitizer
{
	/// <summary>
	/// 丢弃落单的 UTF-16 代理项。含孤立代理项的请求会被 provider 拒绝或破坏，而这类字符正是
	/// 流在码点中间被截断时产生的。
	/// </summary>
	public static string SanitizeSurrogates(string value)
	{
		var needsWork = false;
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			if (char.IsHighSurrogate(c))
			{
				if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
				{
					needsWork = true;
					break;
				}

				i++;
			}
			else if (char.IsLowSurrogate(c))
			{
				needsWork = true;
				break;
			}
		}

		if (!needsWork)
		{
			return value;
		}

		var builder = new StringBuilder(value.Length);
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			if (char.IsHighSurrogate(c))
			{
				if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
				{
					builder.Append(c).Append(value[i + 1]);
					i++;
				}

				continue;
			}

			if (char.IsLowSurrogate(c))
			{
				continue;
			}

			builder.Append(c);
		}

		return builder.ToString();
	}
}
