using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-01：会话内联问询卡片按 PiDeck 的底部卡片渲染批量问题，并返回同构 JSON。</summary>
[Collection("mewui-controls")]
public sealed class AskQuestionPanelTests
{
	private static readonly List<AskQuestion> Questions =
	[
		new("q1", "select", "选哪个？", [new AskOption("选项 A", "A", "说明 A"), new AskOption("选项 B", "B", null)], null, null),
		new("q2", "input", "补充说明？", null, "写点什么", null),
	];

	private static (AskQuestionPanel Panel, Task<string?> Pending) Show(bool review = false)
	{
		var panel = new AskQuestionPanel();
		var root = new StackPanel();
		_ = new Window().Content(root);
		root.Children(panel.View);
		var pending = panel.ShowAsync(Questions, review, CancellationToken.None);
		return (panel, pending);
	}

	[Fact]
	public void PanelRendersQuestionTabsAndOptionButtons()
	{
		var (panel, _) = Show();

		Assert.True(panel.IsOpen);
		// 批量题用一行标签：两道题都出现在标签条里，当前题在正文区。
		Assert.Contains(Find<TextBlock>(panel.View), block => block.Text == "选哪个？");
		Assert.Contains(Find<TextBlock>(panel.View), block => block.Text == "补充说明？");
		// 选项是按钮（PiDeck 的 outline 选项），不再是下拉框。
		Assert.Contains(Find<Button>(panel.View), button => ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true);
		Assert.Empty(Find<ComboBox>(panel.View));
		// select 仍保留自定义输入。
		Assert.Contains(Find<TextBox>(panel.View), box => box.Placeholder == Strings.AskCustomPlaceholder);
		panel.Close();
	}

	[Fact]
	public async Task AnsweringOptionsAdvancesAndSubmitsTheBatch()
	{
		var (panel, pending) = Show();

		var option = Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true);
		ImagePreviewTests.Click(option);

		// 第二题是自由输入：填好内容后点页脚提交。
		var input = Find<TextBox>(panel.View).Single(box => box.Placeholder == "写点什么");
		input.Text = "补充内容";
		var submit = Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskSubmit && button.IsEnabled);
		ImagePreviewTests.Click(submit);

		var response = await pending;
		var (answers, cancelled) = AskQuestions.ParseResponse(response, Questions);
		Assert.False(cancelled);
		Assert.Equal(2, answers.Count);
		Assert.Equal("A", answers[0].Value!.GetValue<string>());
		Assert.Equal("补充内容", answers[1].Value!.GetValue<string>());
	}

	[Fact]
	public async Task ReviewTabCollectsTheLastSubmit()
	{
		var (panel, pending) = Show(review: true);

		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));

		var input = Find<TextBox>(panel.View).Single(box => box.Placeholder == "写点什么");
		input.Text = "审阅内容";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskNext && button.IsEnabled));

		// 审阅页：全部题目与答案都在，提交键是「提交全部」。
		Assert.Contains(Find<TextBlock>(panel.View), block => block.Text == Strings.AskReviewTitle);
		var submitAll = Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskSubmitAll && button.IsEnabled);
		ImagePreviewTests.Click(submitAll);

		var (answers, cancelled) = AskQuestions.ParseResponse(await pending, Questions);
		Assert.False(cancelled);
		Assert.Equal(2, answers.Count);
		Assert.Equal("A", answers[0].Value!.GetValue<string>());
	}

	[Fact]
	public async Task CustomAnswerOnSelectIsMarkedLikePiDeck()
	{
		var (panel, pending) = Show();

		var custom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
		custom.Text = "自定义选项";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskCustomUse && button.IsEnabled));

		// 自定义答案会把 select 也答完并推进到第二题，补完输入后整批提交。
		var input = Find<TextBox>(panel.View).Single(box => box.Placeholder == "写点什么");
		input.Text = "说明";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskSubmit && button.IsEnabled));

		var response = await pending;
		var (answers, cancelled) = AskQuestions.ParseResponse(response, Questions);
		Assert.False(cancelled);
		Assert.Equal("自定义选项", answers[0].Value!.GetValue<string>());
		Assert.True(answers[0].WasCustom);
	}

	[Fact]
	public async Task EscapeCancelsTheWholeBatch()
	{
		var (panel, pending) = Show();

		// MewUI 的 OnKeyDown 是受保护方法，测试用同一条入口递键（与 ModelPickerTests 一致）。
		typeof(UIElement)
			.GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(panel.View, [new KeyEventArgs(Key.Escape, 0)]);

		Assert.False(panel.IsOpen);
		Assert.True(AskQuestions.ParseResponse(await pending, Questions).Cancelled);
	}

	[Fact]
	public async Task CloseResolvesACancelledResponse()
	{
		var (panel, pending) = Show();

		panel.Close();

		var response = await pending;
		Assert.NotNull(response);
		Assert.True(AskQuestions.ParseResponse(response, Questions).Cancelled);
	}

	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			StackPanel panel => Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text)),
			Grid grid => Find<TextBlock>(grid).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text)),
			_ => null,
		};
}
