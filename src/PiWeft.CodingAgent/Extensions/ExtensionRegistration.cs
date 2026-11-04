using PiWeft.Agent;

namespace PiWeft.CodingAgent.Extensions;

public sealed record ExtensionCommand(
	string Name,
	string Description,
	Action<string> Invoke);

/// 一个已加载扩展注册出来的东西。宿主读它来拼工具集、命令表与钩子，
/// 单独抽出来是因为「注册表」要在扩展线程之外被读（代理构建工具声明时）。
internal sealed class ExtensionRegistration
{
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

	public IReadOnlyList<ExtensionFlag> Flags => _flags;

	public IReadOnlyList<string> UnsupportedEvents => _unsupported;

	public void AddTool(AgentTool tool) => _tools.Add(tool);

	public void AddCommand(ExtensionCommand command) => _commands.Add(command);

	public void AddFlag(ExtensionFlag flag) => _flags.Add(flag);

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

	public bool Supported(string @event) => SupportedEvents.Contains(@event, StringComparer.Ordinal);

	/// 有没有人监听这个事件。实际派发由 <see cref="ExtensionHost"/> 走 JS 侧注册表完成，
	/// 这里只回答「要不要为它构造 payload」。
	public bool HasHandlers(string @event) => _listeners.Contains(@event, StringComparer.Ordinal);
}
