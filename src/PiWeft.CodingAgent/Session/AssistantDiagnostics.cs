using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// 助手消息诊断的读取与措辞，与 pi 的会话提示同义：供应商在请求里剔掉了一些思考块，
/// 用一行 warning 告诉用户，完整清单留在会话文件的 <c>diagnostics</c> 里。
public static class AssistantDiagnostics
{
	/// 一次被供应商丢弃的思考块统计。
	/// <param name="Provider">提示里的供应商名（<c>OpenAI</c> / <c>Anthropic</c>）。</param>
	/// <param name="Count">被丢弃的块数。</param>
	/// <param name="Reasons">供应商给出的原因，按出现顺序去重。</param>
	public sealed record DroppedReasoning(string Provider, int Count, IReadOnlyList<string> Reasons)
	{
		/// 与 pi 相同的单行提示：<c>OpenAI dropped 2 reasoning items: prefix_mismatch (details in session)</c>。
		public string Notice
		{
			get
			{
				var noun = Count == 1
					? Provider == "Anthropic" ? "thinking block" : "reasoning item"
					: Provider == "Anthropic" ? "thinking blocks" : "reasoning items";
				var reasons = Reasons.Count > 0 ? $": {string.Join(", ", Reasons)}" : string.Empty;
				return $"{Provider} dropped {Count} {noun}{reasons} (details in session)";
			}
		}
	}

	/// 取第一条报告了丢弃的 <c>*_input_transformations</c> 诊断；没有则 <c>null</c>。
	public static DroppedReasoning? DroppedReasoningOf(IReadOnlyList<AssistantDiagnostic>? diagnostics)
	{
		if (diagnostics is not { Count: > 0 })
		{
			return null;
		}

		foreach (var diagnostic in diagnostics)
		{
			var (provider, droppedType) = diagnostic.Type switch
			{
				"openai_input_transformations" => ("OpenAI", "reasoning"),
				"anthropic_input_transformations" => ("Anthropic", "thinking_dropped"),
				_ => (null, null),
			};
			if (provider is null ||
				diagnostic.Details?["transformations"] is not JsonArray transformations)
			{
				continue;
			}

			var count = 0;
			var reasons = new List<string>();
			foreach (var entry in transformations)
			{
				// 会话文件来自外部写入：字段缺失或类型不对都不能让界面投影抛异常。
				if (entry is not JsonObject transformation ||
					transformation["type"] is not JsonValue typeValue ||
					!typeValue.TryGetValue<string>(out var type) ||
					!string.Equals(type, droppedType, StringComparison.Ordinal))
				{
					continue;
				}

				count++;
				if (transformation["reason"] is JsonValue reasonValue &&
					reasonValue.TryGetValue<string>(out var reason) &&
					reason.Length > 0 &&
					!reasons.Contains(reason, StringComparer.Ordinal))
				{
					reasons.Add(reason);
				}
			}

			if (count > 0)
			{
				return new DroppedReasoning(provider, count, reasons);
			}
		}

		return null;
	}
}
