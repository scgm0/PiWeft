namespace PiWeft.Desktop;

/// <summary>
/// 会话进行态的种类（与 PiDeck 的 <c>respondingKind</c> 同语义）。
/// 判定顺序见 <see cref="RespondingStatusPolicy.KindOf"/>。
/// </summary>
internal enum RespondingKind
{
	/// <summary>已发出、本回合还没有任何可见输出：装配环境 + 请求在途 + 等首 token。</summary>
	Starting,

	/// <summary>回合内的空窗：上一件事已经收口（工具跑完、中间回复结束），下一段还没开始推流。</summary>
	Waiting,

	/// <summary>思考 delta 正在推流。</summary>
	Thinking,

	/// <summary>正文 delta 正在推流。</summary>
	Responding,

	/// <summary>工具有活动行在跑。</summary>
	Executing,

	/// <summary>上下文压缩中。</summary>
	Compacting,

	/// <summary>自动重试退避中：这段时间没有任何消息事件，最容易看起来像已经失败。</summary>
	Retrying,
}

/// <summary>
/// 判定一帧进行态需要的全部事实。都是调用方已经知道的量：这里不做 IO、不读会话，
/// 因此状态条的每一句都能在没有窗口的单测里逐帧断言。
/// </summary>
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

/// <summary>
/// 状态条一帧要显示的内容。<param name="ElapsedMs">本回合已用时长，呼吸点与短语轮播都按它取帧。</param>
/// </summary>
internal readonly record struct RespondingStatus(
	RespondingKind Kind,
	string Icon,
	string Role,
	string Phrase,
	string Suffix,
	double ElapsedMs)
{
	/// <summary>呼吸点：静默期唯一的「还在动」的证据。</summary>
	public string Pulse => RespondingStatusPolicy.PulseAt(ElapsedMs);

	/// <summary>整行文字：短语 + 可选的事实后缀。</summary>
	public string Text => Suffix.Length > 0 ? $"{Phrase} · {Suffix}" : Phrase;
}

/// <summary>
/// 状态条的判定与措辞。规则抄 PiDeck：状态必须和「此刻屏幕上在发生什么」对齐，
/// 每一种可观测的阶段给一句确定的话；只有观测不到子阶段的预热期才轮播。
/// </summary>
internal static class RespondingStatusPolicy
{
	/// <summary>呼吸点步进：500ms 一跳，快过读秒、又不至于变成闪烁。</summary>
	public const double PulseStepMs = 500;

	/// <summary>预热短语轮播步进：比呼吸点慢一个量级，两处文字同时跳会很吵。</summary>
	public const double PhraseStepMs = 1800;

	/// <summary>静默多久才开始报「本轮已 N 秒」：刚开始的一两秒报数是噪音。</summary>
	public const double ElapsedFromMs = 2000;

	private static readonly string[] PulseFrames = ["·", "··", "···", "··"];

	/// <summary>
	/// 优先级：退避 → 压缩 → 工具 → 正文 → 思考 → 预热/空窗。
	/// 正文排在思考之前：思考收尾与正文首字可能落在同一批更新里，先判正文可以避免
	/// 状态条从「正在生成回复」退回「正在思考」再跳回来。
	/// 预热与空窗的分别只看一件事——本回合有没有已经出现可见的行。
	/// </summary>
	public static RespondingKind KindOf(in RespondingInput input)
	{
		if (input.Retrying) return RespondingKind.Retrying;
		if (input.Compacting) return RespondingKind.Compacting;
		if (input.LiveTool) return RespondingKind.Executing;
		if (input.LiveBody) return RespondingKind.Responding;
		if (input.LiveThinking) return RespondingKind.Thinking;
		return input.TurnHasOutput ? RespondingKind.Waiting : RespondingKind.Starting;
	}

	/// <summary>这一帧的状态条内容。</summary>
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
		// 预热期若时间线里已经有一行占位在报同一个数，这里就不再重复（一轮只留一个实时耗时）。
		var quiet = kind is RespondingKind.Starting or RespondingKind.Waiting
			or RespondingKind.Executing or RespondingKind.Compacting;
		var duplicate = kind == RespondingKind.Starting && input.PendingRowVisible;
		var suffix = quiet && !duplicate && input.ElapsedMs >= ElapsedFromMs
			? Strings.RespondingElapsed(input.ElapsedMs)
			: string.Empty;

		return new RespondingStatus(kind, icon, role, phrase, suffix, input.ElapsedMs);
	}

	/// <summary>预热阶段的第几句短语：按本回合已用时间取，回合从零开始就从头轮起。</summary>
	public static string StartingPhraseAt(double elapsedMs)
	{
		var phrases = Strings.RespondingStartingPhrases;
		var index = (int)(Math.Max(0, elapsedMs) / PhraseStepMs) % phrases.Count;
		return phrases[index];
	}

	/// <summary>呼吸点的一帧（涨到三点再收回去，比循环「···→空」更像在喘气）。</summary>
	public static string PulseAt(double elapsedMs)
	{
		var index = (int)(Math.Max(0, elapsedMs) / PulseStepMs) % PulseFrames.Length;
		return PulseFrames[index];
	}
}
