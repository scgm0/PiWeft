namespace PiWeft.CodingAgent.Resources;

/// <summary>资源来源的作用域。</summary>
public enum ResourceScope
{
	/// <summary>工作目录内的项目资源（<c>&lt;cwd&gt;/.pi/…</c> 与 <c>.agents/…</c>）。</summary>
	Project,

	/// <summary>用户级资源（<c>&lt;agentDir&gt;/…</c> 与 <c>~/.agents/…</c>）。</summary>
	User,

	/// <summary>由 pi package 声明的资源。</summary>
	Package,
}

/// <summary>
/// 一个资源的来源：作用域、根目录与优先级。优先级数字越小越优先，
/// 与 pi 的 <c>resourcePrecedenceRank</c> 对齐，使同名冲突时「先发现者获胜」有确定依据。
/// </summary>
/// <param name="Scope">资源作用域。</param>
/// <param name="Root">发现该资源的根目录（用于管理界面展示与定位）。</param>
/// <param name="Rank">优先级，越小越优先。</param>
/// <param name="Package">包资源的包名；非包资源为 <c>null</c>。</param>
public readonly record struct ResourceOrigin(
	ResourceScope Scope,
	string Root,
	int Rank,
	string? Package = null)
{
	/// <summary>项目自动发现。</summary>
	public const int ProjectRank = 1;

	/// <summary>用户自动发现。</summary>
	public const int UserRank = 3;

	/// <summary>包资源。</summary>
	public const int PackageRank = 4;

	public static readonly ResourceOrigin ProjectAuto = new(ResourceScope.Project, string.Empty, ProjectRank);

	public static readonly ResourceOrigin UserAuto = new(ResourceScope.User, string.Empty, UserRank);

	public ResourceOrigin WithRoot(string root) => this with { Root = root };

	/// <summary>管理界面使用的中文来源标签。</summary>
	public string Display => Package is { Length: > 0 } package
		? $"包 {package}"
		: Scope switch
		{
			ResourceScope.Project => "项目",
			ResourceScope.Package => "包",
			_ => "用户",
		};
}
