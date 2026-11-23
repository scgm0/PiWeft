using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 输入区上方的「团队任务」条：共享任务板的前几项，状态、owner 与等待原因。
/// 只在 Team 模式打开且有任务时占位；任务变更不唤醒任何子代理。
internal sealed class TeamTasksCard
{
	private const int MaxRows = 5;
	private const int DescriptionPreviewChars = 600;

	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();
	private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
	private TeamBoard? _board;
	private SubagentHub? _hub;
	private Action<TeamTask>? _openOwner;
	private bool _showAll;
	private string? _ownerFilter;

	public TeamTasksCard()
	{
		_rows.Spacing(4);
		_card = new ComposerWidgetCard("list-checks", Strings.TeamTasksTitle);
		_card.Body.Children(_rows);
	}

	public Border View => _card.View;

	internal bool IsShown => View.IsVisible;

	internal string Meta => _card.Meta;

	internal void ShowHeader(bool visible) => _card.ShowHeader(visible);

	internal void Expand() => _card.IsExpanded = true;

	internal void Render(
		TeamBoard? board,
		Action<TeamTask>? openOwner = null,
		SubagentHub? hub = null)
	{
		_board = board;
		_openOwner = openOwner;
		_hub = hub;
		if (board is null)
		{
			_card.Show(false);
			return;
		}

		var tasks = board.Tasks
			.Where(static task => task.Status != TeamTaskStatus.Deleted)
			.Where(task => _ownerFilter is not { Length: > 0 } owner ||
				string.Equals(task.Owner, owner, StringComparison.Ordinal))
			.OrderBy(static task => task.Status switch
			{
				TeamTaskStatus.InProgress => 0,
				TeamTaskStatus.Pending => 1,
				_ => 2,
			})
			.ThenBy(static task => task.Id, StringComparer.Ordinal)
			.ToArray();
		if (tasks.Length == 0)
		{
			if (_ownerFilter is { Length: > 0 } missing)
			{
				_rows.Clear();
				_rows.Children(new TextBlock()
					.Text(Strings.TeamTasksOwnerEmpty(missing))
					.FontSize(AppTheme.FontSizeMicro)
					.SubtleText()
					.TextWrapping(TextWrapping.Wrap));
				_rows.Children(AppIcons.TextButton("x", Strings.TeamTasksClearFilter, ClearFilter));
				_card.Meta = Strings.TeamTasksFilterMeta(missing, 0);
				_card.Show(true);
				return;
			}

			_card.Show(false);
			return;
		}

		// 任务被删掉后，展开状态跟着清；否则同名 id 复用时行会莫名其妙是打开的。
		if (_expanded.Count > 0)
		{
			var alive = tasks.Select(static task => task.Id).ToHashSet(StringComparer.Ordinal);
			_expanded.RemoveWhere(id => !alive.Contains(id));
		}

		_rows.Clear();
		if (_ownerFilter is { Length: > 0 } filteredOwner)
		{
			_rows.Children(AppIcons.TextButton("x", Strings.TeamTasksClearFilter, ClearFilter));
		}

		var runs = _hub?.Runs;
		foreach (var task in tasks.Take(_showAll ? tasks.Length : MaxRows))
		{
			_rows.Children(Row(task, runs));
		}

		if (tasks.Length > MaxRows)
		{
			_rows.Children(AppIcons.TextButton(
				"ellipsis",
				_showAll ? Strings.TeamTasksShowLess : Strings.TeamTasksShowAll(tasks.Length - MaxRows),
				() =>
				{
					_showAll = !_showAll;
					Render(_board, _openOwner, _hub);
				}));
		}

		var active = tasks.Count(static task => task.Status == TeamTaskStatus.InProgress);
		var pending = tasks.Count(static task => task.Status == TeamTaskStatus.Pending);
		_card.Meta = _ownerFilter is { Length: > 0 } ownerFilter
			? Strings.TeamTasksFilterMeta(ownerFilter, tasks.Length)
			: Strings.TeamTasksMeta(active, pending, tasks.Length);
		_card.Show(true);
	}

	/// 测试用：展开或收起某项。
	internal void ToggleForTests(string id) => Toggle(id);

	private void ClearFilter()
	{
		_ownerFilter = null;
		Render(_board, _openOwner, _hub);
	}

	/// 测试用：只看某个 owner 的任务；传 null 清除。
	internal void FilterOwnerForTests(string? owner)
	{
		_ownerFilter = owner;
		Render(_board, _openOwner, _hub);
	}

	private void Toggle(string id)
	{
		if (!_expanded.Add(id))
		{
			_expanded.Remove(id);
		}

		Render(_board, _openOwner, _hub);
	}

