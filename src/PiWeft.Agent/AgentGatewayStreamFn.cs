using PiWeft.Ai;
using PiWeft.Ai.Providers;

namespace PiWeft.Agent;

/// <summary>把 <see cref="ModelsGateway"/> 适配成代理使用的 <see cref="StreamFn"/>。</summary>
public static class AgentGatewayStreamFn
{
	public static StreamFn For(ModelsGateway gateway)
	{
		ArgumentNullException.ThrowIfNull(gateway);
		return (model, messages, options) => gateway.Stream(model, new Context { Messages = messages }, options);
	}
}
