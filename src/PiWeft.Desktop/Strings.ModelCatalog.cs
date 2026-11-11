using System.Globalization;

namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string SettingsModelCatalog = "模型目录";
	public const string ModelCatalogDescription =
		"内置目录随版本发布；这里从 pi-ai 取最新目录覆盖到 agent 目录，没有 npm 覆盖版时可用 GitHub main 兜底。覆盖文件可随时还原。";
	public const string ModelCatalogSource = "目录更新源";
	public const string ModelCatalogSourceValue = "pi-ai npm（最新）· GitHub main 兜底";
	public const string ModelCatalogBuiltin = "内置目录";
	public const string ModelCatalogOverlay = "覆盖版本";
	public const string ModelCatalogBackup = "备份";
	public const string ModelCatalogNone = "无";
	public const string ModelCatalogInvalid = "文件无效";
	public const string ModelCatalogHasBackup = "有（上一个覆盖版）";
	public const string ModelCatalogAutoUpdate = "自动更新模型目录";
	public const string ModelCatalogAutoUpdateDesc = "启动后检查一次，24 小时内不重复；发现新版本时自动拉取并启用。";
	public const string ModelCatalogCheck = "检查更新";
	public const string ModelCatalogChecking = "正在检查…";
	public const string ModelCatalogUpdate = "更新到最新";
	public const string ModelCatalogUpdating = "正在更新…";
	public const string ModelCatalogRestore = "还原内置版";
	public const string ModelCatalogRestorePrevious = "恢复上一个覆盖版";
	public const string ModelCatalogOpen = "打开文件";
	public const string ModelCatalogOffline = "离线模式，已跳过网络操作。";
	public const string ModelCatalogFailNetwork = "拉取目录失败，请检查网络或代理后重试。";
	public const string ModelCatalogFailValidation = "下载内容没有通过校验，已保留当前目录。";
	public const string ModelCatalogFailWrite = "目录写入失败，请检查 agent 目录权限与磁盘空间。";
	public const string ModelCatalogFailNoBackup = "没有可恢复的上一个覆盖版。";
	public const string ModelCatalogFailBackup = "上一个覆盖版已损坏，无法恢复；当前目录未改动。";
	public const string ModelCatalogFailUnexpected = "模型目录操作失败。";
	public const string ModelCatalogBannerUpdated = "模型目录已更新。";
	public const string ModelCatalogBannerRestored = "已还原为内置目录。";
	public const string ModelCatalogBannerRestoredPrevious = "已恢复上一个覆盖版。";

	public static string ModelCatalogInfo(int providers, int entries) =>
		$"{providers} 个供应商 · {entries} 条模型";

	public static string ModelCatalogOverlayInfo(string? version, DateTimeOffset? fetchedAt, int providers, int entries)
	{
		var stamp = fetchedAt is { } at
			? at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · "
			: string.Empty;
		var tag = string.IsNullOrWhiteSpace(version) ? string.Empty : version + " · ";
		return $"{tag}{stamp}{ModelCatalogInfo(providers, entries)}";
	}

	public static string ModelCatalogCheckAvailable(string remote, string local) =>
		$"发现新目录：{remote}。当前 {local}。";

	public static string ModelCatalogCheckLatest(string local) =>
		$"当前已是最新目录：{local}。";

	public static string ModelCatalogUpdated(string? version, int entries) =>
		string.IsNullOrWhiteSpace(version)
			? $"模型目录已更新，共 {entries} 条模型。"
			: $"模型目录已更新到 {version}，共 {entries} 条模型。";
}
