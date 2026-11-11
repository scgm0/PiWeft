namespace PiWeft.Desktop;

/// 一个随桌面分发的内置扩展：PiDeck 的 <c>pi-deck-*.ts</c> 由原生 C# 接管，没有对应文件，
/// 只在设置页展示。只列已移植的项：列出来的都真的在生效。
internal sealed record DesktopExtension(string Name, string Description);

/// 桌面内置扩展清单，顺序与 PiDeck 的 <c>BUILT_IN_EXTENSIONS</c> 一致。
internal static class DesktopExtensions
{
	public static IReadOnlyList<DesktopExtension> All { get; } =
	[
		new("pi-deck-request-size-recovery", Strings.DesktopExtensionRequestSizeRecovery),
		new("pi-deck-ask-question", Strings.DesktopExtensionAskQuestion),
		new("pi-deck-goal-mode", Strings.DesktopExtensionGoalMode),
		new("pi-deck-linked-sessions", Strings.DesktopExtensionLinkedSessions),
		new("pi-deck-nul-redirect-fix", Strings.DesktopExtensionNulRedirectFix),
		new("pi-deck-plan-mode", Strings.DesktopExtensionPlanMode),
		new("pi-deck-retry-no-body", Strings.DesktopExtensionRetryNoBody),
		new("pi-deck-security-gate", Strings.DesktopExtensionSecurityGate),
		new("pi-deck-todo", Strings.DesktopExtensionTodo),
		new("pi-deck-trash-guard", Strings.DesktopExtensionTrashGuard),
	];
}
