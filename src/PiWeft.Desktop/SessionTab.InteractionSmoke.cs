using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	/// <summary>Native interaction diagnostics only; --ui-smoke interactions uses isolated fixtures.</summary>
	internal void PrepareInteractionSmoke()
	{
		View.KeyDown += key =>
		{
			if (key.Key != Key.F8) return;
			key.Handled = true;
			PreviewImage(ChatImages.Source("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII="));
		};
		Expander? run = null;
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50));
		var previous = new Dictionary<string, string>();
		timer.Tick += () =>
		{
			if (View.FindVisualRoot() is not Window window) return;
			run ??= Find<Expander>(_messages).Where(element => HoverHints.Text(element) == Strings.ExecutionExpandHint &&
				element.Bounds.Y >= _scroll.Bounds.Y && element.Bounds.Y < _scroll.Bounds.Bottom).OrderBy(element => element.Bounds.Y).FirstOrDefault();
			foreach (var (name, hint) in new[] { ("run", Strings.ExecutionExpandHint), ("group", Strings.ExecutionGroupHint), ("tool", Strings.ToolExpandHint) })
			{
				var fold = run is null ? null : Find<Expander>(run).FirstOrDefault(element => HoverHints.Text(element) == hint &&
					element.Bounds.Y >= _scroll.Bounds.Y && element.Bounds.Y < _scroll.Bounds.Bottom);
				if (fold is not null) Report(name, new Point(fold.Bounds.X + 10, fold.Bounds.Y + 10), fold.IsExpanded.ToString());
			}
			var diff = Find<MultiLineTextBox>(_messages).LastOrDefault(text => text.Extensions.Classifiers.Any(classifier => classifier is DiffView.DiffClassifier));
			if (diff is not null)
			{
				Report("diff-start", new Point(diff.Bounds.X + 10, diff.Bounds.Y + diff.DefaultLineHeight * 1.4), diff.SelectionLength.ToString());
				Report("diff-end", new Point(diff.Bounds.X + 65, diff.Bounds.Y + diff.DefaultLineHeight * 2.5), diff.SelectionLength.ToString());
			}
			var link = Find<MarkdownPresenter>(_messages).LastOrDefault(text => text.Markdown.StartsWith("[sample]", StringComparison.Ordinal));
			if (link is not null) Report("link", new Point(link.Bounds.X + 25, link.Bounds.Y + 10), "ready");
			Report("preview", new Point(60, window.ClientSize.Height / 2), _imagePreview.IsOpen.ToString());

			void Report(string name, Point client, string state)
			{
				var point = window.ClientToScreen(client);
				var value = $"{point.X:0},{point.Y:0}:{state}";
				if (previous.GetValueOrDefault(name) == value) return;
				previous[name] = value;
				Console.WriteLine($"interaction:{name}:{value}");
			}
		};
		if (View.FindVisualRoot() is Window owner) owner.Closed += timer.Dispose;
		timer.Start();
	}
}
