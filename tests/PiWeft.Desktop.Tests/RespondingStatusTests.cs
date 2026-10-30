using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 会话进行态的判定与措辞。规矩是从 PiDeck 抄来的：状态条只能说后台此刻确定的事实，
/// 只有观测不到子阶段的「等首 token」允许多句轮播；而这一段恰恰是首轮最久的空白，
/// 因此它必须永远有一句话可说。
/// </summary>
public sealed class RespondingStatusTests : IDisposable
{
	private readonly Func<bool> _originalEnabled = RespondingTips.EnabledProvider;
	private readonly Func<ShortcutMap> _originalBindings = RespondingTips.BindingsProvider;

	public void Dispose()
	{
		RespondingTips.EnabledProvider = _originalEnabled;
		RespondingTips.BindingsProvider = _originalBindings;
	}

	private static RespondingInput Input(
		double elapsed = 0,
		bool output = false,
		bool body = false,
		bool thinking = false,
		bool tool = false,
		bool compacting = false,
		bool retrying = false,
		int attempt = 0,
		int max = 0,
		int seconds = 0,
		bool pendingRow = false) =>
		new(elapsed, output, pendingRow, body, thinking, tool, compacting, retrying, attempt, max, seconds);

	[Fact]
	public void BackoffBeatsEverythingElseBecauseThatSilenceLooksLikeFailure() =>
		Assert.Equal(RespondingKind.Retrying, RespondingStatusPolicy.KindOf(
			Input(retrying: true, attempt: 2, max: 3, seconds: 7, body: true, compacting: true)));

	[Fact]
	public void CompactionBeatsToolAndStreamBecauseItIsTheOuterRuntimeStep() =>
		Assert.Equal(RespondingKind.Compacting, RespondingStatusPolicy.KindOf(Input(compacting: true, tool: true)));

	[Fact]
	public void BodyWinsOverThinkingSoTheBarNeverWalksBackwards() =>
		Assert.Equal(RespondingKind.Responding, RespondingStatusPolicy.KindOf(Input(body: true, thinking: true)));

	[Fact]
	public void ToolExecutionOutranksTheDeltasOfTheSameBatch() =>
		Assert.Equal(RespondingKind.Executing, RespondingStatusPolicy.KindOf(Input(tool: true, body: true)));

	[Fact]
	public void NothingOnScreenYetIsWarmingUpNotIdle()
	{
		// 首轮发出后到首 token 之前：屏幕上只有用户自己那条，这段必须判成预热而不是「没在跑」。
		Assert.Equal(RespondingKind.Starting, RespondingStatusPolicy.KindOf(Input(output: false)));
		Assert.Equal(RespondingKind.Starting, RespondingStatusPolicy.KindOf(Input(elapsed: 40_000)));
	}

	[Fact]
	public void SilenceAfterSomeOutputIsAnInterTurnGap() =>
		Assert.Equal(RespondingKind.Waiting, RespondingStatusPolicy.KindOf(Input(output: true)));

	[Fact]
	public void WarmingUpRotatesThroughRealStepsAndWrapsAround()
	{
		var phrases = Strings.RespondingStartingPhrases;
		Assert.Equal(phrases[0], RespondingStatusPolicy.Compose(Input()).Phrase);
		Assert.Equal(phrases[1], RespondingStatusPolicy.Compose(
			Input(elapsed: RespondingStatusPolicy.PhraseStepMs)).Phrase);
		Assert.Equal(phrases[2], RespondingStatusPolicy.Compose(
			Input(elapsed: RespondingStatusPolicy.PhraseStepMs * 2)).Phrase);
		Assert.Equal(phrases[0], RespondingStatusPolicy.Compose(
			Input(elapsed: RespondingStatusPolicy.PhraseStepMs * phrases.Count)).Phrase);
	}

	[Fact]
	public void ObservableStatesNeverRotateBecauseGuessingOutLoudIsWorseThanOneTrueSentence()
	{
		Assert.Equal(Strings.RespondingWaiting, RespondingStatusPolicy.Compose(Input(output: true, elapsed: 9_000)).Phrase);
		Assert.Equal(Strings.RespondingWaiting, RespondingStatusPolicy.Compose(Input(output: true, elapsed: 18_000)).Phrase);
		Assert.Equal(Strings.RespondingThinking, RespondingStatusPolicy.Compose(Input(thinking: true, elapsed: 9_000)).Phrase);
	}

	[Fact]
	public void ElapsedAppearsOnlyOnceTheWaitLastsAndOnlyWhileNothingIsGrowing()
	{
		Assert.Equal(string.Empty, RespondingStatusPolicy.Compose(Input(elapsed: 1_200)).Suffix);
		Assert.Contains("2 秒", RespondingStatusPolicy.Compose(Input(elapsed: 2_000)).Suffix, StringComparison.Ordinal);
		Assert.Contains("1 分 05 秒", RespondingStatusPolicy.Compose(Input(elapsed: 65_000)).Suffix, StringComparison.Ordinal);

		// 推流期眼睛在正上文中；退避期已经有更精确的倒计时。两处都不再读秒。
		Assert.Equal(string.Empty, RespondingStatusPolicy.Compose(Input(body: true, elapsed: 30_000)).Suffix);
		Assert.Equal(string.Empty, RespondingStatusPolicy.Compose(
			Input(retrying: true, attempt: 1, max: 3, seconds: 5, elapsed: 30_000)).Suffix);
	}

