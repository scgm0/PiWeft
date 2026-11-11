using System.Text.Json;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// provider 模型的目录。默认注册表加载上游 pi 版本固定的模型目录，
/// 以内嵌资源 <c>Resources/models.json</c> 的形式存在，因此离线与 NativeAOT 环境都能用。
public sealed class ModelRegistry
{
	/// 内嵌目录的资源名后缀；在线目录更新用它校验兜底基线。
	public const string EmbeddedCatalogResourceSuffix = "Resources.models.json";

	private readonly Dictionary<string, Model> _byKey = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<Model>> _byProvider = new(StringComparer.Ordinal);
	private readonly Dictionary<string, VirtualModelDefinition> _virtual = new(StringComparer.Ordinal);

	public int Count => _byKey.Count;

	public IReadOnlyCollection<string> Providers => _byProvider.Keys;

	public ModelRegistry()
	{
	}

	public void Add(Model model)
	{
		ArgumentNullException.ThrowIfNull(model);
		var key = Key(model.Provider, model.Id);
		if (!VirtualModels.IsVirtual(model))
		{
			_virtual.Remove(key);
		}

		_byKey[key] = model;
		if (!_byProvider.TryGetValue(model.Provider, out var list))
		{
			list = [];
			_byProvider[model.Provider] = list;
		}

		list.RemoveAll(m => string.Equals(m.Id, model.Id, StringComparison.Ordinal));
		list.Add(model);
	}

	public Model? Get(string provider, string id) =>
		_byKey.TryGetValue(Key(provider, id), out var model) ? model : null;

	public void AddVirtual(VirtualModelDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		var key = Key(definition.Provider, definition.Id);
		if (_byKey.TryGetValue(key, out var existing) && !VirtualModels.IsVirtual(existing))
		{
			throw new InvalidOperationException(
				$"Virtual model {definition.Provider}/{definition.Id} conflicts with a physical model.");
		}

		_virtual[key] = definition;
		Add(VirtualModels.CreateCatalogModel(definition));
	}

	public VirtualModelDefinition? Virtual(string provider, string id) =>
		_virtual.TryGetValue(Key(provider, id), out var definition) ? definition : null;

	public bool RemoveVirtual(string provider, string id)
	{
		var key = Key(provider, id);
		if (!_virtual.Remove(key))
		{
			return false;
		}

		return Remove(provider, id);
	}

	public bool Remove(string provider, string id)
	{
		var key = Key(provider, id);
		_virtual.Remove(key);
		if (!_byKey.Remove(key))
		{
			return false;
		}

		if (_byProvider.TryGetValue(provider, out var list))
		{
			list.RemoveAll(model => string.Equals(model.Id, id, StringComparison.Ordinal));
			if (list.Count == 0)
			{
				_byProvider.Remove(provider);
			}
		}

		return true;
	}

	public IReadOnlyList<Model> List(string? provider = null)
	{
		if (provider is null)
		{
			return [.. _byKey.Values];
		}

		return _byProvider.TryGetValue(provider, out var list) ? [.. list] : [];
	}

	public IReadOnlyList<Model> ChatModels(string? provider = null) =>
		[.. List(provider).Where(m => m.Type == ModelType.Chat)];

	public IReadOnlyList<Model> ImageModels(string? provider = null) =>
		[.. List(provider).Where(m => m.Type == ModelType.Image)];

	public IReadOnlyList<Model> ClassifierModels(string? provider = null) =>
		[.. List(provider).Where(m => m.Type == ModelType.Classifier)];

	public Model? Resolve(string providerOrModel, string? modelId = null)
	{
		if (modelId is not null)
		{
			return Get(providerOrModel, modelId);
		}

		// "provider/id" 直接解析，因为目录的键就是这么存的。
		if (_byKey.TryGetValue(providerOrModel, out var qualified))
		{
			return qualified;
		}

		Model? match = null;
		foreach (var candidate in FindById(providerOrModel))
		{
			if (match is not null)
			{
				return null; // 有歧义
			}

			match = candidate;
		}

		return match;
	}

	public IReadOnlyList<Model> FindById(string modelId) =>
		[.. _byKey.Values.Where(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))];

	/// 打开内嵌模型目录。调用方负责释放。
	public static Stream OpenEmbeddedCatalog()
	{
		var assembly = typeof(ModelRegistry).Assembly;
		var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(EmbeddedCatalogResourceSuffix, StringComparison.Ordinal))
			?? throw new InvalidOperationException($"embedded model catalog '{EmbeddedCatalogResourceSuffix}' is missing");

		return assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"embedded model catalog '{name}' could not be opened");
	}

	/// 读取内嵌模型目录的新注册表。
	public static ModelRegistry LoadDefault()
	{
		using var stream = OpenEmbeddedCatalog();
		var registry = new ModelRegistry();
		registry.AddCatalog(stream);
		return registry;
	}

	/// 合并一份 <c>provider → 模型</c> 目录，格式与内嵌 <c>models.json</c> 相同。
	/// 条目缺 id 时整份拒绝：在线目录是一个文件，缺字段通常意味着下载被截断，
	/// 半份目录比没有目录更难排查。provider 以分组键为准。
	/// <returns>合入的模型数。</returns>
	public int AddCatalog(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		var providers = JsonSerializer.Deserialize(stream, PiJsonContext.Default.DictionaryStringListModel)
			?? throw new JsonException("model catalog was empty");

		var incoming = new List<Model>();
		foreach (var (providerId, models) in providers)
		{
			if (string.IsNullOrWhiteSpace(providerId))
			{
				throw new JsonException("model catalog has an empty provider id");
			}

			foreach (var model in models)
			{
				if (string.IsNullOrWhiteSpace(model.Id))
				{
					throw new JsonException($"model catalog entry of '{providerId}' has an empty id");
				}

				// OpenRouter 对 openrouter/auto 之类的路由模型报 -1 作为「价格取决于它选中的模型」的
				// 占位值；pi 把负价一律当 0（否则界面会显示 -$1,000,000/1M）。
				incoming.Add(SanitizeCost(model with { Provider = providerId }));
			}
		}

		if (incoming.Count == 0)
		{
			throw new JsonException("model catalog has no models");
		}

		foreach (var model in incoming)
		{
			Add(model);
		}

		return incoming.Count;
	}

	internal static Model SanitizeCost(Model model)
	{
		var cost = model.Cost;
		if (cost.Input >= 0 && cost.Output >= 0 && cost.CacheRead >= 0 && cost.CacheWrite >= 0)
		{
			return model;
		}

		return model with
		{
			Cost = cost with
			{
				Input = Math.Max(0, cost.Input),
				Output = Math.Max(0, cost.Output),
				CacheRead = Math.Max(0, cost.CacheRead),
				CacheWrite = Math.Max(0, cost.CacheWrite),
			},
		};
	}

	private static string Key(string provider, string id) => provider + "/" + id;
}
