namespace PiWeft.Desktop;

/// 窗口级会话活动：哪个会话正在跑、最后一次改过多少文件。
/// 侧栏父行的「运行中 / 3 文件」用它；子代理的改动在 <see cref="SubagentStatusIndex"/> 里单独算。
internal sealed class SessionActivityIndex
{
	internal sealed record Entry(bool Streaming, IReadOnlyList<string> Files)
	{
		public int ChangedFiles => Files.Count;
	}

	private readonly Dictionary<string, Entry> _bySession = new(StringComparer.Ordinal);

	/// 更新一个会话的活动；只有显示相关字段变化才返回 true。
	internal bool Update(string sessionId, bool streaming, IReadOnlyList<ChatFileChange> changedFiles)
	{
		if (sessionId.Length == 0)
		{
			return false;
		}

		var files = new string[changedFiles.Count];
		for (var index = 0; index < changedFiles.Count; index++)
		{
			files[index] = changedFiles[index].Path;
		}

		if (_bySession.TryGetValue(sessionId, out var previous) &&
			previous.Streaming == streaming &&
			previous.Files.SequenceEqual(files, StringComparer.Ordinal))
		{
			return false;
		}

		_bySession[sessionId] = new Entry(streaming, files);
		return true;
	}

	internal bool Remove(string sessionId) => sessionId.Length > 0 && _bySession.Remove(sessionId);

	internal Entry? Get(string sessionId) => _bySession.GetValueOrDefault(sessionId);
}