	[Fact]
	public void TheSameElapsedSecondIsNotPrintedTwiceWhenThePlaceholderRowAlreadyShowsIt()
	{
		// 预热期：时间线尾部那行占位在报秒，状态条就让位，同一屏只有一个实时耗时。
		Assert.Equal(string.Empty, RespondingStatusPolicy.Compose(
			Input(elapsed: 12_000, pendingRow: true)).Suffix);
		// 用户消息还没落到投影里（例如发送前的检查点快照）时没有占位行，状态条自己报。
		Assert.Contains("12 秒", RespondingStatusPolicy.Compose(
			Input(elapsed: 12_000, pendingRow: false)).Suffix, StringComparison.Ordinal);
	}

	[Fact]
	public void PlaceholderRowNeverSuppressesTheElapsedOfALaterQuietPhase()
	{
		// 一旦有了输出，占位行就该收起来；此时「正在处理 · 本轮已 N 秒」仍然照报。
		var status = RespondingStatusPolicy.Compose(Input(output: true, elapsed: 30_000, pendingRow: false));
		Assert.Equal(RespondingKind.Waiting, status.Kind);
		Assert.Contains("30 秒", status.Suffix, StringComparison.Ordinal);
	}

	[Fact]
	public void WaitingLineSaysTheStateAndTheWaitInOneBreath()
	{
		var status = RespondingStatusPolicy.Compose(Input(output: true, elapsed: 12_000));
		Assert.Equal($"{Strings.RespondingWaiting} · 本轮已 12 秒", status.Text);
	}

	[Fact]
	public void BackoffKeepsItsAttemptNumberAndCountdown()
	{
		var status = RespondingStatusPolicy.Compose(Input(retrying: true, attempt: 2, max: 3, seconds: 4));
		Assert.Contains("正在自动重试 2/3", status.Phrase, StringComparison.Ordinal);
		Assert.Contains("4 秒后重试", status.Phrase, StringComparison.Ordinal);
	}

	[Fact]
	public void PulseBreathesUpDownUpDownInsteadOfLoopingToEmpty()
	{
		Assert.Equal("·", RespondingStatusPolicy.PulseAt(0));
		Assert.Equal("··", RespondingStatusPolicy.PulseAt(RespondingStatusPolicy.PulseStepMs));
		Assert.Equal("···", RespondingStatusPolicy.PulseAt(RespondingStatusPolicy.PulseStepMs * 2));
		Assert.Equal("··", RespondingStatusPolicy.PulseAt(RespondingStatusPolicy.PulseStepMs * 3));
		Assert.Equal("·", RespondingStatusPolicy.PulseAt(RespondingStatusPolicy.PulseStepMs * 4));
	}

	[Fact]
	public void StatusCarriesItsOwnIconAndSemanticRole()
	{
		Assert.Equal("sparkles", RespondingStatusPolicy.Compose(Input(thinking: true)).Icon);
		Assert.Equal(AppStyles.RoleThinking, RespondingStatusPolicy.Compose(Input(thinking: true)).Role);
		Assert.Equal("loader-circle", RespondingStatusPolicy.Compose(Input()).Icon);
	}

	[Fact]
	public void TipsAreShownByDefaultAndRotateSlowerThanTheStatusPhrase()
	{
		RespondingTips.EnabledProvider = static () => true;
		RespondingTips.Invalidate();
		Assert.NotNull(RespondingTips.Current(0));
		Assert.True(RespondingTips.TipStepMs > RespondingStatusPolicy.PhraseStepMs);
	}

	[Fact]
	public void TurningTheSettingOffEmptiesThatSegment()
	{
		RespondingTips.EnabledProvider = static () => false;
		RespondingTips.Invalidate();
		Assert.Null(RespondingTips.Current(0));
	}

	[Fact]
	public void NoTipLeaksAShortcutPlaceholderOrACustomKeyThatWasNeverBound()
	{
		RespondingTips.EnabledProvider = static () => true;
		RespondingTips.Invalidate();
		for (var index = 0; RespondingTips.At(index) is { } tip; index++)
		{
			Assert.DoesNotContain(Strings.RespondingTipShortcutToken, tip, StringComparison.Ordinal);
			Assert.DoesNotContain(Strings.ShortcutUnbound, tip, StringComparison.Ordinal);
			Assert.NotEmpty(tip);
		}
	}

	[Fact]
	public void EveryTurnStartsWithADifferentTipSoShortWaitsDoNotAlwaysShowTheSameOne()
	{
		RespondingTips.EnabledProvider = static () => true;
		RespondingTips.Invalidate();

		// 回合起点当相位：从不同起点出发的回合，第一句提示不应该总能碰上同一条。
		var firsts = new HashSet<string>(StringComparer.Ordinal);
		for (long seed = 0; seed < 12; seed++)
		{
			if (RespondingTips.Current(0, seed) is { Length: > 0 } tip) firsts.Add(tip);
		}

		Assert.True(firsts.Count > 1, "提示的第一句不应固定在同一条（短等待只看得到第一条）");
	}

	[Fact]
	public void TipKeysFollowTheEffectiveBindingNotTheDefaultOne()
	{
		var map = new ShortcutMap();
		Assert.True(map.Rebind(ShortcutActions.Palette, "Ctrl+Shift+P"));
		RespondingTips.EnabledProvider = static () => true;
		RespondingTips.BindingsProvider = () => map;
		RespondingTips.Invalidate();

		Assert.StartsWith("Ctrl+Shift+P", RespondingTips.At(0), StringComparison.Ordinal);
	}
}
