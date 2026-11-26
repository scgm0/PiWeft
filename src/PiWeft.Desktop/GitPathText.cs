namespace PiWeft.Desktop;

/// 行名与路径显示的公共规则：git 的未跟踪目录条目以 `/` 结尾，按最后斜杠直接切会得到空串。
internal static class GitPathText
{
	/// 取路径最后一段；空路径原样返回，分隔符统一按 `/` 与 `\` 处理。
	public static string LeafName(string path)
	{
		if (path.Length == 0)
		{
			return path;
		}

		var trimmed = path.TrimEnd('/', '\\');
		if (trimmed.Length == 0)
		{
			return path;
		}

		var slash = trimmed.Replace('\\', '/').LastIndexOf('/');
		return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
	}
}
