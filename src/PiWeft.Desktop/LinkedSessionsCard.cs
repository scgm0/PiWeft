using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 输入区上方的「子会话」卡：待决定的提议 + 已开出的关联会话。
/// 提议是 `offer` / `own_initiative` 的唯一入口；子会话行点击打开对应的后台标签。
/// 每次 <see cref="Render"/> 重建行：调用点只在 hub 变化与挂载时触发，行数很少。
internal sealed class LinkedSessionsCard
{
	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();

	public LinkedSessionsCard()
	{
		_rows.Spacing(6);
		_card = new ComposerWidgetCard("git-branch", Strings.LinkedSessionsTitle);
		_card.Body.Children(_rows);
	}

	public Border View => _card.View;

	internal bool IsShown => View.IsVisible;

	internal int RowCount => _rows.Children.Count;

	public void Render(LinkedSessionHub? hub, Action<string> openChild, Action<string> failed)
	{
		if (hub is null)
		{
			_card.Show(false);
			return;
		}

		var proposals = hub.Proposals
			.Where(static proposal => proposal.Status == LinkedSessionProposal.Pending)
			.ToArray();
		var children = hub.Children;
		if (proposals.Length == 0 && children.Count == 0)
		{
			_card.Show(false);
			return;
		}

		_rows.Clear();
		foreach (var proposal in proposals)
		{
			_rows.Children(ProposalRow(hub, proposal, failed));
		}

		foreach (var child in children)
		{
			_rows.Children(ChildRow(child, openChild));
		}

		_card.Meta = Strings.LinkedSessionsMeta(children.Count, proposals.Length);
		_card.Show(true);
	}

	private static UIElement ProposalRow(LinkedSessionHub hub, LinkedSessionProposal proposal, Action<string> failed)
	{
		var heading = new TextBlock()
			.Text(proposal.Initiation == LinkedSessionProposal.Offer ? Strings.LinkedProposalOffer : Strings.LinkedProposalOwn)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText();
		var title = new TextBlock()
			.Text(proposal.Title)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.HoverTip(proposal.Prompt)
			.VerticalAlignment(VerticalAlignment.Center);
		var status = new TextBlock()
			.Text(Strings.LinkedProposalPending)
			.FontSize(AppTheme.FontSizeMicro)
			.Role(AppStyles.RoleWarning)
			.VerticalAlignment(VerticalAlignment.Center);
		var actions = new WrapPanel()
			.Spacing(4)
			.Children(
				AppIcons.TextButton("plus", Strings.LinkedNewSession,
					() => Answer(hub, proposal.Id, LinkedSessionAnswers.New, failed)),
				AppIcons.TextButton("git-branch", Strings.LinkedDoHere,
					() => Answer(hub, proposal.Id, LinkedSessionAnswers.Here, failed)),
				AppIcons.TextButton("x", Strings.LinkedDismiss,
					() => Answer(hub, proposal.Id, LinkedSessionAnswers.Dismiss, failed)));

		return new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2))
			.Child(new StackPanel()
				.Spacing(4)
				.Children(
					heading,
					new Grid()
						.Columns("*,Auto")
						.Children(title.Column(0), status.Column(1)),
					actions));
	}

	private static Button ChildRow(LinkedSessionLink child, Action<string> openChild)
	{
		var icon = AppIcons.Icon(
			"git-branch",
			child.State switch
			{
				LinkedSessionLink.Running => AppStyles.RoleInfo,
				LinkedSessionLink.Idle => AppStyles.RoleSuccess,
				LinkedSessionLink.Failed => AppStyles.RoleError,
				LinkedSessionLink.Starting => AppStyles.RoleWarning,
				_ => AppStyles.RoleSubtle,
			},
			13);
		var title = new TextBlock()
			.Text(child.Title)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var meta = new TextBlock()
			.Text($"{Strings.LinkedState(child.State)} · {Strings.LinkedTurns(child.TurnCount)}")
			.FontSize(AppTheme.FontSizeMicro)
			.Role(child.State == LinkedSessionLink.Failed ? AppStyles.RoleError : AppStyles.RoleSubtle)
			.VerticalAlignment(VerticalAlignment.Center);
		var open = AppIcons.Icon("external-link", AppStyles.RoleFaint, 12)
			.VerticalAlignment(VerticalAlignment.Center);

		return new Button()
			.Ghost()
			.Content(new Grid()
				.Columns("Auto,*,Auto,Auto")
				.Spacing(6)
				.Children(icon.Column(0), title.Column(1), meta.Column(2), open.Column(3)))
			.OnClick(() => openChild(child.ChildSessionId))
			.HoverTip(child.LastReport is { Length: > 0 } report ? report : child.Title)
			.HorizontalAlignment(HorizontalAlignment.Stretch);
	}

	private static void Answer(LinkedSessionHub hub, string proposalId, string answer, Action<string> failed)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				var result = await hub.AnswerProposalAsync(proposalId, answer).ConfigureAwait(false);
				// 重复点击/已结算不算错误，不弹提示。
				if (!result.Ok && result.Text is not ("proposal-settled" or "proposal-busy"))
				{
					failed(result.Text);
				}
			}
			catch (Exception ex)
			{
				failed(ex.Message);
			}
		});
	}
}
