using Aprillz.MewUI;
using Aprillz.MewUI.Automation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 思考强度弹层，版面对齐 PiDeck 的 ThinkingLevelDropdown：
/// 头行是「思考强度 + 预览档位」，下面是渐变强度条，末行档位名兼作点击入口。
/// 选择只在关闭时提交：拖拽与点档位只改草稿，底栏铭牌与落盘都不跟着每半档跳一次。
internal sealed class ThinkingPicker : IDisposable
{
	internal const double CardWidth = 288;

	private const double AnchorGap = 6;
	private const double BodySpacing = 10;

	private readonly FadePopup _popup = new();
	private readonly List<TextBlock> _tickTexts = [];
	private IReadOnlyList<string> _levels = [];
	private Action<string>? _pick;
	private TextBlock? _levelText;
	private Slider? _slider;
	private int _draft;
	private int _applied;

	public ThinkingPicker() => _popup.Closed += OnClosed;

	public bool IsOpen => _popup.IsOpen;

	/// 打开在锚点上方，右缘与锚点对齐（PiDeck 的 side="top" align="end" 同序）。
	/// 没有后端（单测、诊断脚本未 Run）时静默降级，与发送菜单一致。
	public void Show(UIElement anchor, IReadOnlyList<string> levels, string current, Action<string> pick)
	{
		if (!Application.IsRunning)
		{
			return;
		}

		Close();
		if (anchor.FindVisualRoot() is not Window window)
		{
			return;
		}

		_popup.Show(Build(levels, current, pick), anchor, Anchor(anchor), PopupAnchorSide.Above);
		// Popup 自身不搬焦点；滑条要能用方向键，只能这里主动对焦。
		_slider?.Focus();
	}

	public void Close()
	{
		_popup.Close();
	}

	public void Dispose()
	{
		// 标签关闭时已经拖到一半的草稿没有落盘价值；先断开回调，Close 就不再提交。
		_pick = null;
		_popup.Closed -= OnClosed;
		Close();
		_popup.Dispose();
	}

	internal UIElement BuildForTests(IReadOnlyList<string> levels, string current, Action<string>? pick = null)
		=> Build(levels, current, pick);

	internal void CommitForTests() => Commit();

	private UIElement Build(IReadOnlyList<string> levels, string current, Action<string>? pick)
	{
		_levels = levels;
		_pick = pick;
		_tickTexts.Clear();

		var index = IndexOf(levels, current);
		_draft = index;
		_applied = index;

		_levelText = new TextBlock()
			.Text(Strings.ThinkingLevelName(levels[index]))
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.SemiBold)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var header = new Grid().Columns("Auto,Auto,*").Children(
			new TextBlock()
				.Text(Strings.ThinkingLevelTitle)
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.VerticalAlignment(VerticalAlignment.Center)
				.Column(0),
			_levelText.Margin(new Thickness(AppTheme.Space2 - 2, 0, 0, 0)).Column(1));

		var slider = new Slider()
			.Minimum(0)
			.Maximum(Math.Max(0, levels.Count - 1))
			.SmallChange(1)
			.LargeChange(1)
			.Height(22)
			.Padding(default);
		slider.Value = index;
		slider.ValueChanged += value =>
		{
			// Slider 的拖拽是连续的；这里把它按最近档位吸附回去，键鼠才能落在同一组离散值上。
			var snapped = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, levels.Count - 1);
			if (slider.Value != snapped)
			{
				slider.Value = snapped;
				return;
			}

