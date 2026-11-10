namespace PiWeft.Ai.Providers.Anthropic;

internal enum ThinkingMode
{
	None,
	Disabled,
	Budget,
	Adaptive,
}

/// Claude 的思考请求形态：旧模型用预算，Opus 5.5 / Fable 这类模型只接受 adaptive effort。
internal readonly record struct ThinkingConfig(ThinkingMode Mode, int BudgetTokens = 0, string? Effort = null)
{
	public static ThinkingConfig None => new(ThinkingMode.None);

	public static ThinkingConfig Disabled => new(ThinkingMode.Disabled);

	public static ThinkingConfig On(int budgetTokens) => new(ThinkingMode.Budget, budgetTokens);

	public static ThinkingConfig Adaptive(string effort) => new(ThinkingMode.Adaptive, Effort: effort);

	public bool Enabled => Mode is ThinkingMode.Budget or ThinkingMode.Adaptive;

	public bool IsAdaptive => Mode == ThinkingMode.Adaptive;

	public bool SendDisabled => Mode == ThinkingMode.Disabled;
}
