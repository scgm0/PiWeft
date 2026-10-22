using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>命令 Dock 的最小 VT 屏幕：文本、光标、颜色与滚动。</summary>
public sealed class TerminalScreenTests
{
	private static TerminalScreen NewScreen(int columns = 20, int rows = 5) => new(columns, rows);

	[Fact]
	public void PrintsTextAndWrapsAtTheRightEdge()
	{
		var screen = NewScreen(columns: 5, rows: 3);

		screen.Feed("abcdef".AsSpan());

		Assert.Equal("abcde\nf", screen.GetText());
		Assert.Equal(1, screen.CursorRow);
		Assert.Equal(1, screen.CursorColumn);
	}

	[Fact]
	public void CarriageReturnOverwritesTheLine()
	{
		var screen = NewScreen();

		screen.Feed("abc\rXY".AsSpan());

		Assert.Equal("XYc", screen.GetText());
	}

	[Fact]
	public void BackspaceMovesTheCursor()
	{
		var screen = NewScreen();

		screen.Feed("abc\b\bZ".AsSpan());

		Assert.Equal("aZc", screen.GetText());
	}

	[Fact]
	public void CursorPositioningAndEraseLineFollowTheSequences()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("one\r\ntwo\r\nthree".AsSpan());
		screen.Feed("\u001b[2;1H\u001b[K".AsSpan());

