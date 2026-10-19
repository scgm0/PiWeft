using System.Text.Json;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// <summary>
/// provider 模型的目录。默认注册表加载上游 pi 版本固定的模型目录，
/// 以内嵌资源 <c>Resources/models.json</c> 的形式存在，因此离线与 NativeAOT 环境都能用。
/// </summary>
public sealed class ModelRegistry
{
	private const string ResourceSuffix = "Resources.models.json";

	private readonly Dictionary<string, Model> _byKey = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<Model>> _byProvider = new(StringComparer.Ordinal);
	private readonly Dictionary<string, VirtualModelDefinition> _virtual = new(StringComparer.Ordinal);

	public int Count => _byKey.Count;

	public IReadOnlyCollection<string> Providers => _byProvider.Keys;

	/// <summary>创建一个空注册表。</summary>
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

	/// <summary>注册虚拟模型；同名的物理模型会阻止注册，避免路由目标被遮蔽。</summary>
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

	/// <summary>查找虚拟模型定义；普通模型返回 null。</summary>
	public VirtualModelDefinition? Virtual(string provider, string id) =>
		_virtual.TryGetValue(Key(provider, id), out var definition) ? definition : null;

	/// <summary>移除虚拟模型定义与目录项。</summary>
	public bool RemoveVirtual(string provider, string id)
	{
		var key = Key(provider, id);
		if (!_virtual.Remove(key))
		{
			return false;
		}

		return Remove(provider, id);
	}

	/// <summary>移除一个模型；返回是否存在。</summary>
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

	/// <summary>列出模型，可限定某个 provider。</summary>
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

	/// <summary>解析 <c>provider/model</c>；找不到时退回跨 provider 的唯一 id 匹配。</summary>
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

	/// <summary>所有与该模型 id 同名的目录条目（可能分布在多个 provider）。</summary>
	public IReadOnlyList<Model> FindById(string modelId) =>
		[.. _byKey.Values.Where(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))];

	public static ModelRegistry LoadDefault()
	{
		var assembly = typeof(ModelRegistry).Assembly;
		var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
			?? throw new InvalidOperationException($"embedded model catalog '{ResourceSuffix}' is missing");

		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"embedded model catalog '{name}' could not be opened");

		var providers = JsonSerializer.Deserialize(stream, PiJsonContext.Default.DictionaryStringListModel)
			?? throw new JsonException("embedded model catalog was empty");

		var registry = new ModelRegistry();
		foreach (var (_, models) in providers)
		{
			foreach (var model in models)
			{
				// OpenRouter 对 openrouter/auto 之类的路由模型报 -1 作为「价格取决于它选中的模型」的
				// 占位值；pi 把负价一律当 0（否则界面会显示 -$1,000,000/1M）。
				registry.Add(SanitizeCost(model));
			}
		}

		return registry;
	}

	/// <summary>把负的价格项钳到 0（占位值不是真实价格）。</summary>
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
