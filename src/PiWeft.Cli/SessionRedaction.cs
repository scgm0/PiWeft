using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Cli;

/// 安全导出（`/share --safe`、`--export --safe`）的脱敏范围：工作目录、家目录、常见凭据。
internal sealed record RedactionScope(string? Cwd, string? Home)
{
	public static RedactionScope From(string? cwd) =>
		new(cwd, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// 把会话改写成可以放到共享链接里的版本。默认导出保持字节兼容，只有显式 `--safe` 才走这里。
/// 取舍是有损：没写进的字段按“看不到”处理，不从原文里猜。
internal static partial class SessionRedaction
{
	public const string HiddenToolResult = "[tool result hidden]";
	public const string HiddenSystemPrompt = "[system prompt hidden]";
	public const string HiddenImage = "[image hidden]";
	public const string HiddenWorkspace = "<workspace>";
	public const string HiddenSessionFile = "<session file>";

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

	public static string Text(string? value, RedactionScope scope)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? string.Empty;
		}

		var text = ReplacePaths(value, scope);
		text = PrivateKey().Replace(text, "[private key hidden]");
		text = BearerToken().Replace(text, "$1$2***");
		text = AssignedSecret().Replace(text, "$1$2$3***");
		text = UrlSecret().Replace(text, "$1***");
		text = KnownToken().Replace(text, "***");
		return text;
	}

