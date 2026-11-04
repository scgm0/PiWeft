namespace PiWeft.JavaScript;

public static class CodeModeIdentifier
{
	/// 脚本使用的标识符：不能出现在 JavaScript 标识符里的字符换成 <c>_</c>。
	/// <c>mcp__docs__search</c> 原样保留，<c>my-tool</c> 变成 <c>my_tool</c>。
	public static string ToIdentifier(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		var identifier = new System.Text.StringBuilder(name.Length);
		foreach (var rune in name.EnumerateRunes())
		{
			var valid = identifier.Length == 0
				? IsIdentifierStart(rune)
				: IsIdentifierPart(rune);
			identifier.Append(valid ? rune.ToString() : "_");
		}

		return identifier.Length == 0 ? "_" : identifier.ToString();
	}

	private static bool IsIdentifierStart(System.Text.Rune rune) =>
		rune.Value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') || rune.Value == '_' || rune.Value == '$';

	private static bool IsIdentifierPart(System.Text.Rune rune) =>
		IsIdentifierStart(rune) || rune.Value is >= '0' and <= '9';
}
