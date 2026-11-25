using Xunit;

namespace PiWeft.Desktop.Tests;

/// 提示词/斜杠命令在草稿里画成命令 chip：只认白名单里的名字，路径与未知命令保持纯文本。
public sealed class UserCommandChipTests
{
	private static readonly IReadOnlySet<string> Names =
		new HashSet<string>(StringComparer.Ordinal) { "review", "plan", "usr" };

	[Fact]
	public void WhitelistedCommandsChipAndUnknownOnesStayPlain()
	{
		var chips = UserChips.Parse("用 /review 和 /plan 以及 /unknown，再看 /skill:mewui", Names);

		Assert.Equal(new[] { "review", "plan" }, chips.OfType<UserCommandReference>().Select(command => command.Name));
		Assert.Contains(chips, chip => chip.Kind == UserChipKind.Skill && chip.Value == "mewui");
		Assert.DoesNotContain(chips, chip => chip is UserCommandReference command && command.Name == "unknown");
	}

	[Fact]
	public void PathsCodeAndUrlsKeepTheCommandNameAsPlainText()
	{
		Assert.Empty(UserChips.Parse("/usr/bin /review/x", Names));
		Assert.Empty(UserChips.Parse("看 `/review` 和 https://example.com/review", Names));
	}

	[Fact]
	public void WithoutAWhitelistNothingBecomesACommandChip()
	{
		Assert.Empty(UserChips.Parse("用 /review 和 /plan"));
	}
}
