namespace PiWeft.Ai.Providers.Anthropic;

/// <summary>一次 Anthropic 请求解析后的思考配置。</summary>
internal readonly record struct ThinkingConfig(bool Enabled, int BudgetTokens, bool SendDisabled)
{
	/// <summary>模型不具备推理能力，因此不发送 <c>thinking</c> 字段。</summary>
	public static ThinkingConfig None => new(false, 0, false);

	/// <summary>发送 <c>{"type":"disabled"}</c>，让模型默认不思考。</summary>
	public static ThinkingConfig Disabled => new(false, 0, true);

	/// <summary>发送 <c>{"type":"enabled","budget_tokens":N}</c>。</summary>
	public static ThinkingConfig On(int budgetTokens) => new(true, budgetTokens, false);
}
