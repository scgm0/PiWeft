namespace PiWeft.Desktop;

/// <summary>Only web and local file links from model output may start an external application.</summary>
internal sealed record MessageLinkTarget(string Target, bool IsWeb, int? Line = null, int? Column = null)
{
	public static MessageLinkTarget? Resolve(string url, string cwd)
	{
		try
		{
			var value = url.Trim();
			if (value.Length == 0 || value.Any(char.IsControl) || value.StartsWith('#')) return null;
			var locatedFile = System.Text.RegularExpressions.Regex.IsMatch(value, @"^[^:]+\.[^:/]+:\d+(?::\d+)?$");
			if (!locatedFile && Uri.TryCreate(value, UriKind.Absolute, out var uri))
			{
				if (uri.Scheme is "http" or "https") return new(uri.AbsoluteUri, true);
				if (!uri.IsFile) return null;
				// Reject remote file shares. They must not silently send local credentials.
				if (uri.Host.Length > 0 && uri.Host != "localhost") return null;
				value = uri.LocalPath + uri.Fragment;
			}
			else value = Uri.UnescapeDataString(value);

			if (value.StartsWith("~/", StringComparison.Ordinal))
				value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
			var direct = Path.GetFullPath(value, cwd);
			if (File.Exists(direct) || Directory.Exists(direct)) return new(direct, false);

			int? line = null, column = null;
			var suffix = System.Text.RegularExpressions.Regex.Match(value, @"(?::(\d+)(?::(\d+))?|#L(\d+)(?:C(\d+))?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			if (suffix.Success)
			{
				var lineText = suffix.Groups[1].Success ? suffix.Groups[1].Value : suffix.Groups[3].Value;
				var columnText = suffix.Groups[2].Success ? suffix.Groups[2].Value : suffix.Groups[4].Value;
				if (int.TryParse(lineText, out var parsedLine) && parsedLine > 0) line = parsedLine;
				if (int.TryParse(columnText, out var parsedColumn) && parsedColumn > 0) column = parsedColumn;
				value = value[..suffix.Index];
			}
			return new(Path.GetFullPath(value, cwd), false, line, column);
		}
		catch (Exception error) when (error is ArgumentException or NotSupportedException or UriFormatException or PathTooLongException)
		{
			return null;
		}
	}
}
