using PiWeft.Agent;

namespace PiWeft.CodingAgent.Extensions;

/// <summary>扩展注册的一个斜杠命令。</summary>
public sealed record ExtensionCommand(
	string Name,
	string Description,
	/// <summary>命令处理函数的引擎内引用；由宿主在扩展线程上调用。</summary>
	Action<string> Invoke);

/// <summary>
/// 一个已加载扩展注册出来的东西。宿主读它来拼工具集、命令表与钩子，
/// 单独抽出来是因为「注册表」要在扩展线程之外被读（代理构建工具声明时）。
/// </summary>
internal sealed class ExtensionRegistration
{
	/// <summary>可支持的事件名；其余事件在注册时就报告，而不是收下后永远不触发。</summary>
	public static readonly IReadOnlyCollection<string> SupportedEvents =
	[
		"tool_call",
		"tool_result",
		"before_agent_start",
		"agent_end",
	];

	private readonly List<string> _listeners = [];
	private readonly List<string> _unsupported = [];
	private readonly List<AgentTool> _tools = [];
	private readonly List<ExtensionCommand> _commands = [];
	private readonly List<ExtensionFlag> _flags = [];

	public IReadOnlyList<AgentTool> Tools => _tools;

	public IReadOnlyList<ExtensionCommand> Commands => _commands;

	/// <summary>该扩展注册的 CLI flag。加载失败的扩展不参与 flag 解析。</summary>
	public IReadOnlyList<ExtensionFlag> Flags => _flags;

	public IReadOnlyList<string> UnsupportedEvents => _unsupported;

	public void AddTool(AgentTool tool) => _tools.Add(tool);

	public void AddCommand(ExtensionCommand command) => _commands.Add(command);

	public void AddFlag(ExtensionFlag flag) => _flags.Add(flag);

	/// <summary>记下一个监听者；事件名不受支持时返回 false（调用方负责报告诊断）。</summary>
	public bool NoteListener(string @event)
	{
		var supported = SupportedEvents.Contains(@event, StringComparer.Ordinal);
		if (!supported)
		{
			// 记下来供界面汇总：悄悄收下却永不触发，比注册时就报错难查。
			_unsupported.Add(@event);
			return false;
		}

		if (!_listeners.Contains(@event, StringComparer.Ordinal))
		{
			_listeners.Add(@event);
		}

		return true;
	}

	/// <summary>事件名是否被宿主支持。</summary>
	public bool Supported(string @event) => SupportedEvents.Contains(@event, StringComparer.Ordinal);

	/// <summary>
	/// 有没有人监听这个事件。实际派发由 <see cref="ExtensionHost"/> 走 JS 侧注册表完成，
	/// 这里只回答「要不要为它构造 payload」。
	/// </summary>
	public bool HasHandlers(string @event) => _listeners.Contains(@event, StringComparer.Ordinal);
}
