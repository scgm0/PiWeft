namespace PiWeft.CodingAgent.Security;

/// <summary>路径边界策略，与 PiDeck 同名同义。</summary>
public enum ToolPathPolicy
{
	/// <summary>不限制目录（仍然受 <c>denyDirs</c> 与敏感文件规则约束）。</summary>
	Unrestricted,

	/// <summary>只允许工作目录内。</summary>
	Workspace,

	/// <summary>允许工作目录加上 <see cref="ToolSecurity.CustomAllowDirs"/>。</summary>
	Custom,
}
