using System.Text.Json.Nodes;

namespace PiWeft.JavaScript;

/// <summary>
/// 脚本可以调用的一项能力。参数与返回值都经过一次 JSON 往返，
/// 因此宿主永远拿不到脚本对象，脚本也拿不到宿主对象。
/// </summary>
public sealed record CodeModeTool
{
	/// <summary>工具的原始名称（可能包含 <c>-</c> 等不能做标识符的字符）。</summary>
	public required string Name { get; init; }

	/// <summary>面向脚本作者的说明：完整样例（描述 + TypeScript 声明），即 <c>ALL_TOOLS</c> 与 <c>describeTool()</c> 返回的内容。</summary>
	public string? Description { get; init; }

	/// <summary>检索语料用的原始描述（不含声明）；未设置时退回 <see cref="Description"/>。</summary>
	public string? SearchDescription { get; init; }

	/// <summary>参数 JSON Schema；只用于生成声明，不做校验。</summary>
	public JsonNode? InputSchema { get; init; }

	/// <summary>返回值 JSON Schema；只用于生成声明。</summary>
	public JsonNode? OutputSchema { get; init; }

	/// <summary>给 <c>searchTools()</c> 补上的关键词（标签、guideline 等），不进入声明。</summary>
	public string? SearchTerms { get; init; }

	/// <summary>工具所属的命名空间，供 <c>searchTools({ namespace })</c> 与 <c>describeNamespace()</c> 使用。</summary>
	public string? Namespace { get; init; }

	/// <summary>命名空间的别名（例如 MCP 服务器原名）。</summary>
	public IReadOnlyList<string>? NamespaceAliases { get; init; }

	/// <summary>命名空间的可读描述。</summary>
	public string? NamespaceDescription { get; init; }

	/// <summary>命名空间的使用说明（MCP 服务器的 instructions）。</summary>
	public string? NamespaceInstructions { get; init; }

	/// <summary>执行一次调用；参数为 <c>null</c> 表示脚本没有传参。</summary>
	public required Func<JsonNode?, CancellationToken, Task<JsonNode?>> Execute { get; init; }
}

/// <summary><c>models.*</c> 的三种查询，对应 pi 的 getModelsOfType/getAvailableOfType/getModelOfType。</summary>
public enum CodeModeModelOperation
{
	/// <summary>某一类型的全部目录项。</summary>
	List,

	/// <summary>只保留凭据可用 provider 的目录项。</summary>
	Available,

	/// <summary>按 provider + id 取一项；找不到时返回 <c>null</c>。</summary>
	Get,
}

/// <summary>脚本对 <c>models.*</c> 的一次查询；由宿主注入目录。</summary>
public sealed record CodeModeModelsRequest
{
	public required CodeModeModelOperation Operation { get; init; }

	/// <summary><c>chat</c>、<c>image</c> 或 <c>classifier</c>。</summary>
	public required string Type { get; init; }

	public string? Provider { get; init; }

	public string? Id { get; init; }
}

/// <summary>脚本产出的一条输出。</summary>
public abstract record CodeModeOutputItem;

/// <summary><c>text()</c> 或 <c>console.*</c> 产生的文本。</summary>
public sealed record CodeModeTextItem : CodeModeOutputItem
{
	public required string Text { get; init; }

	/// <summary>是否来自 <c>console.*</c>（排版时单独归组）。</summary>
	public bool Console { get; init; }
}

/// <summary><c>image()</c> 产生的图片；<see cref="Data"/> 是 base64。</summary>
public sealed record CodeModeImageItem : CodeModeOutputItem
{
	public required string Data { get; init; }

	public required string MimeType { get; init; }
}

/// <summary>一次嵌套调用的结局。</summary>
public enum CodeModeCallStatus
{
	Ok,
	Error,
	Cancelled,
}

/// <summary>脚本发起的一次工具调用（不含宿主辅助函数）。</summary>
public sealed record CodeModeCall
{
	public required string Name { get; init; }

	public required CodeModeCallStatus Status { get; init; }

	public double DurationMs { get; init; }
}

/// <summary><c>models.classify()</c> / <c>models.generateImages()</c> 的宿主调用。</summary>
public sealed record CodeModeModelCall
{
	/// <summary><c>classify</c> 或 <c>generateImages</c>。</summary>
	public required string Operation { get; init; }

	public required string Provider { get; init; }

	public required string Id { get; init; }

	public JsonNode? Context { get; init; }
}

/// <summary>模型调用的结果；<see cref="Output"/> 是脚本拿到的 JSON。</summary>
public sealed record CodeModeModelCallResult
{
	/// <summary><c>stop</c>、<c>error</c> 或 <c>aborted</c>。</summary>
	public required string Status { get; init; }

	public string? Error { get; init; }

	public JsonNode? Output { get; init; }

	public long InputTokens { get; init; }

	public long OutputTokens { get; init; }

