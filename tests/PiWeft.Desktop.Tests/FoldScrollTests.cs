using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Reflection;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class FoldScrollTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ManualFoldStopsEndFollowingAndKeepsItsHeaderAtTheSameReadingPosition(bool keyboard)
	{
		var fold = new Expander().Header(new TextBlock().Text("process")).IsExpanded(false)
			.Content(new Border().Height(800));
		var body = new StackPanel().Children(new Border().Height(1000), fold, new Border().Height(100));
		var scroll = new ScrollViewer().Content(body).AutoVerticalScroll().NoHorizontalScroll();
		using var guard = new TextFocusScrollGuard(scroll);
		using var conversation = new ConversationScroll(scroll, _ => { });
		guard.TrackFold(fold);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.True(conversation.Following);
		var y = fold.Bounds.Y;
		// Simulate the reading-intent callback immediately before the native toggle.
		using var inputGuard = new TextFocusScrollGuard(scroll, conversation.PreserveAnchor);
		inputGuard.TrackFold(fold);
		if (!keyboard) conversation.PreserveAnchor(fold);
		Toggle(fold, keyboard);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		conversation.RestoreAnchor();
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.True(fold.IsExpanded);
		Assert.False(conversation.Following);
		Assert.InRange(Math.Abs(fold.Bounds.Y - y), 0, 1);
		if (!keyboard) conversation.PreserveAnchor(fold);
		Toggle(fold, keyboard);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		conversation.RestoreAnchor();
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.False(fold.IsExpanded);
		Assert.False(conversation.Following);
		Assert.InRange(Math.Abs(fold.Bounds.Y - y), 0, 1);
		UiLifetime.Release(scroll);
	}

	[Fact]
	public void VirtualizedProcessDoesNotJumpToTheAnswerWhenExpandedAtTheBottom()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		using var timeline = new ConversationTimeline(entry =>
		{
			if (entry.Role == ChatRole.Process)
				return new ExecutionProcessView(entry, _ => new Border().Height(100), guard).View;
			return new Border().Height(100).Child(new TextBlock().Text(entry.Text));
		});
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (guard)
		using (conversation)
		{
			var rows = Enumerable.Range(0, 50).Select(i => new ChatEntry { Role = ChatRole.User, Text = $"history {i}", EntryId = $"history-{i}" }).ToList();
			rows.Add(new ChatEntry { Role = ChatRole.Process, Text = "", EntryId = "process", ProcessEntries = Enumerable.Range(0, 8)
				.Select(i => new ChatEntry { Role = ChatRole.Assistant, Text = $"step {i}", EntryId = $"step-{i}" }).ToArray() });
			rows.Add(new ChatEntry { Role = ChatRole.Assistant, Text = "answer", EntryId = "answer" });
			timeline.SetEntries(rows);
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			var fold = VisualTree.Find<Expander>(timeline.View).Single();
			var y = fold.Bounds.Y;
			var offset = timeline.Scroll.VerticalOffset;
			conversation.PreserveAnchor(fold);
			Toggle(fold, false);
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			conversation.RestoreAnchor();
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			Assert.True(Math.Abs(fold.Bounds.Y - y) <= 1,
				$"Header moved from {y} to {fold.Bounds.Y}; offset {offset} -> {timeline.Scroll.VerticalOffset}; parent {fold.Parent?.GetType().Name}; visible folds {VisualTree.Find<Expander>(timeline.View).Count()}");
			Assert.False(conversation.Following);
		}
	}

	private static void Toggle(Expander fold, bool keyboard)
	{
		if (keyboard)
			typeof(UIElement).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fold, [new KeyEventArgs(Key.Space, 0)]);
		else
		{
			var point = new Point(fold.Bounds.X + 10, fold.Bounds.Y + 10);
			typeof(UIElement).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(fold, [new MouseEventArgs(point, point, MouseButton.Left)]);
		}
	}
}
