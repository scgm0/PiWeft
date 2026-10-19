using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <summary>结构化分类问题；对应 pi 的 <c>ClassifierQuestion</c> 联合类型。</summary>
public abstract record ClassifierQuestion
{
	public abstract string Type { get; }

	public string Instructions { get; init; } = string.Empty;
}

public sealed record ClassifierChoiceQuestion : ClassifierQuestion
{
	public override string Type => "choice";

	/// <summary>选项值到说明的映射。</summary>
	public IReadOnlyDictionary<string, string> Criteria { get; init; } = new Dictionary<string, string>();
}

public sealed record ClassifierScoreQuestion : ClassifierQuestion
{
	public override string Type => "score";

	/// <summary>从低到高的评分档位标签。</summary>
	public IReadOnlyList<string> Criteria { get; init; } = [];
}

public sealed record ClassifierBoolQuestion : ClassifierQuestion
{
	public override string Type => "bool";

	public string TrueMeaning { get; init; } = string.Empty;

	public string FalseMeaning { get; init; } = string.Empty;
}

/// <summary>一次分类请求：共享状态、可选图片以及一组具名问题。</summary>
public sealed record ClassifierContext
{
	public JsonObject State { get; init; } = new();

	public IReadOnlyList<ImageContent>? Images { get; init; }

	public IReadOnlyDictionary<string, ClassifierQuestion> Questions { get; init; } =
		new Dictionary<string, ClassifierQuestion>();
}

public abstract record ClassifierAnswer
{
	public abstract string Type { get; }
}

public sealed record ClassifierChoiceAnswer : ClassifierAnswer
{
	public override string Type => "choice";

	public string Choice { get; init; } = string.Empty;

	public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
		new Dictionary<string, double>();

	public double Confidence { get; init; }
}

public sealed record ClassifierScoreAnswer : ClassifierAnswer
{
	public override string Type => "score";

	public double Score { get; init; }

	public double Confidence { get; init; }
}

public sealed record ClassifierBoolAnswer : ClassifierAnswer
{
	public override string Type => "bool";

	public double Probability { get; init; }
}

/// <summary>一次分类的结果；失败也通过结果返回。</summary>
public sealed record ClassifierResult
{
	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string Model { get; init; } = string.Empty;

	public IReadOnlyDictionary<string, ClassifierAnswer> Answers { get; init; } =
		new Dictionary<string, ClassifierAnswer>();

	public Usage? Usage { get; init; }

	public StopReason StopReason { get; init; } = StopReason.Stop;

	public string? ErrorMessage { get; init; }

	public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>一种分类 API 的实现，按 <see cref="Api"/> 索引。</summary>
public interface IClassifierApiProvider
{
	string Api { get; }

	Task<ClassifierResult> ClassifyAsync(
		Model model,
		ClassifierContext context,
		ProviderRequestOptions options,
		CancellationToken cancellationToken = default);
}

/// <summary>按模型声明的 <c>api</c> 分发到分类实现。</summary>
public sealed class ClassifiersGateway
{
	private readonly Dictionary<string, IClassifierApiProvider> _providers = new(StringComparer.Ordinal);

	public void Register(IClassifierApiProvider provider)
	{
		ArgumentNullException.ThrowIfNull(provider);
		_providers[provider.Api] = provider;
	}

	public Task<ClassifierResult> ClassifyAsync(
		Model model,
		ClassifierContext context,
		ProviderRequestOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(model);
		if (!_providers.TryGetValue(model.Api, out var provider))
		{
			return Task.FromResult(new ClassifierResult
			{
				Api = model.Api,
				Provider = model.Provider,
				Model = model.Id,
				StopReason = StopReason.Error,
				ErrorMessage = $"No classifier provider registered for api: {model.Api}",
			});
		}

		return provider.ClassifyAsync(model, context, options ?? new ProviderRequestOptions(), cancellationToken);
	}
}
