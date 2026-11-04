namespace PiWeft.Ai.Providers.Anthropic;

internal readonly record struct ThinkingConfig(bool Enabled, int BudgetTokens, bool SendDisabled)
{
	public static ThinkingConfig None => new(false, 0, false);

	public static ThinkingConfig Disabled => new(false, 0, true);

	public static ThinkingConfig On(int budgetTokens) => new(true, budgetTokens, false);
}
