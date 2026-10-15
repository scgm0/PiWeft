using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-01：桌面对话框渲染问题并把关闭/取消转成取消响应。</summary>
[Collection("mewui-controls")]
public sealed class AskQuestionPanelTests
{
	private static readonly List<AskQuestion> Questions =
	[
		new("q1", "select", "选哪个？", [new AskOption("选项 A", "A", "说明 A"), new AskOption("选项 B", "B", null)], null, null),
		new("q2", "input", "补充说明？", null, "写点什么", null),
	];

	[Fact]
	public void PanelRendersEveryQuestion()
	{
		var panel = new AskQuestionPanel();
		var root = new Border();
		_ = new Window().Content(root);

		_ = panel.ShowAsync(root, Questions, CancellationToken.None);

		Assert.True(panel.IsOpen);
		Assert.Contains(Find<TextBlock>(panel.View), block => block.Text == "选哪个？");
		Assert.Contains(Find<TextBlock>(panel.View), block => block.Text == "补充说明？");
		// select 用下拉框渲染，input 用文本框。
		Assert.NotEmpty(Find<ComboBox>(panel.View));
		Assert.Contains(Find<TextBox>(panel.View), box => box.Placeholder == "写点什么");
		panel.Close();
	}

	[Fact]
	public async Task CloseResolvesACancelledResponse()
	{
		var panel = new AskQuestionPanel();
		var root = new Border();
		_ = new Window().Content(root);

		var pending = panel.ShowAsync(root, Questions, CancellationToken.None);
		panel.Close();

		var response = await pending;
		Assert.NotNull(response);
		Assert.True(AskQuestions.ParseResponse(response, Questions).Cancelled);
	}
}