			SetDraft(snapped);
		};
		AutomationProperties.SetName(slider, Strings.ThinkingLevelTitle);
		AutomationProperties.SetHelpText(slider, Strings.ThinkingLevelTitle);
		_slider = slider;
		var intensity = new Grid { AutoIndexing = false }.Children(slider, new ThinkingSliderTrack(slider));

		var ticks = new TickRow();
		for (var tick = 0; tick < levels.Count; tick++)
		{
			var text = new TextBlock()
				.Text(Strings.ThinkingLevelName(levels[tick]))
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center);
			_tickTexts.Add(text);

			var captured = tick;
			var button = new Button()
				.Ghost()
				.Content(text)
				.Padding(new Thickness(1, 0, 1, 0))
				.MinHeight(20)
				.Height(20)
				.OnClick(() => slider.Value = captured);
			ticks.Add(button);
		}

		StyleTick(index, selected: true);

		var card = new Border()
			.StyleName(AppStyles.PopupCard)
			.Width(CardWidth)
			.Padding(new Thickness(AppTheme.Space3))
			.OnKeyDown(OnPopupKeyDown)
			.Child(new StackPanel()
				.Spacing(BodySpacing)
				.Children(header, intensity, ticks));
		// 点标题或空白处时把焦点还给滑条：落在宿主按钮上之后，方向键与 Esc 都到不了弹层。
		// 档位按钮自己会 Focus 并吃掉 MouseDown，这里不会替它抢焦点；随后 Enter/Space 仍是再点一次档位。
		card.MouseDown += down =>
		{
			if (down.Button == MouseButton.Left) slider.Focus();
		};
		return card;
	}

	private static int IndexOf(IReadOnlyList<string> levels, string current)
	{
		for (var index = 0; index < levels.Count; index++)
		{
			if (string.Equals(levels[index], current, StringComparison.Ordinal))
			{
				return index;
			}
		}

		return 0;
	}

	private static Rect Anchor(UIElement anchor)
	{
		var bounds = anchor.Bounds;
		// 锚点矩形与弹层同宽并贴住按钮右缘：Popup 的横向夹取以矩形左缘为起点，
		// 这样弹层右缘停在按钮右缘，不会被推到屏幕外。
		return new Rect(
			bounds.Right - CardWidth,
			bounds.Y - AnchorGap,
			CardWidth,
			bounds.Height + (AnchorGap * 2));
	}

	private void SetDraft(int index)
	{
		if (_levels.Count == 0) return;
		index = Math.Clamp(index, 0, _levels.Count - 1);
		if (index == _draft) return;

		StyleTick(_draft, selected: false);
		_draft = index;
		StyleTick(index, selected: true);
		if (_levelText is { } level)
		{
			level.Text(Strings.ThinkingLevelName(_levels[index]));
		}
	}

	private void StyleTick(int index, bool selected)
	{
		if (index < 0 || index >= _tickTexts.Count) return;
		var text = _tickTexts[index];
		text.FontWeight(selected ? FontWeight.SemiBold : FontWeight.Normal);
		if (selected)
		{
			text.PrimaryText();
		}
		else
		{
			text.MutedText();
		}
	}

	private void OnPopupKeyDown(KeyEventArgs e)
	{
		if (e.Key is Key.Escape or Key.Enter or Key.Space)
		{
			e.Handled = true;
			_popup.Close();
			return;
		}

		// 焦点可能在档位按钮或卡片本身（点标题行），方向键仍应换档；滑条自己处理时不走这里。
		if (_slider is not { } slider) return;
		var next = e.Key switch
		{
			Key.Left or Key.Down => slider.Value - 1,
			Key.Right or Key.Up => slider.Value + 1,
			Key.Home => slider.Minimum,
			Key.End => slider.Maximum,
			_ => double.NaN,
		};
		if (double.IsNaN(next)) return;
		var snapped = Math.Clamp(Math.Round(next, MidpointRounding.AwayFromZero), slider.Minimum, slider.Maximum);
		if (snapped != slider.Value) slider.Value = snapped;
		e.Handled = true;
	}

	private void OnClosed(object? sender, PopupClosedEventArgs e) => Commit();

	private void Commit()
	{
		var pick = _pick;
		if (pick is null) return;

		if (_draft != _applied && _draft >= 0 && _draft < _levels.Count)
		{
			var level = _levels[_draft];
			_applied = _draft;
			pick(level);
		}

		// 一次关闭只提交一次；再收到关闭通知（内容释放、重复 Close）时不再重复落盘。
		_pick = null;
	}

	/// 档位名按强度条上的刻度点定位，而不是平分列宽：5 档以上平分时中间几档
	/// 会离自己的点越来越远（实测「中」偏左 10 px）。两端夹在内容区内，
	/// 和 PiDeck 的 justify-between 一样允许首尾标签不压出卡片。
	private sealed class TickRow : Panel
	{
		private const double TrackInset = ThinkingSliderTrack.TrackInset;

		protected override Size MeasureContent(Size availableSize)
		{
			var width = 0.0;
			var height = 0.0;
			foreach (var child in ChildrenList)
			{
				if (child is not UIElement { IsVisible: true } element) continue;
				element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				width += element.DesiredSize.Width;
				height = Math.Max(height, element.DesiredSize.Height);
			}

			return new Size(double.IsFinite(availableSize.Width) ? Math.Max(availableSize.Width, 0) : width, height);
		}

		protected override void ArrangeContent(Rect bounds)
		{
			var count = ChildrenList.Count;
			if (count == 0) return;

			var span = Math.Max(0, bounds.Width - (TrackInset * 2));
			for (var index = 0; index < count; index++)
			{
				if (ChildrenList[index] is not UIElement child) continue;
				var width = child.DesiredSize.Width;
				var height = child.DesiredSize.Height;
				var center = bounds.X + TrackInset + (count == 1 ? 0 : (span * index / (count - 1)));
				var x = Math.Clamp(center - (width / 2), bounds.X, Math.Max(bounds.X, bounds.Right - width));
				child.Arrange(new Rect(x, bounds.Y + ((bounds.Height - height) / 2), width, height));
			}
		}
	}
}
