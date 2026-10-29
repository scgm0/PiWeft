using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// ask_question 卡片钉在会话底部（对话区与输入框之间），与 PiDeck 的 SessionRuntimeUiOverlay 同义：
/// 不再有遮罩弹窗；卡片打开时输入区整块收起，答完再回来。
/// </summary>
[Collection("mewui-controls")]
public sealed class SessionTabAskTests
{
	private static readonly List<AskQuestion> Questions =
	[
		new("q1", "select", "选哪个？", [new AskOption("选项 A", "A", null), new AskOption("选项 B", "B", null)], null, null),
	];

	[Fact]
	public void AskCardSitsInTheComposerColumnAndCollapsesTheInputArea()
	{
		using var session = ConversationPerformanceTests.CreateSession(2);
		using var tab = new SessionTab(session, [session.Model], "ask", availableModels: null);
		Assert.False(tab.AskPending);
		Assert.True(tab.InputAreaVisible);

		var pending = tab.AskPanel.ShowAsync(Questions, review: false, CancellationToken.None);

		Assert.True(tab.AskPending);
		Assert.False(tab.InputAreaVisible);
		// 卡片在会话视图里（不是窗口 OverlayLayer），选项、自定义输入与关闭键都在。
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "选哪个？");
		Assert.Contains(Find<Button>(tab.View), button => ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true);

		tab.AskPanel.Close();

		Assert.False(tab.AskPending);
		Assert.True(tab.InputAreaVisible);
		Assert.True(AskQuestions.ParseResponse(pending.Result, Questions).Cancelled);
	}

	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			StackPanel panel => Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text)),
			_ => null,
		};
}
