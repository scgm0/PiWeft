using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using PiWeft.Ai;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Security;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class WelcomeSurfaceTests
{
	[Fact]
	public void EnterStartsWithTheDraft()
	{
		var surface = new WelcomeSurface();
		var submitted = new List<string>();
		surface.Submitted += submitted.Add;
		surface.Input.Text = "看一下这个项目";

		Press(surface.Input, new KeyEventArgs(Key.Enter, 0));

		Assert.Equal(new[] { "看一下这个项目" }, submitted);
	}

	[Fact]
	public void EmptyDraftDoesNothing()
	{
		var surface = new WelcomeSurface();
		var submitted = new List<string>();
		surface.Submitted += submitted.Add;

		Press(surface.Input, new KeyEventArgs(Key.Enter, 0));

		Assert.Empty(submitted);
	}

	[Fact]
	public void ShiftEnterLeavesTheKeyToTheTextBox()
	{
		var surface = new WelcomeSurface();
		var submitted = new List<string>();
		surface.Submitted += submitted.Add;
		surface.Input.Text = "第一行";

		var key = new KeyEventArgs(Key.Enter, 0, ModifierKeys.Shift);
		Press(surface.Input, key);

		Assert.Empty(submitted);
	}

	[Fact]
	public void StartButtonSendsWhatEnterWould()
	{
		var surface = new WelcomeSurface();
		var submitted = new List<string>();
		surface.Submitted += submitted.Add;
		surface.Input.Text = "补一组测试";

		ImagePreviewTests.Click(surface.StartButton);

		Assert.Equal(new[] { "补一组测试" }, submitted);
	}

	[Fact]
	public void StarterChipsFillTheDraftWithoutSending()
	{
		var surface = new WelcomeSurface();
		var submitted = new List<string>();
		surface.Submitted += submitted.Add;

		var starter = Find<Button>(surface.View)
			.Single(button => ContentText(button) == Strings.StarterProjectLabel);
		ImagePreviewTests.Click(starter);

		Assert.Equal(Strings.StarterProjectPrompt, surface.Input.Text);
		Assert.Empty(submitted);
	}

	[Fact]
	public void TargetChipShowsTheDirectoryAndAsksForTheMenu()
	{
		var surface = new WelcomeSurface();
		var requested = 0;
		surface.TargetRequested += () => requested += 1;

		surface.SetTarget(Strings.SidebarTabChats, "/tmp/piweft-chat");
		ImagePreviewTests.Click(surface.TargetButton);

		Assert.Equal(1, requested);
		Assert.Contains(Find<TextBlock>(surface.View), text => text.Text == Strings.SidebarTabChats);
		Assert.Contains("/tmp/piweft-chat", HoverHints.Text(surface.TargetButton));
	}

	[Fact]
	public void DroppedFileBecomesAnAtReference()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-welcome-drop-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var path = Path.Combine(root, "note.txt");
			File.WriteAllText(path, "x");
			var surface = new WelcomeSurface();
			surface.SetTarget(Strings.SidebarTabChats, root);
			var data = new DataObject();
			data.SetStorageItems([path]);

			var args = Drop(surface, data);

			Assert.True(args.Accepted);
			Assert.Equal(" " + ComposerDrop.Reference(path) + " ", surface.Input.Text);
		}
		finally
		{
			TempDirectory.Delete(root);
		}
	}

	[Fact]
	public async Task DroppedImageWaitsInTheAttachmentBar()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-welcome-image-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var path = Path.Combine(root, "photo.png");
			File.WriteAllBytes(path, [1, 2, 3]);
			var surface = new WelcomeSurface();
			surface.SetTarget(Strings.SidebarTabChats, root);
			var data = new DataObject();
			data.SetStorageItems([path]);

			var args = Drop(surface, data);

			Assert.True(args.Accepted);
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (surface.PendingImages.Count == 0 && DateTime.UtcNow < deadline)
			{
				await Task.Delay(20);
			}

			Assert.Single(surface.PendingImages);
			Assert.Equal("image/png", surface.PendingImages[0].Content.MimeType);
			Assert.Contains(Find<TextBlock>(surface.View), text => text.Text == "photo.png");
		}
		finally
		{
			TempDirectory.Delete(root);
		}
	}

	[Fact]
	public void PromptTemplateTokensProjectToChipsAndDeleteAtomically()
	{
		var surface = new WelcomeSurface
		{
			ResourceProvider = () => new ResourceSnapshot
			{
				Templates =
				[
					new PromptTemplate { Name = "review", Content = string.Empty, FilePath = "/tmp/review.md" },
				],
			},
		};
		surface.SetTarget(Strings.SidebarTabChats, "/tmp");
		surface.Prefill("跑 /review 看看");
		ConversationPerformanceTests.Layout(surface.View, 960, 720);

		Assert.Equal(1, UserReferenceRichText.Count(surface.Input));
		var names = new HashSet<string>(StringComparer.Ordinal) { "review" };
		var command = Assert.IsType<UserCommandReference>(UserChips.Parse(surface.Input.Text, names)[0]);
		var line = surface.Input.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(command.End - command.Start,
			line.MapSourceOffsetToProjected(command.End) - line.MapSourceOffsetToProjected(command.Start));

		surface.Input.MoveCaret(command.End, false);
		Assert.True(UserReferenceRichText.TryHandleKey(surface.Input, new KeyEventArgs(Key.Backspace, 0)));
		Assert.DoesNotContain("/review", surface.Input.Text);
	}

	[Fact]
	public void FileReferencesProjectToChipsAndDeleteAtomically()
	{
		var surface = new WelcomeSurface();
		const string draft = "看 @\"src/App.cs\" 和 /skill:review";
		surface.Prefill(draft);
		ConversationPerformanceTests.Layout(surface.View, 960, 720);

		Assert.True(UserReferenceRichText.IsAttached(surface.Input));
		Assert.Equal(2, UserReferenceRichText.Count(surface.Input.Text));
		var file = Assert.IsType<UserFileReference>(UserChips.Parse(draft)[0]);
		var line = surface.Input.VisibleTextLines.Single(row => row.LogicalLine.Offset == 0);
		Assert.Equal(file.End - file.Start,
			line.MapSourceOffsetToProjected(file.End) - line.MapSourceOffsetToProjected(file.Start));

		// 与 composer 一样按整枚 chip 编辑：光标贴在右缘按 Backspace 删掉整段引用。
		surface.Input.MoveCaret(file.End, false);
		Assert.True(UserReferenceRichText.TryHandleKey(surface.Input, new KeyEventArgs(Key.Backspace, 0)));
		Assert.DoesNotContain("src/App.cs", surface.Input.Text);
		Assert.Equal(1, UserReferenceRichText.Count(surface.Input.Text));
	}

	[Fact]
	public void ToolbarShowsTheModelAndThinkingLikeTheSessionComposer()
	{
		var surface = new WelcomeSurface();
		var model = new Model { Id = "local-code", Provider = "local-openai", Name = "Local Code" };
		surface.SetModel(model, "high");

		Assert.Equal(model, surface.Model);
		Assert.Equal("high", surface.ThinkingLevel);
		Assert.Contains(Find<TextBlock>(surface.View), text => text.IsVisible && text.Text == "local-openai/Local Code");
		Assert.Contains(Find<TextBlock>(surface.View), text => text.IsVisible && text.Text == "· high");

		surface.SetModel(null, string.Empty);
		Assert.DoesNotContain(Find<TextBlock>(surface.View), text => text.Text == "local-openai/Local Code");
	}

	[Fact]
	public void ToolbarHasTheSessionComposerEntries()
	{
		var surface = new WelcomeSurface();
		var tips = Find<Button>(surface.View).Select(HoverHints.Text).ToArray();

		Assert.Contains(Strings.ComposerAddTitle, tips);
		Assert.Contains(Strings.QuickMessagesTitle, tips);
	}

	[Fact]
	public void SecurityButtonCarriesTheLevelForTheNextSession()
	{
		var surface = new WelcomeSurface();
		var level = ToolSecurity.Standard;
		surface.SetSecurity(SecurityConfig.Off, level, hasOverride: true);

		Assert.Contains(Find<Button>(surface.View), button =>
			HoverHints.Text(button) == Strings.SecurityOverrideHint(level.Name, level.Description));
	}

	[Fact]
	public void PlanModeRewritesThePlaceholderAndShowsTheChip()
	{
		var surface = new WelcomeSurface();
		surface.SetModeForTests(ComposerMode.Plan);

		Assert.Equal(ComposerMode.Plan, surface.Mode);
		Assert.Contains(Find<TextBlock>(surface.View), text => text.IsVisible && text.Text == Strings.ModePlan);
		Assert.Contains(Find<TextBlock>(surface.View),
			text => text.IsVisible && text.Text == Strings.ComposerPlaceholderFor(ComposerMode.Plan));

		surface.SetModeForTests(ComposerMode.Plan);
		Assert.Equal(ComposerMode.Normal, surface.Mode);
	}

	[Fact]
	public void ColumnFollowsTheComposerWidthAndShrinksWithTheWindow()
	{
		var surface = new WelcomeSurface();
		ConversationPerformanceTests.Layout(surface.View, 1200, 760);
		var card = Find<Border>(surface.View).Single(border => border.StyleName == AppStyles.Composer);
		Assert.Equal(AppTheme.MessageMaxWidth + AppTheme.Space6, card.Bounds.Width, 1);

		ConversationPerformanceTests.Layout(surface.View, 600, 760);
		Assert.Equal(600, card.Bounds.Width, 1);
	}

	private static string? ContentText(Button button) =>
		button.Content is StackPanel panel
			? Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text))
			: null;

	private static DragEventArgs Drop(WelcomeSurface surface, DataObject data)
	{
		var args = new DragEventArgs(data, new Point(1, 1), new Point(1, 1));
		typeof(UIElement).GetMethod("OnDrop", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(surface.View, [args]);
		return args;
	}

	private static void Press(UIElement element, KeyEventArgs args) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(element, [args]);
}
