namespace PiWeft.Ai.Auth;

/// 进程内 auth.json 写入门闩。OAuthStore / AuthStore / CodexAuth 各自做「读整份 → 改一个 provider → 写整份」，
/// 并发刷新不同 provider 时没有共享锁会互相覆盖（后写者把前者的新令牌丢掉）。
/// 进程外的 pi CLI 仍按它自己的锁写入，这里只保证本进程内的读改写不丢更新。
internal static class AuthFileGate
{
	public static readonly Lock Gate = new();
}
