using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SessionHeaderStateTests
{
	private const double Width = 960;
	private const double Height = 720;

	[Fact]
	public void ChatAndBranchToggleShowsTheActiveView()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "history");
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		var chat = Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.ViewChat);
		var branch = Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.ViewBranches);

		Assert.Equal(AppStyles.SelectedButton, chat.StyleName);
		Assert.Equal(AppStyles.GhostButton, branch.StyleName);

		ImagePreviewTests.Click(branch);
		ConversationPerformanceTests.Layout(tab.View, Width, Height);

		Assert.Equal(AppStyles.GhostButton, chat.StyleName);
		Assert.Equal(AppStyles.SelectedButton, branch.StyleName);
	}

	[Fact]
	public void NarrowChatShortensTheModelNameplateInsteadOfWrappingTheToolbar()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "history");
		var label = Assert.IsType<TextBlock>(tab.ModelButtonForTests.Content);

		ConversationPerformanceTests.Layout(tab.View, 520, Height);
		Assert.Equal(session.Model.Name, label.Text);

		ConversationPerformanceTests.Layout(tab.View, Width, Height);
		Assert.Equal($"{session.Model.Provider}/{session.Model.Name}", label.Text);
	}
}
