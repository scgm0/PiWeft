using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	private int _loadingAttachments;

	/// <summary>Acceptance and effect are independent in MewUI; setting only Copy rejects native drops.</summary>
	private void OnFilesDragOver(DragEventArgs e)
	{
		e.Accepted = !_disposed && ComposerDrop.CanAccept(e.Data) && (e.AllowedEffects & DragDropEffects.Copy) != 0;
		e.Effect = e.Accepted ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	/// <summary>Snapshot the native payload synchronously, then load images off the UI thread.</summary>
	private void OnFilesDropped(DragEventArgs e)
	{
		OnFilesDragOver(e);
		if (!e.Accepted) return;
		// A format being advertised doesn't guarantee it contains a usable file or image.
		e.Accepted = false;
		e.Effect = DragDropEffects.None;
		var paths = ComposerDrop.Paths(e.Data, Session.Cwd);
		if (paths.Count > 0)
		{
			e.Accepted = true;
			e.Effect = DragDropEffects.Copy;
			_ = AddFilesAsync(paths);
			FocusComposer();
			return;
		}
		try
		{
			if (ComposerDrop.RawImage(e.Data) is not { } image) return;
			e.Accepted = true;
			e.Effect = DragDropEffects.Copy;
			AddImage(image.Path, image.Content);
			FocusComposer();
		}
		catch (IOException) { NotifyError(Strings.AttachmentLimit); }
	}


	/// <summary>Paths stay in the draft; images are read off the UI thread into this tab only.</summary>
	internal async Task AddFilesAsync(IReadOnlyList<string> paths)
	{
		if (_disposed) return;
		var references = paths.Where(path => Directory.Exists(path) || !ComposerDrop.IsImage(path))
			.Select(ComposerDrop.Reference).ToArray();
		if (references.Length > 0)
		{
			var start = Math.Clamp(_composer.SelectionStart, 0, _composer.Text.Length);
			var length = Math.Clamp(_composer.SelectionLength, 0, _composer.Text.Length - start);
			var insertion = " " + string.Join(" ", references) + " ";
			_composer.Text = _composer.Text.Remove(start, length).Insert(start, insertion);
			_composer.Select(start + insertion.Length, 0);
		}
		_loadingAttachments++;
		try
		{
			foreach (var path in paths.Where(path => File.Exists(path) && ComposerDrop.IsImage(path)))
			{
				try
				{
					var content = await Task.Run(() => ComposerDrop.ReadImageAsync(path)).ConfigureAwait(false);
					Dispatch(() => AddImage(path, content));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
				{
					Dispatch(() => { if (!_disposed) NotifyError(Strings.AttachmentReadFailed); });
				}
			}
		}
		finally { Dispatch(() => _loadingAttachments--); }
	}

	private void AddImage(string path, ImageContent content)
	{
		if (_disposed || _attachments.Any(item => item.Path == path)) return;
		var total = _attachments.Sum(item => item.Content.Data.Length * 3L / 4);
		if (_attachments.Count >= ComposerDrop.MaxImages || total + content.Data.Length * 3L / 4 > ComposerDrop.MaxTotalBytes)
		{
			NotifyError(Strings.AttachmentLimit);
			return;
		}
		_attachments.Add((path, content));
		RefreshAttachments();
	}
}
