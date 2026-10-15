using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	private IReadOnlySet<string> _pinnedSessionIds = new HashSet<string>();

	/// <summary>Virtualized rows keep time visible while titles ellipsize rather than forcing horizontal scrolling.</summary>
	private void ConfigureSessionRows()
	{
		_sessionTree.SmoothScrolling().CornerRadius(AppTheme.RadiusMd);
		_sessionTree.HorizontalScroll = ScrollMode.Disabled;
		_sessionTree.ItemHeight = 36;
		_sessionTree.Indent = 14;
		_sessionTree.ItemPadding = new Thickness(8, 2, 8, 2);
		_sessionTree.ItemTemplate<TreeViewNode>(context =>
		{
			var title = new TextBlock().FontSize(AppTheme.FontSizeControl).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);
			var time = new TextBlock().Width(62).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center);
			context.Register("title", title);
			context.Register("time", time);
			return new DockPanel().LastChildFill().Children(time.DockRight(), title);
		}, (row, node, _, context) =>
		{
			var title = context.Get<TextBlock>("title")!;
			var time = context.Get<TextBlock>("time")!;
			if (node.Tag is SessionListItem item)
			{
				title.FontWeight(FontWeight.Normal).Text((_pinnedSessionIds.Contains(item.Id) ? Strings.PinnedMark + " " : "") + item.Display);
				time.Text(TimeAgo(item.Modified)).IsVisible(true);
				row.HoverTip($"{item.Display}\n{item.Cwd}\n{item.Modified.ToLocalTime():yyyy-MM-dd HH:mm}");
			}
			else
			{
				title.Text(node.Text).FontWeight(FontWeight.Normal);
				time.IsVisible(false);
				row.HoverTip(node.Text);
			}
		});
	}
}