		Assert.Equal("one\n\nthree", screen.GetText());
	}

	[Fact]
	public void ClearScreenResetsEverything()
	{
		var screen = NewScreen();

		screen.Feed("hello\r\nworld".AsSpan());
		screen.Feed("\u001b[2J\u001b[H".AsSpan());

		Assert.Equal(string.Empty, screen.GetText());
		Assert.Equal(0, screen.CursorRow);
	}

	[Fact]
	public void ScrollingKeepsTheLatestRows()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("1\r\n2\r\n3\r\n4".AsSpan());

		Assert.Equal("2\n3\n4", screen.GetText());
	}

	[Fact]
	public void SgrColorsAreKeptPerRun()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[31mred\u001b[0m plain".AsSpan());

		var runs = screen.GetRuns(0, []);
		Assert.Equal(2, runs.Count);
		Assert.Equal("red", runs[0].Text);
		Assert.Equal(TerminalColorKind.Indexed, runs[0].Foreground.Kind);
		Assert.Equal(1, (int)runs[0].Foreground.Index);
		Assert.Equal(" plain", runs[1].Text);
		Assert.Equal(TerminalColorKind.Default, runs[1].Foreground.Kind);
	}

	[Fact]
	public void ExtendedColorsAreParsed()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[38;5;196mX\u001b[48;2;1;2;3mY".AsSpan());

		var runs = screen.GetRuns(0, []);
		Assert.Equal(2, runs.Count);
		Assert.Equal(TerminalColorKind.Indexed, runs[0].Foreground.Kind);
		Assert.Equal(196, (int)runs[0].Foreground.Index);
		Assert.Equal(TerminalColorKind.Rgb, runs[1].Background.Kind);
		Assert.Equal(0x010203u, runs[1].Background.Rgb);
	}

	[Fact]
	public void WideCharactersOccupyTwoCells()
	{
		var screen = NewScreen(columns: 6, rows: 2);

		screen.Feed("中文".AsSpan());

		Assert.Equal("中文", screen.GetText());
		Assert.Equal(4, screen.CursorColumn);
		Assert.Single(screen.GetRuns(0, []));
	}

	[Fact]
	public void OscTitlesAreIgnored()
	{
		var screen = NewScreen();

		screen.Feed("\u001b]0;window title\u0007ok".AsSpan());

		Assert.Equal("ok", screen.GetText());
	}

	[Fact]
	public void ResizeKeepsTheVisibleTail()
	{
		var screen = NewScreen(columns: 10, rows: 4);

		screen.Feed("a\r\nb\r\nc\r\nd".AsSpan());
		screen.Resize(10, 2);

		Assert.Equal("c\nd", screen.GetText());
	}

	[Fact]
	public void ScrollbackKeepsHistoryAndTheViewportCanMove()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6".AsSpan());

		Assert.Equal(3, screen.ScrollbackLines);
		Assert.Equal("4\n5\n6", screen.GetText());
		Assert.Equal(0, screen.ViewOffset);

		Assert.True(screen.ScrollView(2));
		Assert.Equal(2, screen.ViewOffset);
		Assert.Equal("2\n3\n4", screen.GetText());

		screen.ScrollToBottom();
		Assert.Equal("4\n5\n6", screen.GetText());
	}

	[Fact]
	public void ScrollRegionLimitsScrollingToItsRows()
	{
		var screen = NewScreen(columns: 10, rows: 5);

		screen.Feed("a\r\nb\r\nc\r\nd\r\ne".AsSpan());
		screen.Feed("\u001b[2;4r\u001b[4;1H\nZ".AsSpan());

		// 区域是第 2–4 行：a 与 e 不动，b 被挤出区域。
		Assert.Equal("a\nc\nd\nZ\ne", screen.GetText());
	}

	[Fact]
	public void SpecialGraphicsCharsetMapsLineDrawingCharacters()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		screen.Feed("\u001b(0qx\u001b(Bq".AsSpan());
		Assert.Equal("─│q", screen.GetText());

		screen.Feed("\r\n\u001b)0\u000eq\u000fq".AsSpan());
		Assert.EndsWith("─q", screen.GetText(), StringComparison.Ordinal);
	}

	[Fact]
	public void AlternateScreenRestoresThePrimaryBuffer()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("main".AsSpan());
		screen.Feed("\u001b[?1049h".AsSpan());
		Assert.True(screen.IsAlternateScreen);
		Assert.Equal(string.Empty, screen.GetText());

		screen.Feed("alt".AsSpan());
		screen.Feed("\u001b[?1049l".AsSpan());

		Assert.False(screen.IsAlternateScreen);
		Assert.Equal("main", screen.GetText());
	}

	[Fact]
	public void MouseModesFollowTheIndependentSwitches()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[?1002h\u001b[?1006h".AsSpan());
		Assert.Equal(TerminalMouseMode.Button, screen.MouseMode);
		Assert.True(screen.SgrMouse);

		screen.Feed("\u001b[?1003h".AsSpan());
		Assert.Equal(TerminalMouseMode.Any, screen.MouseMode);

		screen.Feed("\u001b[?1003l".AsSpan());
		Assert.Equal(TerminalMouseMode.Button, screen.MouseMode);

		screen.Feed("\u001b[?1002l".AsSpan());
		Assert.Equal(TerminalMouseMode.None, screen.MouseMode);
	}

	[Fact]
	public void SelectionExtractsTextAcrossLines()
	{
		var screen = NewScreen(columns: 5, rows: 3);

		screen.Feed("abcdeab".AsSpan());
		screen.BeginSelection(0, 3);
		screen.ExtendSelection(1, 1);

		Assert.Equal("de\nab", screen.GetSelectionText());
		Assert.True(screen.HasSelection);

		screen.ClearSelection();
		Assert.Null(screen.GetSelectionText());
	}

	[Fact]
	public void SelectAllCoversTheWholeBuffer()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("hello\r\nworld".AsSpan());
		screen.SelectAll();

		var text = screen.GetSelectionText() ?? string.Empty;
		Assert.StartsWith("hello\nworld", text, StringComparison.Ordinal);
	}

	[Fact]
	public void SelectionRaisesTheChangeEvent()
	{
		var screen = NewScreen();
		var changes = 0;
		screen.SelectionChanged += () => changes++;

		screen.BeginSelection(0, 0);
		screen.ExtendSelection(0, 2);
		screen.ClearSelection();

		Assert.Equal(3, changes);
	}

	[Fact]
	public void SurrogatePairsAreNeverSplitAcrossRuns()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		screen.Feed("a\U0001F600b".AsSpan());
		Assert.Equal("a\U0001F600b", screen.GetText());

		// 选区停在 emoji 的第二个 surrogate：run 边界不能落在代理对中间。
		screen.BeginSelection(0, 0);
		screen.ExtendSelection(0, 2);

		foreach (var run in screen.GetRuns(0, []))
		{
			Assert.True(IsWellFormed(run.Text), $"run 边界切开了代理对：{run.Text}");
		}
	}

	private static bool IsWellFormed(string text)
	{
		for (var index = 0; index < text.Length; index++)
		{
			if (char.IsHighSurrogate(text[index]))
			{
				if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
				{
					return false;
				}

				index++;
			}
			else if (char.IsLowSurrogate(text[index]))
			{
				return false;
			}
		}

		return true;
	}

	[Fact]
	public void CombiningMarksStayInTheSameCell()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// é = e + 组合重音：一个文本元素占一格，光标只前进一格。
		screen.Feed("e\u0301x".AsSpan());

		var runs = screen.GetRuns(0, []);
		Assert.Equal("e\u0301x", Assert.Single(runs).Text);
		Assert.Equal(2, runs[0].Cells);
		Assert.Equal(2, screen.CursorColumn);
		Assert.Equal("e\u0301x", screen.GetText());
	}

	[Fact]
	public void WideCharactersAdvanceRunsByCellsNotChars()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		screen.Feed("你a".AsSpan());

		var runs = screen.GetRuns(0, []);
		Assert.Equal("你a", runs[0].Text);
		Assert.Equal(0, runs[0].Column);
		Assert.Equal(3, runs[0].Cells);
		Assert.Equal(3, screen.CursorColumn);
	}

	[Fact]
	public void CursorSplitsAtAWholeWideCharacter()
	{
		// "a你b"：单元格 0=a、1–2=你、3=b；光标落在中文右半格（列 2）时归到左半（列 1），整字一起高亮。
		var screen = NewScreen(columns: 10, rows: 2);
		screen.Feed("a你b".AsSpan());

		var runs = screen.GetRuns(0, [], 2);

		Assert.Equal(["a", "你", "b"], runs.Select(run => run.Text));
		Assert.Equal([0, 1, 3], runs.Select(run => run.Column));
		Assert.Equal([1, 2, 1], runs.Select(run => run.Cells));
		Assert.Equal([false, true, false], runs.Select(run => run.Cursor));
	}

	[Fact]
	public void ZwjEmojiSequenceTakesTwoCells()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// 👨‍👩‍👧：5 个码点、8 个 UTF-16 码元，显示上仍是一个两格宽的簇。
		screen.Feed("\U0001F468\u200D\U0001F469\u200D\U0001F467x".AsSpan());

		var runs = screen.GetRuns(0, []);
		Assert.Equal("\U0001F468\u200D\U0001F469\u200D\U0001F467x", runs[0].Text);
		Assert.Equal(3, runs[0].Cells);
		Assert.Equal(3, screen.CursorColumn);
	}

	[Fact]
	public void SkinToneModifierJoinsTheEmojiCell()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		screen.Feed("\U0001F44D\U0001F3FD".AsSpan());

		Assert.Equal(2, screen.CursorColumn);
		Assert.Equal(2, Assert.Single(screen.GetRuns(0, [])).Cells);
	}

	[Fact]
	public void RegionalIndicatorsPairIntoOneFlagCell()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// 🇨🇳🇯🇵：四个区域指示符配成两面旗，占四格而不是八格。
		screen.Feed("\U0001F1E8\U0001F1F3\U0001F1EF\U0001F1F5".AsSpan());

		var run = Assert.Single(screen.GetRuns(0, []));
		Assert.Equal(4, run.Cells);
		Assert.Equal(4, screen.CursorColumn);
	}

	[Fact]
	public void HangulJamoComposesIntoSingleSyllable()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// 初声（宽）+ 中声 + 终声（零宽）= 한，占两格。
		screen.Feed("\u1112\u1161\u11AB".AsSpan());

		var run = Assert.Single(screen.GetRuns(0, []));
		Assert.Equal("\u1112\u1161\u11AB", run.Text);
		Assert.Equal(2, run.Cells);
		Assert.Equal(2, screen.CursorColumn);
	}

	[Fact]
	public void LeadingCombiningMarkIsDroppedInsteadOfTakingACell()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// 行首没有可附着的格子：组合符号不占格，后面的文本也不能被顶偏。
		screen.Feed("\u0301ab".AsSpan());

		Assert.Equal("ab", screen.GetText());
		Assert.Equal(2, screen.CursorColumn);
	}

	[Fact]
	public void ClusterSplitAcrossChunksStillJoins()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		// PTY 读块会把簇劈开：组合符号与代理对都必须跨块续接。
		screen.Feed("e".AsSpan());
		screen.Feed("\u0301".AsSpan());
		screen.Feed("\uD83D".AsSpan());
		screen.Feed("\uDE00".AsSpan());

		Assert.Equal("e\u0301\U0001F600", screen.GetText());
		Assert.Equal(3, screen.CursorColumn);
	}

	[Fact]
	public void ReflowKeepsWideCharacterContinuations()
	{
		var screen = NewScreen(columns: 4, rows: 3);

		screen.Feed("中文字".AsSpan());
		screen.Resize(6, 3);

		// 重排后右半格必须还是占位格：写成空格会让整行多出一个空格。
		Assert.Equal("中文字", screen.GetText());
		Assert.Equal(6, Assert.Single(screen.GetRuns(0, [])).Cells);
	}

	[Fact]
	public void FeedBumpsTheVersionOncePerChunk()
	{
		var screen = NewScreen(columns: 20, rows: 3);
		var before = screen.Version;

		screen.Feed("abcdef".AsSpan());

		// 高频输出不再为每个字符递增版本，渲染端每块只醒一次。
		Assert.Equal(before + 1, screen.Version);
	}

	[Fact]
	public void Osc52WritesToTheClipboardCallback()
	{
		var screen = NewScreen();
		string? clipboard = null;
		screen.ClipboardWrite = text => clipboard = text;

		var payload = Convert.ToBase64String("hello"u8);
		screen.Feed($"\u001b]52;c;{payload}\u0007".AsSpan());

		Assert.Equal("hello", clipboard);
	}

	[Fact]
	public void InsertModeShiftsTheRestOfTheLine()
	{
		var screen = NewScreen(columns: 10, rows: 2);

		screen.Feed("abc\u001b[1;1H\u001b[4hX\u001b[4l".AsSpan());

		Assert.Equal("Xabc", screen.GetText());
	}

	[Fact]
	public void FocusReportingFollowsDecset1004()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[?1004h".AsSpan());
		Assert.True(screen.FocusReporting);

		screen.Feed("\u001b[?1004l".AsSpan());
		Assert.False(screen.FocusReporting);
	}

	[Fact]
	public void SgrTextStylesAreCarriedIntoRuns()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[3;4;9;2mX".AsSpan());

		var run = screen.GetRuns(0, [])[0];
		Assert.True(run.Italic);
		Assert.True(run.Underline);
		Assert.True(run.Strikethrough);
		Assert.True(run.Dim);
	}

	[Fact]
	public void LineVersionsTrackEditedLinesAndSurviveHeightOnlyResize()
	{
		var screen = NewScreen(columns: 10, rows: 4);

		screen.Feed("abc\r\ndef".AsSpan());
		var first = screen.LineVersion(0);
		var second = screen.LineVersion(1);

		screen.Feed("\u001b[2;1HX".AsSpan());

		Assert.Equal(first, screen.LineVersion(0));
		Assert.NotEqual(second, screen.LineVersion(1));

		// 只改高度时行内容没变，行版本不动，渲染端可以继续沿用未移动的行。
		var stable = screen.LineVersion(0);
		screen.Resize(10, 6);
		Assert.Equal(stable, screen.LineVersion(0));

		// 列宽变化会重排每一行，缓存必须整体作废。
		screen.Resize(12, 6);
		Assert.NotEqual(stable, screen.LineVersion(0));
	}

	[Fact]
	public void DoubleClickSelectionPicksAWord()
	{
		var screen = NewScreen(columns: 20, rows: 2);

		screen.Feed("hello world".AsSpan());
		screen.SelectWord(0, 8);

		Assert.Equal("world", screen.GetSelectionText());

		screen.SelectLine(0);
		Assert.Equal("hello world", screen.GetSelectionText());
	}

	[Fact]
	public void QueriesAreAnsweredThroughTheCallback()
	{
		var screen = NewScreen();
		var responses = new List<string>();
		screen.Respond = responses.Add;

		screen.Feed("abc\u001b[6n\u001b[c".AsSpan());

		Assert.Equal("\u001b[1;4R", responses[0]);
		Assert.Equal("\u001b[?1;2c", responses[1]);
	}

	[Fact]
	public void ApplicationModesAreTracked()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[?1h\u001b[?2004h\u001b[?7l".AsSpan());

		Assert.True(screen.ApplicationCursorKeys);
		Assert.True(screen.BracketedPaste);
	}

	[Fact]
	public void TabsFollowDefaultAndCustomStops()
	{
		var screen = NewScreen(columns: 20, rows: 2);

		screen.Feed("\tX".AsSpan());
		Assert.Equal(8, screen.CursorColumn - 1);

		// HTS 在列 2 加一个自定义位，TBC 3 全清后退到末列。
		screen.Feed("\u001b[3G\u001bH\u001b[1G\tY".AsSpan());
		Assert.Equal(2, screen.CursorColumn - 1);

		screen.Feed("\u001b[3g\tZ".AsSpan());
		Assert.Equal(19, screen.CursorColumn);
	}

	[Fact]
	public void DecalnFillsTheScreenWithE()
	{
		var screen = NewScreen(columns: 4, rows: 2);

		screen.Feed("\u001b#8".AsSpan());

		Assert.Equal("EEEE\nEEEE", screen.GetText());
		Assert.Equal(0, screen.CursorRow);
		Assert.Equal(0, screen.CursorColumn);
	}

	[Fact]
	public void DecscusrSelectsTheCursorShape()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[3 q".AsSpan());
		Assert.Equal(TerminalCursorStyle.Underline, screen.CursorStyle);

		screen.Feed("\u001b[5 q".AsSpan());
		Assert.Equal(TerminalCursorStyle.Bar, screen.CursorStyle);

		screen.Feed("\u001b[1 q".AsSpan());
		Assert.Equal(TerminalCursorStyle.Block, screen.CursorStyle);
	}

	[Fact]
	public void DecstrRestoresDefaultsWithoutClearingText()
	{
		var screen = NewScreen();

		screen.Feed("a\u001b[3 q\u001b[!p".AsSpan());

		Assert.Equal(TerminalCursorStyle.Block, screen.CursorStyle);
		Assert.Equal("a", screen.GetText());
	}

	[Fact]
	public void ReverseVideoTracksDecscnm()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[?5h".AsSpan());
		Assert.True(screen.ReverseVideo);

		screen.Feed("\u001b[?5l".AsSpan());
		Assert.False(screen.ReverseVideo);
	}

	[Fact]
	public void Osc8AttachesLinksToCells()
	{
		var screen = NewScreen();

		screen.Feed("\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\ plain".AsSpan());

		Assert.Equal("https://example.com", screen.LinkAt(0, 0));
		Assert.Equal("https://example.com", screen.LinkAt(0, 3));
		Assert.Null(screen.LinkAt(0, 5));

		var runs = screen.GetRuns(0, []);
		Assert.True(runs[0].Link);
		Assert.False(runs[^1].Link);
	}

	[Fact]
	public void NarrowingReflowsSoftWrappedLines()
	{
		var screen = NewScreen(columns: 20, rows: 4);

		screen.Feed("abcdefghijklmnopqrst".AsSpan());
		screen.Resize(10, 4);

		Assert.Equal("abcdefghij\nklmnopqrst", screen.GetText());
		Assert.Equal(1, screen.CursorRow);
		Assert.Equal(9, screen.CursorColumn);
	}

	[Fact]
	public void WideningRejoinsSoftWrappedLines()
	{
		var screen = NewScreen(columns: 5, rows: 3);

		screen.Feed("abcdefgh".AsSpan());
		Assert.Equal("abcde\nfgh", screen.GetText());

		screen.Resize(20, 3);

		Assert.Equal("abcdefgh", screen.GetText());
		Assert.Equal(0, screen.CursorRow);
		Assert.Equal(8, screen.CursorColumn);
	}

	[Fact]
	public void ReflowKeepsHardLineBreaks()
	{
		var screen = NewScreen(columns: 20, rows: 4);

		screen.Feed("abc\r\n\r\ndef".AsSpan());
		screen.Resize(10, 4);

		Assert.Equal("abc\n\ndef", screen.GetText());
	}

	[Fact]
	public void BlockSelectionCopiesARectangle()
	{
		var screen = NewScreen(columns: 6, rows: 3);
		screen.Feed("abc\r\ndef\r\nghi".AsSpan());

		screen.BeginSelection(0, 1, block: true);
		screen.ExtendSelection(2, 2);

		Assert.Equal("bc\nef\nhi", screen.GetSelectionText());

		var range = screen.GetSelectionRange(1);
		Assert.NotNull(range);
		Assert.Equal(1, range.Value.From);
		Assert.Equal(2, range.Value.To);
	}

	[Fact]
	public void DecrqmReportsTrackedModes()
	{
		var screen = NewScreen();
		string? response = null;
		screen.Respond = text => response = text;

		screen.Feed("\u001b[?7$p".AsSpan());
		Assert.Equal("\u001b[?7;1$y", response);

		screen.Feed("\u001b[?2004$p".AsSpan());
		Assert.Equal("\u001b[?2004;2$y", response);

		screen.Feed("\u001b[9999$p".AsSpan());
		Assert.Equal("\u001b[9999;0$y", response);
	}

	[Fact]
	public void UrxvtMouseModeIsTracked()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[?1015h".AsSpan());
		Assert.True(screen.UrxvtMouse);

		screen.Feed("\u001b[?1015l".AsSpan());
		Assert.False(screen.UrxvtMouse);
	}

	[Fact]
	public void Osc4OverridesPaletteAndAnswersQueries()
	{
		var screen = NewScreen();
		string? response = null;
		screen.Respond = text => response = text;

		screen.Feed("\u001b]4;1;#ff0000\u001b\\".AsSpan());
		Assert.Equal(0xFF0000u, screen.Palette[1]!.Value);

		screen.Feed("\u001b]4;1;?\u001b\\".AsSpan());
		Assert.Equal("\u001b]4;1;rgb:ffff/0000/0000\u001b\\", response);

		screen.Feed("\u001b]104;1\u001b\\".AsSpan());
		Assert.Null(screen.Palette[1]);
	}

	[Fact]
	public void OscDefaultsTrackAndResetTheHostColors()
	{
		var screen = NewScreen();
		screen.HostBackground = 0x101010;
		string? response = null;
		screen.Respond = text => response = text;

		screen.Feed("\u001b]11;rgb:00/00/00\u001b\\".AsSpan());
		Assert.Equal(0x000000u, screen.DefaultBackgroundRgb);

		var version = screen.ColorVersion;
		screen.Feed("\u001b]111\u001b\\".AsSpan());
		Assert.Null(screen.DefaultBackgroundRgb);
		Assert.True(screen.ColorVersion > version);

		screen.Feed("\u001b]11;?\u001b\\".AsSpan());
		Assert.Equal("\u001b]11;rgb:1010/1010/1010\u001b\\", response);

		screen.Feed("\u001b]12;#0f0\u001b\\".AsSpan());
		Assert.Equal(0x00FF00u, screen.CursorColorRgb);
	}

	[Fact]
	public void DecscusrAndMode12ControlCursorBlink()
	{
		var screen = NewScreen();

		screen.Feed("\u001b[3 q".AsSpan());
		Assert.True(screen.CursorBlink);
		Assert.Equal(TerminalCursorStyle.Underline, screen.CursorStyle);

		screen.Feed("\u001b[4 q".AsSpan());
		Assert.False(screen.CursorBlink);

		screen.Feed("\u001b[?12h".AsSpan());
		Assert.True(screen.CursorBlink);

		string? response = null;
		screen.Respond = text => response = text;
		screen.Feed("\u001b[?12$p".AsSpan());
		Assert.Equal("\u001b[?12;1$y", response);
	}

	[Fact]
	public void PixelMouseModeIsTrackedAndReported()
	{
		var screen = NewScreen();
		string? response = null;
		screen.Respond = text => response = text;

		screen.Feed("\u001b[?1016h".AsSpan());
		Assert.True(screen.PixelMouse);

		screen.Feed("\u001b[?1016$p".AsSpan());
		Assert.Equal("\u001b[?1016;1$y", response);

		screen.Feed("\u001b[?1016l".AsSpan());
		Assert.False(screen.PixelMouse);
	}

	[Fact]
	public void EraseScrollbackDropsHistory()
	{
		var screen = NewScreen(columns: 10, rows: 3);

		screen.Feed("1\r\n2\r\n3\r\n4\r\n5".AsSpan());
		Assert.True(screen.ScrollbackLines > 0);

		screen.Feed("\u001b[3J".AsSpan());
		Assert.Equal(0, screen.ScrollbackLines);
	}

	[Fact]
	public void HeightChangeKeepsTheCursorOnItsLine()
	{
		var screen = NewScreen(columns: 10, rows: 4);

		screen.Feed("a\r\nb\r\nc\r\nd\r\ne\r\nf".AsSpan());
		Assert.Equal(3, screen.CursorRow);

		// 变矮：视口向底部收，光标跟着「f」那一行往下走，不跳到别的行上。
		screen.Resize(10, 2);
		Assert.Equal("e\nf", screen.GetText());
		Assert.Equal(1, screen.CursorRow);

		// 变高：多出来的历史从顶部长出来，光标仍在「f」上。
		screen.Resize(10, 5);
		Assert.Equal("b\nc\nd\ne\nf", screen.GetText());
		Assert.Equal(4, screen.CursorRow);
	}

}
