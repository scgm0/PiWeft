using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ReadingPreferencesTests
{
	[Fact]
	public void ReadingChangesUpdateTextAndWidthWithoutReplacingOrClearingDraft()
	{
		var original = new DesktopPreference { ChatFontSize = ReadingPreferences.FontSize, ReadingWidth = ReadingPreferences.Width };
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "answer" }] });
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = FakeStreams.Model(), Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = static (_, _, _) => throw new InvalidOperationException(), Persist = false,
		});
		using var tab = new SessionTab(session, [session.Model], "test");
		try
		{
			var composer = Find<MultiLineTextBox>(tab.View).Single(text => !text.IsReadOnly);
			composer.Text = "draft remains";
			composer.Select(2, 5);
			ReadingPreferences.Apply(new DesktopPreference { ChatFontSize = 20, ReadingWidth = 1360 });
			ConversationPerformanceTests.Layout(tab);
			Assert.Same(composer, Find<MultiLineTextBox>(tab.View).Single(text => !text.IsReadOnly));
			Assert.Equal("draft remains", composer.Text);
			Assert.Equal(2, composer.SelectionStart);
			Assert.Equal(5, composer.SelectionLength);
			Assert.Equal(20, composer.FontSize);
			Assert.Equal(20, Find<MarkdownView>(tab.View).Single().FontSize);
			Assert.All(Find<ReadingColumn>(tab.View), column => Assert.Equal(1360 + AppTheme.Space6, column.MaxWidth));
		}
		finally { ReadingPreferences.Apply(original); }
	}
}
