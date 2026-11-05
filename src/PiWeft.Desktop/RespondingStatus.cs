namespace PiWeft.Desktop;

/// 会话进行态的种类（与 PiDeck 的 <c>respondingKind</c> 同语义）。
/// 判定顺序见 <see cref="RespondingStatusPolicy.KindOf"/>。
internal enum RespondingKind
{
	Starting,

	Waiting,

	Thinking,

	Responding,

	Executing,

	Compacting,

	Retrying,
}

/// 判定一帧进行态需要的全部事实。都是调用方已经知道的量：这里不做 IO、不读会话，
/// 因此状态条的每一句都能在没有窗口的单测里逐帧断言。
/// <param name="ElapsedMs">本回合开始到现在过了多久（毫秒）；呼吸点、短语轮播与「本轮已 N 秒」共用这一个时钟。</param>
/// <param name="PendingRowVisible">
/// 时间线尾部的等待占位行是否正在显示。它自己报「本轮已 N 秒」，状态条就不再重复同一个数字
/// （与「一轮里只有一个实时耗时」的既有约定一致）。
/// </param>
internal readonly record struct RespondingInput(
	double ElapsedMs,
	bool TurnHasOutput,
	bool PendingRowVisible,
	bool LiveBody,
	bool LiveThinking,
	bool LiveTool,
	bool Compacting,
	bool Retrying,
	int RetryAttempt,
	int RetryMaxAttempts,
	int RetrySecondsLeft);

/// 状态条一帧要显示的内容。<param name="ElapsedMs">本回合已用时长，呼吸点与短语轮播都按它取帧。</param>
internal readonly record struct RespondingStatus(
	RespondingKind Kind,
	string Icon,
	string Role,
	string Phrase,
	string Suffix,
	double ElapsedMs)
{
	public string Pulse => RespondingStatusPolicy.PulseAt(ElapsedMs);

	public string Text => Suffix.Length > 0 ? $"{Phrase} · {Suffix}" : Phrase;
}

/// 状态条的判定与措辞。规则抄 PiDeck：状态必须和「此刻屏幕上在发生什么」对齐，
/// 每一种可观测的阶段给一句确定的话；只有观测不到子阶段的预热期才轮播。
internal static class RespondingStatusPolicy
{
	public const double PulseStepMs = 500;

	public const double PhraseStepMs = 1800;

	public const double ElapsedFromMs = 2000;

	private static readonly string[] PulseFrames = ["·", "··", "···", "··"];

	/// 优先级：退避 → 压缩 → 工具 → 正文 → 思考 → 预热/空窗。
	/// 正文排在思考之前：思考收尾与正文首字可能落在同一批更新里，先判正文可以避免
	/// 状态条从「正在生成回复」退回「正在思考」再跳回来。
	/// 预热与空窗的分别只看一件事——本回合有没有已经出现可见的行。
	public static RespondingKind KindOf(in RespondingInput input)
	{
		if (input.Retrying) return RespondingKind.Retrying;
		if (input.Compacting) return RespondingKind.Compacting;
		if (input.LiveTool) return RespondingKind.Executing;
		if (input.LiveBody) return RespondingKind.Responding;
		if (input.LiveThinking) return RespondingKind.Thinking;
		return input.TurnHasOutput ? RespondingKind.Waiting : RespondingKind.Starting;
	}

	/// 时间线尾部要不要补一行「正在等第一句话」。条件是这一轮还没落下任何可见输出，
	/// 且屏幕上没有任何一段还在流：轮次中间的 steering 消息会让最后一行回到用户行，
	/// 而上一段的工具可能仍在跑——那时候屏幕上的事实是「正在执行命令」，
	/// 占位行既说谎，又与状态条争同一个实时秒数。
	public static bool NeedsPendingRow(in RespondingInput input) =>
		!input.TurnHasOutput && !input.LiveTool && !input.LiveBody && !input.LiveThinking &&
		!input.Retrying && !input.Compacting;

	public static RespondingStatus Compose(in RespondingInput input)
	{
		var kind = KindOf(input);
		var (icon, role, phrase) = kind switch
		{
			RespondingKind.Retrying => ("rotate-ccw", AppStyles.RoleInfo,
				input.RetrySecondsLeft > 0
					? Strings.RetryingIn(input.RetryAttempt, input.RetryMaxAttempts, input.RetrySecondsLeft)
					: Strings.Retrying(input.RetryAttempt, input.RetryMaxAttempts)),
			RespondingKind.Compacting => ("refresh-cw", AppStyles.RoleSubtle, Strings.RespondingCompacting),
			RespondingKind.Executing => ("terminal", AppStyles.RoleInfo, Strings.RespondingExecuting),
			RespondingKind.Responding => ("message-square", AppStyles.RoleInfo, Strings.RespondingGenerating),
			RespondingKind.Thinking => ("sparkles", AppStyles.RoleThinking, Strings.RespondingThinking),
			RespondingKind.Starting => ("loader-circle", AppStyles.RoleInfo, StartingPhraseAt(input.ElapsedMs)),
			_ => ("loader-circle", AppStyles.RoleSubtle, Strings.RespondingWaiting),
		};

		// 时长只在「屏幕上没有东西自己在长」的时候报：推流期肉眼就看得到进展，再读一个数字纯属占位。
		// 退避期也不报：那句「N 秒后重试」已经是更精确的同一个信息。
		// 时间线里已经有一行占位在报同一个数时，这里一律不再重复（一轮只留一个实时耗时）。
		// 判据不看 Kind：占位行与可观测的子阶段（工具执行、压缩）可能同时成立，
		// 只按 Kind 放行就会出现两个各自采样的秒数并排跳（占位行 48 秒 / 状态条 49 秒）。
		var quiet = kind is RespondingKind.Starting or RespondingKind.Waiting
			or RespondingKind.Executing or RespondingKind.Compacting;
		var suffix = quiet && !input.PendingRowVisible && input.ElapsedMs >= ElapsedFromMs
			? Strings.RespondingElapsed(input.ElapsedMs)
			: string.Empty;

		return new RespondingStatus(kind, icon, role, phrase, suffix, input.ElapsedMs);
	}

	public static string StartingPhraseAt(double elapsedMs)
	{
		var phrases = Strings.RespondingStartingPhrases;
		var index = (int)(Math.Max(0, elapsedMs) / PhraseStepMs) % phrases.Count;
		return phrases[index];
	}

	public static string PulseAt(double elapsedMs)
	{
		var index = (int)(Math.Max(0, elapsedMs) / PulseStepMs) % PulseFrames.Length;
		return PulseFrames[index];
	}
}
