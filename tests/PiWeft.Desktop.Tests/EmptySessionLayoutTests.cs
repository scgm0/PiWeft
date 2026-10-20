using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>空会话的输入区：随提示文本一起居中、贴在其下方，且不再显示工作区路径。</summary>
[Collection("mewui-controls")]
public sealed class EmptySessionLayoutTests
{
	private const double Width = 960;
	private const double Height = 720;

	[Fact]
	public void EmptySessionCentersTheComposerUnderThePrompt()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "empty");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		var composer = Composer(tab);
		var column = Column(composer);
		var prompt = Find<TextBlock>(tab.View).Single(text => text.Text == Strings.WelcomeTitle);

		// 提示文本在输入框上方，两者同属居中的那一列。
		Assert.True(prompt.Bounds.Bottom <= composer.Bounds.Y);
		Assert.True(column.Bounds.Y <= prompt.Bounds.Y);
		Assert.InRange(column.Bounds.Y, (Height - column.Bounds.Height) / 2 - 1, (Height - column.Bounds.Height) / 2 + 1);
	}

	[Fact]
	public void SessionWithHistoryDocksTheComposerToTheBottom()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "history");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		Assert.True(Composer(tab).Bounds.Y > Height * 0.7);
	}

	[Fact]
	public void ScrollToLatestIsAFloatingBottomCenterButton()
	{
		using var session = ConversationPerformanceTests.CreateSession(20);
		using var tab = new SessionTab(session, [session.Model], "history");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		var button = Find<Button>(tab.View).Single(candidate => HoverHints.Text(candidate) == Strings.ScrollToLatest);
		var shell = Assert.IsType<Grid>(button.Parent);
		Assert.Equal(HorizontalAlignment.Center, shell.HorizontalAlignment);
		Assert.Equal(VerticalAlignment.Bottom, shell.VerticalAlignment);
		Assert.Equal(36, button.Width, 1);
		Assert.Equal(36, button.Height, 1);
		Assert.True(button.IsVisible);
		Assert.Equal(0, button.Opacity);
		Assert.False(button.IsHitTestVisible);

		// 隐藏期间也必须参与布局；重新出现时图标中心仍与 36 DIP 圆钮中心重合。
		button.Opacity = 1;
		ConversationPerformanceTests.Layout(tab.View, Width, Height - 120);
		ConversationPerformanceTests.Layout(tab.View, Width, Height);
		var icon = Find<Image>(button).Single();
		Assert.Equal(button.Bounds.X + button.Bounds.Width / 2, icon.Bounds.X + icon.Bounds.Width / 2, 1);
		Assert.Equal(button.Bounds.Y + button.Bounds.Height / 2, icon.Bounds.Y + icon.Bounds.Height / 2, 1);
	}

	[Fact]
	public void ComposerNeverShowsTheWorkspacePath()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "empty");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		Assert.DoesNotContain(Find<TextBlock>(tab.View), text => text.Text == session.Cwd);
	}

	private static MultiLineTextBox Composer(SessionTab tab) =>
		Find<MultiLineTextBox>(tab.View).Single(text => !text.IsReadOnly);

	/// <summary>输入框所在的输入区整列（提示文本 + 输入框 + 状态栏）。</summary>
	private static StackPanel Column(Element composer)
	{
		StackPanel? column = null;
		for (var element = composer.Parent; element is not null; element = element.Parent)
		{
			if (element is StackPanel panel) column = panel;
		}

		return column ?? throw new InvalidOperationException("composer is not inside a column");
	}
}
