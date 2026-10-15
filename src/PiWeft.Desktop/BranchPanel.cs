using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// 会话分支树：把会话条目按 <c>id</c>/<c>parentId</c> 展开，每行都能「跳到此处」。
/// 数据来自 <see cref="SessionManager.BuildTree"/>，与 RPC 的 <c>get_tree</c> 同源。
/// </summary>
public sealed class BranchPanel
{
	private readonly Func<CodingAgentSession?> _session;
	private readonly StackPanel _tree = new();

	public BranchPanel(Func<CodingAgentSession?> session)
	{
		_session = session;
		_tree.Spacing(2);

		View = new Border()
			.StyleName(AppStyles.Window)
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(new StackPanel()
					.Spacing(AppTheme.Space2)
					.Padding(new Thickness(AppTheme.Space6))
					.Children(
						new TextBlock()
							.Text(Strings.BranchPanelTitle)
							.FontSize(18)
							.FontWeight(FontWeight.Bold)
							.PrimaryText(),
						new TextBlock()
							.Text(Strings.BranchPanelHint)
							.FontSize(AppTheme.FontSizeCaption)
							.MutedText()
							.TextWrapping(TextWrapping.Wrap),
						_tree))
				.AutoVerticalScroll()
				.NoHorizontalScroll());

		// The session tree is built only when the user opens the branch view.
	}

	public UIElement View { get; }

	/// <summary>重建树。每次切到该视图时调用，保证显示的是最新状态。</summary>
	public void Refresh()
	{
		_tree.Clear();

		var session = _session();
		if (session is null)
		{
			_tree.Children(new TextBlock().Text(Strings.NoOpenSession).FontSize(AppTheme.FontSizeCaption).MutedText());
			return;
		}

		var leafId = session.Session.LeafId;
		var roots = session.Session.BuildTree();

		foreach (var root in roots)
		{
			AddNode(session, root, depth: 0, leafId);
		}
	}

	private void AddNode(CodingAgentSession session, SessionTreeNode node, int depth, string? leafId)
	{
		var isLeaf = node.Entry.Id == leafId;

		var row = new DockPanel()
			.LastChildFill()
			.Margin(new Thickness(depth * 16, 0, 0, 0));

		var summarize = new Button()
			.Content(new TextBlock().Text(Strings.JumpWithSummary).FontSize(AppTheme.FontSizeMicro))
			.OnClick(() => _ = JumpWithSummaryAsync(session, node.Entry.Id));
		row.Children(summarize.DockRight());

		var jump = new Button()
			.Content(new TextBlock().Text(Strings.JumpHere).FontSize(AppTheme.FontSizeMicro))
			.OnClick(() => JumpTo(session, node.Entry.Id));
		row.Children(jump.DockRight());

		var label = new TextBlock()
			.Text($"{(isLeaf ? "●" : "○")} {Describe(node.Entry)}")
			.FontSize(AppTheme.FontSizeCaption)
			.TextWrapping(TextWrapping.NoWrap);
		row.Children(isLeaf ? label.AccentText() : label.MutedText());

		_tree.Children(new Border()
			.StyleName(isLeaf ? AppStyles.SelectedRow : AppStyles.Panel)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(row));

		foreach (var child in node.Children)
		{
			AddNode(session, child, depth + 1, leafId);
		}
	}

	/// <summary>
	/// 带摘要的跳转：先把被放弃的那段路径摘要成一条 <c>branch_summary</c>，再移动叶子。
	/// 摘要要调用模型，因此是异步且有耗时的操作。
	/// </summary>
	private async Task JumpWithSummaryAsync(CodingAgentSession session, string entryId)
	{
		try
		{
			await session.NavigateWithSummaryAsync(entryId).ConfigureAwait(false);
			Refresh();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<BranchPanel>(), "Navigate with branch summary", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.JumpFailed(ex.Message)}");
		}
	}

	private void JumpTo(CodingAgentSession session, string entryId)
	{
		try
		{
			session.NavigateTo(entryId);
			Refresh();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<BranchPanel>(), "Navigate session branch", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.JumpFailed(ex.Message)}");
		}
	}

	/// <summary>每个条目一行摘要，尽量让用户认出这一步做了什么。</summary>
	private static string Describe(SessionEntry entry) => entry switch
	{
		SessionMessageEntry { Message: UserMessage user } => $"{Strings.RoleUser}: {First(user.Content.Text)}",
		SessionMessageEntry { Message: AssistantMessage assistant } =>
			$"{Strings.RoleAssistant}: {First(string.Concat(assistant.Content.OfType<TextContent>().Select(static block => block.Text)))}",
		SessionMessageEntry { Message: ToolResultMessage result } => $"{Strings.RoleTool(result.ToolName)}: {First(string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text)))}",
		SessionMessageEntry { Message: SystemMessage } => Strings.BranchEntrySystem,
		CompactionEntry => Strings.BranchEntryCompaction,
		BranchSummaryEntry => Strings.BranchEntryBranchSummary,
		ModelChangeEntry change => $"{Strings.BranchEntryModelChange} {change.Provider}/{change.ModelId}",
		ThinkingLevelChangeEntry change => $"{Strings.BranchEntryThinkingChange} {change.ThinkingLevel}",
		_ => entry.Type,
	};

	private static string First(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "—";
		}

		var flat = text.Replace('\n', ' ').Trim();
		return flat.Length <= 48 ? flat : string.Concat(flat.AsSpan(0, 48), "…");
	}
}
