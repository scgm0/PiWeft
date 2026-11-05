using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.JavaScript;

/// 工具检索：与 pi 的 <c>extensions/tool-search/tool.ts</c> 对齐的 Okapi BM25
/// （k1 = 1.2、b = 0.75），名称权重 3、命名空间权重 0.5，并保留覆盖度、
/// 完整本地名加成和精确名优先排序。
public static partial class CodeModeSearch
{
	private const double K1 = 1.2;

	private const double B = 0.75;

	private const double NameWeight = 3;

	private const double NamespaceWeight = 0.5;

	private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
	{
		"a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it",
		"of", "on", "or", "that", "the", "this", "to", "with",
	};

	/// 建立 pi 的检索文档：description + schema 描述/属性名 + 可选附加词。
	public sealed record ToolSearchDocument(string Name, string Text, string? NamespaceText = null);

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

	/// 与 pi 的 <c>createToolSearchDocument</c> 同形：名称单独参与权重，不混进正文。
	public static ToolSearchDocument CreateToolSearchDocument(
		string name,
		string? description,
		JsonNode? inputSchema,
		string? searchTerms = null,
		string? namespaceText = null)
	{
		var parts = new List<string>();
		if (!string.IsNullOrWhiteSpace(description)) parts.Add(description);
		if (!string.IsNullOrWhiteSpace(searchTerms)) parts.Add(searchTerms);
		if (inputSchema is not null) SchemaText(inputSchema, parts);
		return new ToolSearchDocument(
			name,
			string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part))),
			string.IsNullOrWhiteSpace(namespaceText) ? null : namespaceText);
	}

	public static IReadOnlyList<string> Rank(
		string query,
		IReadOnlyList<ToolSearchDocument> documents,
		int limit)
	{
		if (limit <= 0 || documents.Count == 0 || string.IsNullOrWhiteSpace(query))
		{
			return [];
		}

		var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
		if (queryTerms.Count == 0)
		{
			return [];
		}

		var indexed = new List<IndexedDocument>(documents.Count);
		foreach (var document in documents)
		{
			indexed.Add(Index(document));
		}

		var averageLength = indexed.Sum(static document => document.TextLength) / (double)indexed.Count;
		if (averageLength <= 0) averageLength = 1;

		var idf = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (var term in queryTerms)
		{
			var frequency = indexed.Count(document =>
				document.NameTerms.Contains(term) ||
				document.TextCounts.ContainsKey(term) ||
				document.NamespaceTerms.Contains(term));
			idf[term] = Math.Log(1 + (indexed.Count - frequency + 0.5) / (frequency + 0.5));
		}

		var fullNameBonus = idf.Values.Sum();
		var normalizedQuery = NormalizeName(query);
		var matches = new List<Match>(indexed.Count);
		foreach (var document in indexed)
		{
			var norm = K1 * (1 - B + B * document.TextLength / averageLength);
			var score = 0.0;
			var covered = 0;
			foreach (var term in queryTerms)
			{
				var count = document.TextCounts.GetValueOrDefault(term);
				var weight = (document.NameTerms.Contains(term) ? NameWeight : 0) +
					(document.NamespaceTerms.Contains(term) ? NamespaceWeight : 0) +
					count * (K1 + 1) / (count + norm);
				if (weight > 0) covered++;
				score += idf[term] * weight;
			}

			if (score <= 0) continue;
			if (document.LocalNameTerms.Count > 0 &&
				document.LocalNameTerms.All(queryTerms.Contains))
			{
				score += fullNameBonus;
			}

			matches.Add(new Match(
				document.Name,
				score,
				covered,
				IsExactNameQuery(normalizedQuery, document.Name)));
		}

		return
		[
			.. matches
				.OrderByDescending(static match => match.Exact)
				.ThenByDescending(static match => match.Covered)
				.ThenByDescending(static match => match.Score)
				.Take(limit)
				.Select(static match => match.Name),
		];
	}

	/// 兼容旧的字符串文档调用；新代码应使用 <see cref="CreateToolSearchDocument"/>。
	public static IReadOnlyList<string> Rank(
		string query,
		IReadOnlyList<(string Name, string Text)> documents,
		int limit) =>
		Rank(
			query,
			[.. documents.Select(static document => new ToolSearchDocument(document.Name, document.Text))],
			limit);

	/// 已分词的旧调用；重新走同一套排名器，保证语义不分叉。
	public static IReadOnlyList<string> RankTerms(
		string query,
		IReadOnlyList<(string Name, IReadOnlyList<string> Terms)> documents,
		int limit) =>
		Rank(
			query,
			[.. documents.Select(static document => new ToolSearchDocument(document.Name, string.Join(" ", document.Terms)))],
			limit);

	public static IReadOnlyList<string> Tokenize(string text)
	{
		// 与 pi 相同的顺序：先拆小写→大写边界，再拆连续大写缩写。
		var spaced = CamelRun().Replace(CamelBoundary().Replace(text, "$1 $2"), "$1 $2");
		var lowered = spaced.ToLowerInvariant();

		// ASCII/拉丁词按空白与标点切；CJK 没有空格，pi 用 Intl.Segmenter 按词切，
		// .NET 没有等价物，这里退一步用「单字 + 二元组」：查询「截图」与描述里的
		// 「截取屏幕截图」共享二元组「截图」，至少能命中。只用 [^a-z0-9]+ 切会把
		// 整段中文当分隔符丢掉，中文查询必然零结果。
		var terms = new List<string>();
		var word = new StringBuilder();
		var ideographs = new List<char>(8);

		void FlushWord()
		{
			if (word.Length == 0) return;
			var token = word.ToString();
			word.Clear();
			if (token.Length == 0 || StopWords.Contains(token)) return;
			terms.Add(Stem(token));
		}

		void FlushIdeographs()
		{
			if (ideographs.Count == 0) return;
			for (var index = 0; index < ideographs.Count; index++)
			{
				terms.Add(ideographs[index].ToString());
				if (index > 0) terms.Add(string.Concat(ideographs[index - 1], ideographs[index]));
			}

			ideographs.Clear();
		}

		foreach (var c in lowered)
		{
			if (IsIdeograph(c))
			{
				FlushWord();
				ideographs.Add(c);
				continue;
			}

			FlushIdeographs();
			if (char.IsLetterOrDigit(c))
			{
				word.Append(c);
			}
			else
			{
				FlushWord();
			}
		}

		FlushWord();
		FlushIdeographs();
		return terms;
	}

	/// 汉字/假名/谚文：这些文字没有词间空格，按字与二元组切。
	private static bool IsIdeograph(char c) =>
		c is >= '\u3040' and <= '\u30FF' ||
		c is >= '\u3400' and <= '\u4DBF' ||
		c is >= '\u4E00' and <= '\u9FFF' ||
		c is >= '\uF900' and <= '\uFAFF' ||
		c is >= '\uAC00' and <= '\uD7AF';

	private static IndexedDocument Index(ToolSearchDocument document)
	{
		var nameTerms = Tokenize(document.Name).ToHashSet(StringComparer.Ordinal);
		var localName = document.Name.Split("__").LastOrDefault() ?? document.Name;
		var localNameTerms = Tokenize(localName).Distinct(StringComparer.Ordinal).ToList();
		var textCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var term in Tokenize(document.Text))
		{
			textCounts[term] = textCounts.GetValueOrDefault(term) + 1;
		}

		return new IndexedDocument(
			document.Name,
			nameTerms,
			localNameTerms,
			textCounts,
			textCounts.Values.Sum(),
			document.NamespaceText is { Length: > 0 }
				? Tokenize(document.NamespaceText).ToHashSet(StringComparer.Ordinal)
				: []);
	}

	private static string NormalizeName(string name) =>
		NonIdentifierCharacters().Replace(name.Trim().ToLowerInvariant(), "_");

	private static bool IsExactNameQuery(string normalizedQuery, string name)
	{
		var normalized = NormalizeName(name);
		return normalized == normalizedQuery || normalized.EndsWith($"__{normalizedQuery}", StringComparison.Ordinal);
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

	private sealed record IndexedDocument(
		string Name,
		HashSet<string> NameTerms,
		IReadOnlyList<string> LocalNameTerms,
		IReadOnlyDictionary<string, int> TextCounts,
		int TextLength,
		HashSet<string> NamespaceTerms);

	private sealed record Match(string Name, double Score, int Covered, bool Exact);

	[GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
	private static partial Regex CamelBoundary();

	[GeneratedRegex(@"([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
	private static partial Regex CamelRun();

	[GeneratedRegex(@"(ches|shes|sses|xes|zes)$", RegexOptions.CultureInvariant)]
	private static partial Regex PluralEnding();

	[GeneratedRegex(@"[^a-z0-9_$]", RegexOptions.CultureInvariant)]
	private static partial Regex NonIdentifierCharacters();
}
