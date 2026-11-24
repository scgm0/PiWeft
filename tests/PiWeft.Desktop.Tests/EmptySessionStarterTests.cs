using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class EmptySessionStarterTests
{
	private const double Width = 960;
	private const double Height = 720;

	[Fact]
	public void StartersPrefillWithoutSending()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "empty");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		var labels = new[] { Strings.StarterProjectLabel, Strings.StarterChangesLabel, Strings.StarterTestsLabel };
		var starters = Find<Button>(tab.View)
			.Where(button => ContentText(button) is { } text && labels.Contains(text))
			.ToArray();

		Assert.Equal(labels.Length, starters.Length);

		ImagePreviewTests.Click(starters[0]);

		var composer = Find<MultiLineTextBox>(tab.View).Single(text => !text.IsReadOnly);
		Assert.Equal(Strings.StarterProjectPrompt, composer.Text);

		// 只填进输入框：起步建议不能替用户花掉一次模型调用。
		Assert.Empty(session.Session.Entries);
	}

	[Fact]
	public void TitleStaysOnTheDesignTypeScale()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "empty");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		var title = Find<TextBlock>(tab.View).Single(text => text.Text == Strings.WelcomeTitle);
		// 与准备会话时的欢迎标题同一档，不再比同一页的其它标题大一倍。
		Assert.Equal(20, title.FontSize, 1);
	}

	private static string? ContentText(Button button) =>
		button.Content is StackPanel panel
			? Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text))
			: null;
}
