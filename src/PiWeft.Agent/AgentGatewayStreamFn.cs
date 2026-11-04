using PiWeft.Ai;
using PiWeft.Ai.Providers;

namespace PiWeft.Agent;

public static class AgentGatewayStreamFn
{
	public static StreamFn For(ModelsGateway gateway)
	{
		ArgumentNullException.ThrowIfNull(gateway);
		return (model, messages, options) => gateway.Stream(model, new Context { Messages = messages }, options);
	}
}
