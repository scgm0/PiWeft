using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.JavaScript;

/// <summary>
/// <c>searchTools()</c>/<c>describeTool()</c> 使用的工具检索：与 pi 的
/// <c>extensions/tool-search/tool.ts</c> 同一套分词与 Okapi BM25 参数（k1 = 1.2，b = 0.75），
/// 文档是工具名、描述、参数 schema 的说明与属性名，以及可选的关键词。
/// </summary>
public static partial class CodeModeSearch
{
	private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
	{
		"a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it",
		"of", "on", "or", "that", "the", "this", "to", "with",
	};

	/// <summary>构造排名字段；<paramref name="searchTerms"/> 用于补上工具标签等关键词。</summary>
	public static string CreateDocument(
		string name,
		string? description,
		JsonNode? inputSchema,
		string? searchTerms = null)
	{
		var parts = new List<string> { name, name.Replace('_', ' ') };
		if (!string.IsNullOrWhiteSpace(description)) parts.Add(description);
		if (searchTerms is not null) parts.Add(searchTerms);
		if (inputSchema is not null) SchemaText(inputSchema, parts);
		return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
	}

	/// <summary>返回与查询相关的工具名，按分数降序；同分保持传入顺序。</summary>
	public static IReadOnlyList<string> Rank(
		string query,
		IReadOnlyList<(string Name, string Text)> documents,
		int limit)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
		if (string.IsNullOrWhiteSpace(query) || documents.Count == 0) return [];

		var tokenized = new (string Name, IReadOnlyList<string> Terms)[documents.Count];
		for (var index = 0; index < documents.Count; index++)
		{
			tokenized[index] = (documents[index].Name, Tokenize(documents[index].Text));
		}

		return RankTerms(query, tokenized, limit);
	}

	/// <summary>
	/// 与 <see cref="Rank"/> 相同，但文档已预先分词：同一个脚本里多次 <c>searchTools()</c>
	/// 复用第一次的分词结果，而不是每条查询重新扫描全部 schema 文本。
	/// </summary>
	public static IReadOnlyList<string> RankTerms(
		string query,
		IReadOnlyList<(string Name, IReadOnlyList<string> Terms)> documents,
		int limit)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
		if (string.IsNullOrWhiteSpace(query) || documents.Count == 0) return [];

		var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
		if (queryTerms.Count == 0) return [];

		var counts = new List<Dictionary<string, int>>(documents.Count);
		var lengths = new List<int>(documents.Count);
		foreach (var document in documents)
		{
			var termCounts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var term in document.Terms)
			{
				termCounts[term] = termCounts.GetValueOrDefault(term) + 1;
			}

			counts.Add(termCounts);
			lengths.Add(document.Terms.Count);
		}

		var totalLength = 0;
		foreach (var length in lengths) totalLength += length;
		var averageLength = totalLength / (double)documents.Count;
		if (averageLength <= 0) averageLength = 1;

		var idf = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (var term in queryTerms)
		{
			var frequency = counts.Count(count => count.ContainsKey(term));
			idf[term] = Math.Log(1 + (documents.Count - frequency + 0.5) / (frequency + 0.5));
		}

		const double k1 = 1.2;
		const double b = 0.75;
		var matches = new List<(string Name, double Score)>();
		for (var index = 0; index < documents.Count; index++)
		{
			var score = 0.0;
			foreach (var term in queryTerms)
			{
				if (!counts[index].TryGetValue(term, out var count) || count == 0) continue;
				var norm = k1 * (1 - b + (b * lengths[index] / averageLength));
				score += idf[term] * (count * (k1 + 1) / (count + norm));
			}

			if (score > 0) matches.Add((documents[index].Name, score));
		}

		return
		[
			.. matches
				.OrderByDescending(static match => match.Score)
				.Take(limit)
				.Select(static match => match.Name),
		];
	}

	/// <summary>按 pi 的规则切词：camelCase 边界、小写、去停用词、朴素单数化。</summary>
	public static IReadOnlyList<string> Tokenize(string text)
	{
		// 与 pi 相同的顺序：先拆小写→大写边界，再拆连续大写缩写。
		var spaced = CamelRun().Replace(CamelBoundary().Replace(text, "$1 $2"), "$1 $2");
		var terms = new List<string>();
		foreach (var raw in Separator().Split(spaced.ToLowerInvariant()))
		{
			if (raw.Length == 0 || StopWords.Contains(raw)) continue;
			terms.Add(Stem(raw));
		}

		return terms;
	}

	private static string Stem(string term)
	{
		if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
		{
			return string.Concat(term.AsSpan(0, term.Length - 3), "y");
		}

		if (term.Length > 4 && PluralEnding().IsMatch(term))
		{
			return term[..^2];
		}

		if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal))
		{
			return term[..^1];
		}

		return term;
	}

	/// <summary>递归收集 schema 里的说明文字与参数名。</summary>
	private static void SchemaText(JsonNode? schema, List<string> parts)
	{
		if (schema is not JsonObject node) return;

		if (node["description"] is JsonValue description && description.TryGetValue<string>(out var text))
		{
			parts.Add(text);
		}

		if (node["properties"] is JsonObject properties)
		{
			foreach (var (name, property) in properties)
			{
				parts.Add(name);
				SchemaText(property, parts);
			}
		}

		SchemaText(node["items"], parts);
		foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
		{
			if (node[key] is not JsonArray variants) continue;
			foreach (var variant in variants) SchemaText(variant, parts);
		}
	}

	[GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
	private static partial Regex CamelBoundary();

	[GeneratedRegex(@"([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
	private static partial Regex CamelRun();

	[GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
	private static partial Regex Separator();

	[GeneratedRegex(@"(ches|shes|sses|xes|zes)$", RegexOptions.CultureInvariant)]
	private static partial Regex PluralEnding();
}
