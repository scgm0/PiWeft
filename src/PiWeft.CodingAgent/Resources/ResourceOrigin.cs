namespace PiWeft.CodingAgent.Resources;

public enum ResourceScope
{
	Project,

	User,

	Package,
}

/// 一个资源的来源：作用域、根目录与优先级。优先级数字越小越优先，
/// 与 pi 的 <c>resourcePrecedenceRank</c> 对齐，使同名冲突时「先发现者获胜」有确定依据。
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
	public const int ProjectRank = 1;

	public const int UserRank = 3;

	public const int PackageRank = 4;

	public static readonly ResourceOrigin ProjectAuto = new(ResourceScope.Project, string.Empty, ProjectRank);

	public static readonly ResourceOrigin UserAuto = new(ResourceScope.User, string.Empty, UserRank);

	public ResourceOrigin WithRoot(string root) => this with { Root = root };

	public string Display => Package is { Length: > 0 } package
		? $"包 {package}"
		: Scope switch
		{
			ResourceScope.Project => "项目",
			ResourceScope.Package => "包",
			_ => "用户",
		};
}