	public static JsonNode? Json(JsonNode? node, RedactionScope scope) => node switch
	{
		null => null,
		JsonObject obj => new JsonObject(obj.Select(pair =>
			new KeyValuePair<string, JsonNode?>(pair.Key, Json(pair.Value, scope)))),
		JsonArray array => new JsonArray([.. array.Select(item => Json(item, scope))]),
		JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Text(text, scope)),
		_ => node.DeepClone(),
	};

	public static MessageContent Content(MessageContent content, RedactionScope scope) =>
		content.IsText
			? MessageContent.FromText(Text(content.Text, scope))
			: MessageContent.FromBlocks(Blocks(content.Blocks ?? [], scope));

	public static IReadOnlyList<ContentBlock> Blocks(IReadOnlyList<ContentBlock> blocks, RedactionScope scope) =>
	[
		.. blocks
			.Where(static block => block is not ThinkingContent)
			.Select(block => block switch
			{
				TextContent text => text with { Text = Text(text.Text, scope) },
				ImageContent => new TextContent { Text = HiddenImage },
				ToolCall call => call with
				{
					Arguments = new JsonObject { ["redacted"] = true },
					ArgumentsError = null,
					ThoughtSignature = null,
				},
				_ => block,
			}),
	];

	public static Message Message(Message message, RedactionScope scope) => message switch
	{
		SystemMessage system => system with
		{
			Content = MessageContent.FromText(HiddenSystemPrompt),
			Sections = null,
			ToolsAdded = null,
			ToolsRemoved = null,
			Extra = null,
		},
		UserMessage user => user with { Content = Content(user.Content, scope), Extra = null },
		AssistantMessage assistant => assistant with
		{
			Content = Blocks(assistant.Content, scope),
			Extra = null,
		},
		ToolResultMessage tool => tool with
		{
			Content = [new TextContent { Text = HiddenToolResult }],
			Details = null,
			Extra = null,
		},
		CustomMessage custom => custom with
		{
			Content = Content(custom.Content, scope),
			Raw = null,
			Extra = null,
		},
		_ => message,
	};

	public static SessionHeader Header(SessionHeader header, RedactionScope scope) => header with
	{
		Cwd = HiddenWorkspace,
		ParentSession = header.ParentSession is null ? null : HiddenSessionFile,
		Extra = null,
	};

	public static SessionEntry Entry(SessionEntry entry, RedactionScope scope) => entry switch
	{
		SessionMessageEntry message => message with { Message = Message(message.Message, scope), Extra = null },
		CompactionEntry compaction => compaction with
		{
			Summary = Text(compaction.Summary, scope),
			Details = null,
			SystemMessage = compaction.SystemMessage is { } system
				? (SystemMessage)Message(system, scope)
				: null,
			Extra = null,
		},
		BranchSummaryEntry branch => branch with
		{
			Summary = Text(branch.Summary, scope),
			Details = null,
			Extra = null,
		},
		CustomEntry custom => custom with { Data = Json(custom.Data, scope), Extra = null },
		CustomMessageEntry customMessage => customMessage with
		{
			Content = Content(customMessage.Content, scope),
			Details = null,
			Extra = null,
		},
		ContextEditEntry edit => edit with
		{
			Replacement = edit.Replacement is { } replacement ? Content(replacement, scope) : default(MessageContent?),
			Extra = null,
		},
		UsageEntry usage => usage with { Note = Text(usage.Note, scope), Extra = null },
		SessionInfoEntry info => info with { Name = Text(info.Name, scope), Extra = null },
		LabelEntry label => label with { Label = Text(label.Label, scope), Extra = null },
		DeletedEntry deleted => deleted with { Reason = Text(deleted.Reason, scope), Extra = null },
		UnknownSessionEntry unknown => unknown with
		{
			Raw = Json(unknown.Raw, scope) as JsonObject ?? new JsonObject(),
			Extra = null,
		},
		_ => entry with { Extra = null },
	};

	private static string ReplacePaths(string value, RedactionScope scope)
	{
		var text = value;
		if (scope.Cwd is { Length: > 1 } cwd)
		{
			text = ReplacePrefix(text, cwd, HiddenWorkspace, PathComparison);
		}

		if (scope.Home is { Length: > 1 } home &&
			!string.Equals(home, scope.Cwd, PathComparison))
		{
			text = ReplacePrefix(text, home, "~", PathComparison);
		}

		return text;
	}

	/// 只替换完整路径前缀：放在 <c>/home/user</c> 里不能把 <c>/home/username</c> 一起换掉。
	private static string ReplacePrefix(string text, string prefix, string replacement, StringComparison comparison)
	{
		var index = 0;
		var builder = new System.Text.StringBuilder(text.Length);
		while (index < text.Length)
		{
			var found = text.IndexOf(prefix, index, comparison);
			if (found < 0)
			{
				builder.Append(text, index, text.Length - index);
				break;
			}

			builder.Append(text, index, found - index);
			var after = found + prefix.Length;
			if (after == text.Length || !IsPathChar(text[after]))
			{
				builder.Append(replacement);
			}
			else
			{
				builder.Append(text, found, prefix.Length);
			}

			index = after;
		}

		return builder.ToString();
	}

	private static bool IsPathChar(char value) =>
		char.IsLetterOrDigit(value) || value is '_' or '-' or '.';

	[GeneratedRegex(
		@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
		RegexOptions.NonBacktracking)]
	private static partial Regex PrivateKey();

	[GeneratedRegex(
		@"(?i)\b(bearer)(\s+)[A-Za-z0-9\-._~+/]+=*",
		RegexOptions.NonBacktracking)]
	private static partial Regex BearerToken();

	[GeneratedRegex(
		@"(?i)\b(api[_-]?key|apikey|access[_-]?key|client[_-]?secret|password|passwd|secret|token)\b(\s*[:=]\s*)([""']?)[^\s""',}]{8,}",
		RegexOptions.NonBacktracking)]
	private static partial Regex AssignedSecret();

	[GeneratedRegex(
		@"(?i)([?&](?:token|key|api[_-]?key|access[_-]?token|sig|signature)=)[^&\s#]+",
		RegexOptions.NonBacktracking)]
	private static partial Regex UrlSecret();

	[GeneratedRegex(
		@"\b(?:sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abposr]-[A-Za-z0-9\-]{8,}|AIza[0-9A-Za-z_\-]{30,})",
		RegexOptions.NonBacktracking)]
	private static partial Regex KnownToken();
}