	private UIElement Row(TeamTask task, IReadOnlyList<SubagentRun>? runs)
	{
		var expanded = _expanded.Contains(task.Id);
		var icon = task.Status switch
		{
			TeamTaskStatus.InProgress => "loader-circle",
			TeamTaskStatus.Completed => "circle-check",
			_ => "circle",
		};
		var role = task.Status switch
		{
			TeamTaskStatus.InProgress => AppStyles.RoleInfo,
			TeamTaskStatus.Completed => AppStyles.RoleSuccess,
			_ => AppStyles.RoleSubtle,
		};
		var owner = task.Owner is { Length: > 0 } name ? name : Strings.TeamTaskUnowned;
		var run = task.Owner is { Length: > 0 } ownerName && runs is not null
			? runs.FirstOrDefault(candidate => string.Equals(candidate.Name, ownerName, StringComparison.Ordinal))
			: null;
		var ownerEnded = task.Status == TeamTaskStatus.InProgress && run is { IsTerminal: true };
		var metaText = ownerEnded
			? $"{Strings.TeamTaskStatusText(task.Status)} · {owner} · {Strings.TeamTaskOwnerEnded}"
			: $"{Strings.TeamTaskStatusText(task.Status)} · {owner}";
		var meta = new TextBlock()
			.Text(metaText)
			.FontSize(AppTheme.FontSizeMicro)
			.Role(ownerEnded ? AppStyles.RoleWarning : role)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var subject = new TextBlock()
			.Text(task.Subject)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var chevron = new GlyphElement()
			.Kind(expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);

		var header = new Button()
			.Ghost()
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space1, AppTheme.Space1))
			.Content(new Grid()
				.Columns("Auto,*,Auto,Auto")
				.Spacing(6)
				.Children(
					AppIcons.Icon(icon, role, 12).Column(0),
					subject.Column(1),
					meta.Column(2),
					chevron.Column(3)))
			.OnClick(() => Toggle(task.Id))
			.HoverTip(DetailText(task))
			.HorizontalAlignment(HorizontalAlignment.Stretch);

		var stack = new StackPanel().Spacing(2).Children(header);
		if (expanded)
		{
			stack.Children(Details(task));
		}

		return stack;
	}

	private UIElement Details(TeamTask task)
	{
		var preview = Clip(task.Description, DescriptionPreviewChars);
		var body = new StringBuilder(256);
		if (preview.Length > 0)
		{
			body.Append(preview).Append("\n\n");
		}

		body.Append(Strings.TeamTaskRevision(task.Revision));
		if (task.BlockedBy.Count > 0)
		{
			body.Append('\n').Append(Strings.TeamTaskBlockedBy(string.Join(", ", task.BlockedBy)));
		}

		if (task.WriteScopes.Count > 0)
		{
			body.Append('\n').Append(Strings.TeamTaskWriteScopes(string.Join(", ", task.WriteScopes)));
		}

		var stack = new StackPanel()
			.Spacing(4)
			.Margin(new Thickness(20, 0, AppTheme.Space1, AppTheme.Space1))
			.Children(new TextBlock()
				.Text(body.ToString())
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap));
		stack.Children(AppIcons.TextButton("copy", Strings.TeamTaskCopy, () => Copy(FullText(task))));
		if (task.Owner is { Length: > 0 } owner &&
			!string.Equals(owner, "lead", StringComparison.Ordinal))
		{
			if (_openOwner is { } openOwner)
			{
				stack.Children(AppIcons.TextButton(
					"external-link",
					Strings.TeamTaskOpenOwner(owner),
					() => openOwner(task)));
			}

			stack.Children(AppIcons.TextButton(
				"list-ordered",
				Strings.TeamTasksFilterOwner(owner),
				() =>
				{
					_ownerFilter = owner;
					Render(_board, _openOwner, _hub);
				}));
		}

		return stack;
	}

	/// 复制完整详情，不截断；正文过长时用户至少能带走原文。
	private static string FullText(TeamTask task)
	{
		var body = new StringBuilder(256);
		if (task.Description.Length > 0)
		{
			body.Append(task.Description).Append("\n\n");
		}

		body.Append(Strings.TeamTaskRevision(task.Revision));
		if (task.BlockedBy.Count > 0)
		{
			body.Append('\n').Append(Strings.TeamTaskBlockedBy(string.Join(", ", task.BlockedBy)));
		}

		if (task.WriteScopes.Count > 0)
		{
			body.Append('\n').Append(Strings.TeamTaskWriteScopes(string.Join(", ", task.WriteScopes)));
		}

		return body.ToString();
	}

	private void Copy(string text)
	{
		var clipboard = Application.IsRunning && Application.Current is { } app
			? app.PlatformServices.Clipboard
			: null;
		Toasts.Show(View, clipboard?.TrySetText(text) == true ? Strings.Copied : Strings.ClipboardUnavailable);
	}

	private static string DetailText(TeamTask task)
	{
		var body = new StringBuilder(128);
		if (task.Description.Length > 0)
		{
			body.Append(Clip(task.Description, 300)).Append("\n");
		}

		if (task.BlockedBy.Count > 0)
		{
			body.Append(Strings.TeamTaskBlockedBy(string.Join(", ", task.BlockedBy))).Append('\n');
		}

		if (task.WriteScopes.Count > 0)
		{
			body.Append(Strings.TeamTaskWriteScopes(string.Join(", ", task.WriteScopes)));
		}

		return body.ToString().TrimEnd();
	}

	private static string Clip(string text, int max) =>
		text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
