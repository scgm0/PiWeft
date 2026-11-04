using System.Text.Json.Nodes;

namespace PiWeft.JavaScript;

/// 脚本可以调用的一项能力。参数与返回值都经过一次 JSON 往返，
/// 因此宿主永远拿不到脚本对象，脚本也拿不到宿主对象。
public sealed record CodeModeTool
{
	public required string Name { get; init; }

	public string? Description { get; init; }

	public string? SearchDescription { get; init; }

	public JsonNode? InputSchema { get; init; }

	public JsonNode? OutputSchema { get; init; }

	public string? SearchTerms { get; init; }

	public string? Namespace { get; init; }

	public IReadOnlyList<string>? NamespaceAliases { get; init; }

	public string? NamespaceDescription { get; init; }

	public string? NamespaceInstructions { get; init; }

	public required Func<JsonNode?, CancellationToken, Task<JsonNode?>> Execute { get; init; }
}

public enum CodeModeModelOperation
{
	List,

	Available,

	Get,
}

public sealed record CodeModeModelsRequest
{
	public required CodeModeModelOperation Operation { get; init; }

	public required string Type { get; init; }

	public string? Provider { get; init; }

	public string? Id { get; init; }
}

public abstract record CodeModeOutputItem;

public sealed record CodeModeTextItem : CodeModeOutputItem
{
	public required string Text { get; init; }

	public bool Console { get; init; }
}

public sealed record CodeModeImageItem : CodeModeOutputItem
{
	public required string Data { get; init; }

	public required string MimeType { get; init; }
}

public enum CodeModeCallStatus
{
	Ok,
	Error,
	Cancelled,
}

public sealed record CodeModeCall
{
	public required string Name { get; init; }

	public required CodeModeCallStatus Status { get; init; }

	public double DurationMs { get; init; }
}

public sealed record CodeModeModelCall
{
	public required string Operation { get; init; }

	public required string Provider { get; init; }

	public required string Id { get; init; }

	public JsonNode? Context { get; init; }
}

public sealed record CodeModeModelCallResult
{
	public required string Status { get; init; }

	public string? Error { get; init; }

	public JsonNode? Output { get; init; }

	public long InputTokens { get; init; }

	public long OutputTokens { get; init; }

	public long TotalTokens { get; init; }

	public double Cost { get; init; }

	public int GeneratedImages { get; init; }
}

public sealed record CodeModeModelCallRecord
{
	public required string Name { get; init; }

	public required string Model { get; init; }

	public required string Status { get; init; }

	public double DurationMs { get; init; }

	public double? Cost { get; init; }

	public string? Error { get; init; }
}

public sealed record CodeModeStoreWrites
{
	public static CodeModeStoreWrites Empty { get; } = new();

	public IReadOnlyDictionary<string, JsonNode?> Set { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

	public IReadOnlyList<string> Delete { get; init; } = [];
}

public enum CodeModeErrorKind
{
	Script,

	Timeout,

	Aborted,

	Sandbox,
}

public sealed record CodeModeError
{
	public required CodeModeErrorKind Kind { get; init; }

	public string? Name { get; init; }

	public required string Message { get; init; }

	public string? Stack { get; init; }
}

public sealed record CodeModeResult
{
	public bool Ok { get; init; }

	public JsonNode? Value { get; init; }

	public IReadOnlyList<CodeModeOutputItem> Output { get; init; } = [];

	public IReadOnlyList<CodeModeCall> Calls { get; init; } = [];

	public IReadOnlyList<CodeModeModelCallRecord> ModelCalls { get; init; } = [];

	public long InputTokens { get; init; }

	public long OutputTokens { get; init; }

	public long TotalTokens { get; init; }

	public double Cost { get; init; }

	public int GeneratedImages { get; init; }

	public CodeModeStoreWrites StoreWrites { get; init; } = CodeModeStoreWrites.Empty;

	public CodeModeError? Error { get; init; }
}

public sealed record CodeModeSandboxOptions
{
	public IReadOnlyList<CodeModeTool> Tools { get; init; } = [];

	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

	public long MemoryLimitBytes { get; init; } = 256L * 1024 * 1024;

	public int MaxStatements { get; init; } = 2_000_000;

	public int MaxSourceLength { get; init; } = 1024 * 1024;

	public long MaxOutputChars { get; init; } = 16L * 1024 * 1024;

	public int MaxOutputItems { get; init; } = 100_000;

	public Func<CodeModeModelsRequest, JsonNode?>? Models { get; init; }

	public Func<CodeModeModelCall, CancellationToken, Task<CodeModeModelCallResult>>? ModelCall { get; init; }
}

public sealed record CodeModeExecuteOptions
{
	public CancellationToken CancellationToken { get; init; }

	public IReadOnlyDictionary<string, JsonNode?> Store { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

	public TimeSpan? Timeout { get; init; }
}
