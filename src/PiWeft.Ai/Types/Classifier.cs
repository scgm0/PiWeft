using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <c>closed</c>：问题种类固定在程序集内，提供商层的 switch 由编译器验证兑尽。
public closed record ClassifierQuestion
{
	public abstract string Type { get; }

	public string Instructions { get; init; } = string.Empty;
}

public sealed record ClassifierChoiceQuestion : ClassifierQuestion
{
	public override string Type => "choice";

	public IReadOnlyDictionary<string, string> Criteria { get; init; } = new Dictionary<string, string>();
}

public sealed record ClassifierScoreQuestion : ClassifierQuestion
{
	public override string Type => "score";

	public IReadOnlyList<string> Criteria { get; init; } = [];
}

public sealed record ClassifierBoolQuestion : ClassifierQuestion
{
	public override string Type => "bool";

	public string TrueMeaning { get; init; } = string.Empty;

	public string FalseMeaning { get; init; } = string.Empty;
}

public sealed record ClassifierContext
{
	public JsonObject State { get; init; } = new();

	public IReadOnlyList<ImageContent>? Images { get; init; }

	public IReadOnlyDictionary<string, ClassifierQuestion> Questions { get; init; } =
		new Dictionary<string, ClassifierQuestion>();
}

/// <c>closed</c>：答案种类固定在程序集内，解析侧的 switch 由编译器验证兑尽。
public closed record ClassifierAnswer
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

public interface IClassifierApiProvider
{
	string Api { get; }

	Task<ClassifierResult> ClassifyAsync(
		Model model,
		ClassifierContext context,
		ProviderRequestOptions options,
		CancellationToken cancellationToken = default);
}

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
