using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using PiWeft.CodingAgent;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

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
	public void TextDraftsSurviveNavigation()
	{
		var panel = new AskQuestionPanel();
		var host = new StackPanel();
		_ = new Window().Content(host);
		host.Children(panel.View);
		_ = panel.ShowAsync(
			[
				new AskQuestion("q1", "input", "第一题？", null, "第一题占位", null),
				new AskQuestion("q2", "input", "第二题？", null, "第二题占位", null),
			],
			review: false,
			CancellationToken.None);

		var first = Find<TextBox>(panel.View).Single(box => box.Placeholder == "第一题占位");
		first.Text = "第一题草稿";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskNext && button.IsEnabled));

		var second = Find<TextBox>(panel.View).Single(box => box.Placeholder == "第二题占位");
		second.Text = "第二题草稿";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskPrevious));

		var firstAgain = Find<TextBox>(panel.View).Single(box => box.Placeholder == "第一题占位");
		Assert.Equal("第一题草稿", firstAgain.Text);
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskNext && button.IsEnabled));

		var secondAgain = Find<TextBox>(panel.View).Single(box => box.Placeholder == "第二题占位");
		Assert.Equal("第二题草稿", secondAgain.Text);
		panel.Close();
	}

	[Fact]
	public void CustomDraftsSurviveNavigationAndOptionToggles()
	{
		var panel = new AskQuestionPanel();
		var host = new StackPanel();
		_ = new Window().Content(host);
		host.Children(panel.View);
		_ = panel.ShowAsync(
			[
				new AskQuestion("q1", "select", "选哪个？", [new AskOption("选项 A", "A", null)], null, null),
				new AskQuestion("q2", "multi_select", "多选？", [new AskOption("选项 B", "B", null)], null, null),
			],
			review: false,
			CancellationToken.None);

		var selectCustom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
		selectCustom.Text = "单选自定义草稿";
		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));

		// 第二题是多选：勾选项会重绘正文，自定义输入要从草稿恢复。
		var multiCustom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
		multiCustom.Text = "多选自定义草稿";
		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 B", StringComparison.Ordinal) == true));

		multiCustom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
		Assert.Equal("多选自定义草稿", multiCustom.Text);
		Assert.True(Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskCustomAdd).IsEnabled);

		// 回到第一题：单选自定义草稿也要还在，且「使用」可用。
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskPrevious));
		selectCustom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
		Assert.Equal("单选自定义草稿", selectCustom.Text);
		Assert.True(Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskCustomUse).IsEnabled);
		panel.Close();
	}

	[Fact]
	public void EditorDraftsSurviveNavigation()
	{
		var panel = new AskQuestionPanel();
		var host = new StackPanel();
		_ = new Window().Content(host);
		host.Children(panel.View);
		_ = panel.ShowAsync(
			[
				new AskQuestion("q1", "select", "选哪个？", [new AskOption("选项 A", "A", null)], null, null),
				new AskQuestion("q2", "editor", "写点什么？", null, "编辑器占位", null),
			],
			review: false,
			CancellationToken.None);

		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));
		var editor = Find<MultiLineTextBox>(panel.View).Single(box => box.Placeholder == "编辑器占位");
		editor.Text = "多行草稿";
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button =>
			ContentText(button) == Strings.AskPrevious));

		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));
		editor = Find<MultiLineTextBox>(panel.View).Single(box => box.Placeholder == "编辑器占位");
		Assert.Equal("多行草稿", editor.Text);
		panel.Close();
	}

	[Fact]
	public void NewBatchResetsDrafts()
	{
		var panel = new AskQuestionPanel();
		var host = new StackPanel();
		_ = new Window().Content(host);
		host.Children(panel.View);
		_ = panel.ShowAsync(
			[new AskQuestion("q1", "input", "题？", null, "旧占位", null)],
			review: false,
			CancellationToken.None);

		var oldBox = Find<TextBox>(panel.View).Single(box => box.Placeholder == "旧占位");
		oldBox.Text = "旧草稿";
		panel.Close();
		// 旧控件已脱离树；迟到的 TextChanged 必须被世代号忽略，不能写进下一批草稿。
		oldBox.Text = "关闭后写入";
		_ = panel.ShowAsync(
			[new AskQuestion("q1", "input", "题？", null, "新占位", null)],
			review: false,
			CancellationToken.None);

		Assert.Equal(string.Empty, Find<TextBox>(panel.View).Single(box => box.Placeholder == "新占位").Text);
		panel.Close();
	}

	[Fact]
	public void DroppingAPathInsertsAReferenceAtTheCaret()
	{
		var root = Path.Combine(Path.GetTempPath(), "pi-ask-drop-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var file = Path.Combine(root, "file with spaces.txt");
			File.WriteAllText(file, "text");
			var panel = new AskQuestionPanel(cwd: () => root);
			var host = new StackPanel();
			_ = new Window().Content(host);
			host.Children(panel.View);
			_ = panel.ShowAsync(
				[new AskQuestion("q1", "input", "哪个文件？", null, "写点什么", null)],
				review: false,
				CancellationToken.None);

			var box = Find<TextBox>(panel.View).Single(candidate => candidate.Placeholder == "写点什么");
			box.Text = "before after";
			box.Select(6, 0);

			var data = new DataObject();
			data.SetStorageItems([file]);
			var over = new DragEventArgs(data, default, default);
			RaiseDrag(panel.View, "OnDragOver", over);
			Assert.True(over.Accepted);
			Assert.Equal(DragDropEffects.Copy, over.Effect);

			var drop = new DragEventArgs(data, default, default);
			RaiseDrag(panel.View, "OnDrop", drop);
			Assert.True(drop.Accepted);
			var reference = ComposerDrop.Reference(file);
			Assert.Equal($"before {reference} after", box.Text);
			Assert.Equal(6 + reference.Length + 1, box.SelectionStart);
			panel.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void DroppingAPathTargetsTheSelectCustomInput()
	{
		var root = Path.Combine(Path.GetTempPath(), "pi-ask-drop-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var file = Path.Combine(root, "file.txt");
			File.WriteAllText(file, "text");
			var panel = new AskQuestionPanel(cwd: () => root);
			var host = new StackPanel();
			_ = new Window().Content(host);
			host.Children(panel.View);
			_ = panel.ShowAsync(
				[new AskQuestion("q1", "select", "选哪个？", [new AskOption("选项 A", "A", null)], null, null)],
				review: false,
				CancellationToken.None);

			var custom = Find<TextBox>(panel.View).Single(box => box.Placeholder == Strings.AskCustomPlaceholder);
			var data = new DataObject();
			data.SetStorageItems([file]);
			var drop = new DragEventArgs(data, default, default);
			RaiseDrag(panel.View, "OnDrop", drop);

			Assert.True(drop.Accepted);
			Assert.Equal(ComposerDrop.Reference(file), custom.Text);
			Assert.True(Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskCustomUse).IsEnabled);
			panel.Close();
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ConfirmQuestionRejectsPathDropsInsteadOfLeakingToTheHiddenComposer()
	{
		var panel = new AskQuestionPanel();
		var host = new StackPanel();
		_ = new Window().Content(host);
		host.Children(panel.View);
		_ = panel.ShowAsync(
			[new AskQuestion("q1", "confirm", "继续？", null, null, null)],
			review: false,
			CancellationToken.None);

		var data = new DataObject();
		data.SetStorageItems([Path.GetTempPath()]);
		var args = new DragEventArgs(data, default, default);
		RaiseDrag(panel.View, "OnDragOver", args);

		Assert.False(args.Accepted);
		Assert.Equal(DragDropEffects.None, args.Effect);
		Assert.True(args.Handled);
		panel.Close();
	}

	private static void RaiseDrag(UIElement target, string name, DragEventArgs args) =>
		typeof(UIElement).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [args]);

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
	public async Task SkippingEveryQuestionSubmitsSettledAnswers()
	{
		var (panel, pending) = Show();

		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskSkip));
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskSkip));

		var response = AskQuestions.ParseResponse(await pending, Questions);
		Assert.False(response.Cancelled);
		Assert.False(response.Closed);
		Assert.All(response.Answers, static answer => Assert.True(answer.Skipped));
	}

	[Fact]
	public async Task ClosingAfterAPartialAnswerKeepsTheAnswer()
	{
		var (panel, pending) = Show();

		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));
		panel.Close();

		var response = AskQuestions.ParseResponse(await pending, Questions);
		Assert.True(response.Closed);
		Assert.False(response.Cancelled);
		Assert.Equal("A", Assert.Single(response.Answers).Value!.GetValue<string>());
		Assert.Contains("用户关闭了提问卡", AskQuestions.ResultText(Questions, response.Answers, response.Cancelled, response.Closed));
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

	[Fact]
	public void FooterActionLeavesItsColorToThePrimaryButtonItself()
	{
		// 页脚主按钮的底色是强调色（深色主题下是近白）。以前文字复用了「正文主色」的
		// Label()，近白的字压在近白的底上，「下一题」看着就像一块禁用态的灰斑。
		// 修法不是把字写死成 OnAccentText：那样禁用时又会变成深色字压深色底；
		// 而是不给标签任何前景色与角色，让它继承按钮：可用 TextOnAccent、禁用 TextFaint。
		// 「下一题」只在中间的自由输入题上出现，所以这里拿三道题。
		var panel = new AskQuestionPanel();
		var root = new StackPanel();
		_ = new Window().Content(root);
		root.Children(panel.View);
		_ = panel.ShowAsync(
			[
				new AskQuestion("q1", "select", "选哪个？", [new AskOption("选项 A", "A", null)], null, null),
				new AskQuestion("q2", "input", "中间题？", null, "写点什么", null),
				new AskQuestion("q3", "input", "最后一题？", null, "再写点", null),
			],
			review: false,
			CancellationToken.None);

		ImagePreviewTests.Click(Find<Button>(panel.View).First(button =>
			ContentText(button)?.Contains("选项 A", StringComparison.Ordinal) == true));

		var next = Find<Button>(panel.View).Single(button => ContentText(button) == Strings.AskNext);
		Assert.Equal(AppStyles.PrimaryButton, next.StyleName);
		var label = Assert.IsType<TextBlock>(next.Content);
		// 角色标签同样会覆盖继承（换主题时按角色重上色），所以必须为空。
		Assert.Null(label.Tag);
		panel.Close();
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