	public long TotalTokens { get; init; }

	public double Cost { get; init; }

	/// <summary>生图调用返回的图片数（用于“生成但未展示”的提示）。</summary>
	public int GeneratedImages { get; init; }
}

/// <summary>详情里一次 <c>models.*</c> 调用；只用于界面展示。</summary>
public sealed record CodeModeModelCallRecord
{
	public required string Name { get; init; }

	public required string Model { get; init; }

	public required string Status { get; init; }

	public double DurationMs { get; init; }

	public double? Cost { get; init; }

	public string? Error { get; init; }
}

/// <summary><c>store()</c> 造成的改动；只有成功的执行才会上报。</summary>
public sealed record CodeModeStoreWrites
{
	public static CodeModeStoreWrites Empty { get; } = new();

	/// <summary>键 → 新值；值为 <c>null</c> 表示删除。</summary>
	public IReadOnlyDictionary<string, JsonNode?> Set { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

	public IReadOnlyList<string> Delete { get; init; } = [];
}

/// <summary>脚本失败的分类，与 pi 的 <c>CodemodeErrorKind</c> 对齐。</summary>
public enum CodeModeErrorKind
{
	/// <summary>脚本抛错或解析失败。</summary>
	Script,

	/// <summary>整体截止时间或语句预算用完。</summary>
	Timeout,

	/// <summary>调用方取消或沙箱被关闭。</summary>
	Aborted,

	/// <summary>解释器或传输本身失败（宿主侧的异常）。</summary>
	Sandbox,
}

/// <summary>失败详情；<see cref="Stack"/> 指向 <c>codemode.js:行</c>。</summary>
public sealed record CodeModeError
{
	public required CodeModeErrorKind Kind { get; init; }

	public string? Name { get; init; }

	public required string Message { get; init; }

	public string? Stack { get; init; }
}

/// <summary>一次脚本执行的完整结果；脚本失败不会以异常形式抛出。</summary>
public sealed record CodeModeResult
{
	public bool Ok { get; init; }

	/// <summary>脚本 <c>return</c> 的值；失败或没有返回值时为 <c>null</c>。</summary>
	public JsonNode? Value { get; init; }

	/// <summary>按脚本产生顺序排列的输出，失败时保留失败前已产生的部分。</summary>
	public IReadOnlyList<CodeModeOutputItem> Output { get; init; } = [];

	public IReadOnlyList<CodeModeCall> Calls { get; init; } = [];

	/// <summary><c>models.*</c> 调用；计入详情与用量。</summary>
	public IReadOnlyList<CodeModeModelCallRecord> ModelCalls { get; init; } = [];

	public long InputTokens { get; init; }

	public long OutputTokens { get; init; }

	public long TotalTokens { get; init; }

	public double Cost { get; init; }

	/// <summary><c>models.generateImages()</c> 返回的图片总数。</summary>
	public int GeneratedImages { get; init; }

	public CodeModeStoreWrites StoreWrites { get; init; } = CodeModeStoreWrites.Empty;

	public CodeModeError? Error { get; init; }
}

/// <summary>沙箱级配置；每次执行都会新建一个解释器实例。</summary>
public sealed record CodeModeSandboxOptions
{
	public IReadOnlyList<CodeModeTool> Tools { get; init; } = [];

	/// <summary>整体时限（含工具调用时间）；<see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 表示不限。</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>解释器托管分配上限，替换默认的 16 MB。</summary>
	public long MemoryLimitBytes { get; init; } = 256L * 1024 * 1024;

	/// <summary>语句预算；用完后按超时报告。</summary>
	public int MaxStatements { get; init; } = 2_000_000;

	public int MaxSourceLength { get; init; } = 1024 * 1024;

	/// <summary>脚本可以产生的文本与 base64 图片字符总量。</summary>
	public long MaxOutputChars { get; init; } = 16L * 1024 * 1024;

	public int MaxOutputItems { get; init; } = 100_000;

	/// <summary><c>models.*</c> 目录查询的宿主桥；<c>null</c> 时脚本调用目录查询会报错。</summary>
	public Func<CodeModeModelsRequest, JsonNode?>? Models { get; init; }

	/// <summary><c>models.classify()</c> / <c>models.generateImages()</c> 的宿主桥。</summary>
	public Func<CodeModeModelCall, CancellationToken, Task<CodeModeModelCallResult>>? ModelCall { get; init; }
}

/// <summary>单次执行的选项。</summary>
public sealed record CodeModeExecuteOptions
{
	/// <summary>调用方取消；会中断等待中的工具调用。</summary>
	public CancellationToken CancellationToken { get; init; }

	/// <summary><c>load()</c> 能读到的值。</summary>
	public IReadOnlyDictionary<string, JsonNode?> Store { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

	/// <summary>覆盖沙箱默认时限。</summary>
	public TimeSpan? Timeout { get; init; }
}
