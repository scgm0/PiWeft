using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

/// <summary>助手消息的持久视图：思考独立折叠，正文与思考在流式期间增量渲染 Markdown，定稿只切状态。</summary>
internal sealed class AssistantMessageView : IDisposable
{
	private readonly TextBlock _header = new();
	private readonly DockPanel _headerRow;
	private readonly TextBlock _thinkingHeader = new();
	private readonly TextBlock _thinkingPreview = new();
	// 跑马灯容器：流式期把最新一行的行尾贴到右边缘，定稿后回到行首 + 省略号。
	private readonly MarqueeLine _previewLine;
	// 逐字推进：思考与正文各一把打字机，共用一条帧泵。网络一次推一整块，画面每帧只多露几个字，
	// 跑马灯与流式正文才是「流」而不是一跳一跳。
	private readonly Typewriter _thinkingType = new();
	private readonly Typewriter _bodyType = new();
	private readonly StreamPump _pump;
	private readonly Image _thinkingIcon;
	// 思考与正文各持有一个增量 Markdown 视图：流式期间只更新尾块，定稿只切状态。
	private MarkdownView? _thinkingMarkdown;
	private MarkdownView? _bodyMarkdown;
	// 每个视图记住「已经写到可见前缀的第几个字」：下一帧只追加新露出的那几个字，不整串拷贝。
	private readonly MarkdownFeed _thinkingFeed = new();
	private readonly MarkdownFeed _bodyFeed = new();
	private readonly Expander _thinking;
	private readonly Border _body = new();
	private readonly TextBlock _changes = new();
	private readonly Button _copy;
	private readonly TextFocusScrollGuard _focus;
	private readonly Action<string> _openLink;
	private readonly Action<ImageSource>? _previewImage;
	private ChatEntry? _entry;
	// 展开中的思考是否正在跟随内容增长：只在流式思考期间保持，避免结束后把阅读位置拉走。
	private bool _followingThinking;

	public AssistantMessageView(ChatEntry entry, Action<string> copy, TextFocusScrollGuard focus, Action<ImageSource>? preview = null,
		Action<string>? openLink = null)
	{
		_focus = focus;
		_openLink = openLink ?? (url => Shell.OpenMessageLink(url, Environment.CurrentDirectory));
		_previewImage = preview;
		_header.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).SubtleText();
		_thinkingHeader.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium)
			.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Thinking);
		_thinkingPreview.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);
		_previewLine = new MarqueeLine(_thinkingPreview).VerticalAlignment(VerticalAlignment.Center);

		_thinkingIcon = AppIcons.Icon("sparkles", AppStyles.RoleThinking, 12);
		// Expander 自带折叠箭头，不在标题内再画一个。
		var thinkingTitle = new Grid().Columns("Auto,Auto,*").HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				_thinkingIcon.Column(0),
				_thinkingHeader.Column(1).Margin(new Thickness(5, 0, 0, 0)),
				_previewLine.Column(2).Margin(new Thickness(6, 0, 0, 0)));
		_thinking = new Expander()
			.Header(thinkingTitle)
			.IsExpanded(false);
		_thinking.ExpandedChanged += expanded =>
		{
			_previewLine.IsVisible = !expanded && _previewText.Length > 0;
			if (!expanded)
			{
				_followingThinking = false;
				_focus.StopGrowth(_thinking);
				return;
			}
			EnsureThinkingBody();
			// 用户展开：记住这次阅读意图。若思考正在流式输出就立即贴住末尾；
			// 尚未开始时由后续 Update 在首个 thinking delta 到来后接管。
			// 从回收缓存恢复状态时不算一次用户展开，不重新贴底。
			if (!_focus.SuppressGrowthFollow)
			{
				_followingThinking = true;
				if (_entry?.IsThinking == true) _focus.FollowGrowth(_thinking);
			}
		};

		// 思考行内部由组体/跟随增长处理；不要用外层锚点把会话滚动位置带走。
		_focus.TrackFold(_thinking, preserveAnchor: false);
		_copy = AppIcons.Button("copy", Strings.CopyMessage, () => copy(_entry?.Text ?? string.Empty), size: 14);
		_headerRow = new DockPanel().LastChildFill().Children(_copy.DockRight(), _header);
		var content = new StackPanel().Spacing(AppTheme.Space2).Children(
			_headerRow,
			_thinking,
			_body,
			_changes.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap).IsVisible(false));

		foreach (var image in entry.Images)
		{
			content.Children(SessionTab.ImageView(image, preview));
		}

		View = new Border()
			.Padding(entry.Compact ? new Thickness(0, 0, 0, 0) : new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.MaxWidth(AppTheme.MessageMaxWidth)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Child(content);
		View.Tag = this;
		// 帧泵挂在本行的根元素上：它按可见性自己止损，宿主不需要记得为「收起/滚出视口」停帧。
		_pump = new StreamPump(View, PumpFrames);
		Update(entry);
	}

	public Border View { get; }

	/// <summary>一个 Markdown 视图的喂入状态：上次喂给它的权威文本 + 已经写进去的可见长度。</summary>
	private sealed class MarkdownFeed
	{
		internal MarkdownView? View;
		internal string? Source;
		internal int Shown;

		internal void Clear()
		{
			View = null;
			Source = null;
			Shown = 0;
		}
	}

	public void Dispose()
	{
		_pump.Dispose();
		if (_bodyMarkdown is not null)
		{
			UiLifetime.Release(_bodyMarkdown);
			_bodyMarkdown = null;
		}
		if (_thinkingMarkdown is not null)
		{
			UiLifetime.Release(_thinkingMarkdown);
			_thinkingMarkdown = null;
		}
		_bodyFeed.Clear();
		_thinkingFeed.Clear();
	}

	public void Update(ChatEntry entry)
	{
		// 头部文本的输入只有这三项：流式期每帧都重新格式化并转换时区，是纯粹的字符串垃圾。
		var headerKey = (entry.Timestamp, entry.IsStreaming, entry.DurationMs);
		if (headerKey != _headerKey)
		{
			_headerKey = headerKey;
			var time = entry.Timestamp > 0
				? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm")
				: string.Empty;
			var role = entry.IsStreaming ? Strings.RoleAssistantStreaming : Strings.RoleAssistant;
			var timing = !entry.IsStreaming && entry.DurationMs is { } duration
				? $"  ·  {Strings.ResponseDuration(duration)}"
				: string.Empty;
			_header.Text(time.Length > 0 ? $"{role}  ·  {time}{timing}" : $"{role}{timing}");
		}
		_headerRow.IsVisible(!entry.Compact);
		_thinking.IsVisible(entry.Thinking.Length > 0 || entry.IsThinking);
		_thinkingIcon.IsVisible(!entry.Compact);
		var thinkingLabel = entry.IsThinking ? Strings.ThinkingActive
			: entry.Compact && entry.DurationMs is { } thinkingMs ? Strings.ThinkingDuration(thinkingMs) : Strings.ThinkingComplete;
		if (!ReferenceEquals(thinkingLabel, _thinkingLabel))
		{
			_thinkingLabel = thinkingLabel;
			_thinkingHeader.Text(thinkingLabel);
		}

		// 打字机与预览缓存都属于「这一条消息」：行视图会被回收给另一条，身份一变就整体重来。
		// 比 id + 时间戳而不是比引用：同一条消息的每个流式帧都是新的 record。
		var identity = (entry.EntryId, entry.Timestamp);
		if (identity != _streamIdentity)
		{
			_streamIdentity = identity;
			_previewSettled = false;
			_thinkingType.Reset();
			_bodyType.Reset();
		}
		// 逐字只对「正在流的这一段」生效：这一行已经定稿、或是打开的旧会话，就直接全量显示 ——
		// 把几百 KB 的历史思考从头打一遍是灾难。
		_thinkingType.Set(entry.Thinking, PacedStream && entry.IsThinking);
		_bodyType.Set(entry.Text, PacedStream && entry.IsStreaming);
		// 数据帧自己也推一格：没有帧泵（自检/单测）时这就是唯一的推进通道。
		_thinkingType.Advance();
		_bodyType.Advance();

		RefreshPreview(entry);
		_previewLine.IsVisible(!_thinking.IsExpanded && _previewText.Length > 0);
		if (_thinking.IsExpanded)
		{
			EnsureThinkingBody(entry);
			// 已经由用户展开并进入跟随的思考继续贴住末尾；恢复出来的展开状态不自动开始跟随。
			if (entry.IsThinking)
			{
				if (_followingThinking) _focus.FollowGrowth(_thinking);
			}
			else if (_followingThinking)
			{
				// 思考结束只停止“思考自身的跟底”，不要把已置底的组体自动跟随也关掉：
				// 组体滚动条仍在底部时，后续工具步骤应继续贴底；用户上滚时 SmoothScroll
				// 自己会退出跟底。
				_followingThinking = false;
			}
		}
		else
		{
			_followingThinking = false;
		}
		var showChanges = !entry.IsStreaming && !entry.Compact && entry.ChangedFiles.Count > 0;
		_changes.IsVisible(showChanges);
		if (showChanges)
		{
			_changes.Text(Strings.ChangedFilesSummary(entry.ChangedFiles));
		}
		RefreshBodyText(entry);
		SyncPump();

		_entry = entry;
	}

	/// <summary>
	/// 折叠预览：流式中跟着「已经露出的最新一行」跑（尾随 = 跑马灯），定稿后回到首行 + 省略号。
	/// 展开时不算：正文已经整块在屏幕上，再算一遍单行是白烧。
	/// </summary>
	private void RefreshPreview(ChatEntry entry)
	{
		if (_thinking.IsExpanded) return;
		if (entry.IsThinking)
		{
			// 思考还在写：首行随时会被新的一行顶掉，每帧跟着最新一行走。
			_previewSettled = false;
			SetPreview(ThinkingPreview.LatestLine(entry.Thinking, _thinkingType.Shown), followTail: true);
		}
		else if (!_previewSettled)
		{
			var preview = ThinkingPreview.FirstLine(entry.Thinking, out var settled);
			_previewSettled = settled;
			SetPreview(preview, followTail: false);
		}
	}

	/// <summary>
	/// 回答正文：打字机露出多少就喂多少，视图里已有的部分只追加新露出的那几个字。
	/// 只有思考、还没有可见正文时不预建空的 Markdown 视图（过程组里每行都建一个是白烧布局）。
	/// </summary>
	private void RefreshBodyText(ChatEntry entry)
	{
		var shown = _bodyType.Shown;
		_body.IsVisible(shown > 0);
		_copy.IsVisible(!entry.Compact && !entry.IsStreaming && entry.Text.Length > 0);
		if (shown > 0)
		{
			var view = EnsureBodyMarkdown();
			if (!ReferenceEquals(_body.Child, view))
			{
				var previous = _body.Child;
				_body.Child(view);
				if (previous is not null) UiLifetime.Release(previous);
			}
			view.IsStreaming = entry.IsStreaming;
			FeedMarkdown(view, _bodyFeed, entry.Text, shown, entry.IsStreaming, "markdown.stream", "markdown.finalize");
			return;
		}
		var detached = _body.Child;
		if (detached is null) return;
		_body.Child(null);
		UiLifetime.Release(detached);
		if (ReferenceEquals(detached, _bodyMarkdown)) _bodyMarkdown = null;
		_bodyFeed.Clear();
	}

	/// <summary>
	/// 逐字推进能不能摊平：只有挂在窗口上的行才有渲染脉冲可跟。没窗口（自检/无头布局测试）
	/// 或这一行还没接进树（时间线先建视图再挂父级）时直接全露出，既省掉一次假动画，
	/// 也让无窗口路径的结果与真实首屏一致：已经写好的内容不重打一遍。
	/// </summary>
	private bool PacedStream => PaceStreamForTest || (Application.IsRunning && View.FindVisualRoot() is Window);

	/// <summary>窗口less 测试用：强制走有帧循环的推进路径，验证正文确实一个字符一个字符在走。</summary>
	internal bool PaceStreamForTest { get; set; }

	/// <summary>窗口less 测试用：手动推一帧（真窗口里由 StreamPump 的每帧回调驱动）。</summary>
	internal void PumpStreamFrameForTest() => PumpFrames();

	/// <summary>
	/// 帧泵的每一拍：推进两把打字机，只把新露出的那几个字交给对应消费端。
	/// 消费端都有「可见前缀没变就不写」的短路，所以没打到字的那些帧不会带来任何布局工作。
	/// </summary>
	private void PumpFrames()
	{
		var entry = _entry;
		if (entry is null)
		{
			_pump.Stop();
			return;
		}
		if (_thinkingType.Advance())
		{
			// 与 Update 走同一对消费端：展开着就长正文，收起着就长跑马灯预览。
			if (_thinking.IsExpanded) EnsureThinkingBody(entry);
			else RefreshPreview(entry);
		}
		if (_bodyType.Advance()) RefreshBodyText(entry);
		SyncPump();
	}

	/// <summary>
	/// 还有没打出的字就继续吃帧，打完立刻停泵：持续渲染会整窗心跳，
	/// 下一块 delta 到达时 Update 会再把泵开起来。
	/// </summary>
	private void SyncPump()
	{
		if (_thinkingType.CaughtUp && _bodyType.CaughtUp) _pump.Stop();
		else _pump.Start();
	}

	/// <summary>
	/// 写预览行：文本没变就不碰 TextBlock（Text 是 AffectsLayout，赋值会把整行重新测量），
	/// 但排布模式每帧都要对一下 —— 流式结束那一帧正是从尾随切回首行的时机。
	/// </summary>
	private void SetPreview(string text, bool followTail)
	{
		if (!string.Equals(_previewText, text, StringComparison.Ordinal))
		{
			_previewText = text;
			_thinkingPreview.Text = text;
		}
		_previewLine.FollowTail = followTail;
	}

	private (long Timestamp, bool Streaming, double? Duration) _headerKey;
	private string? _thinkingLabel;
	private string _previewText = string.Empty;
	private bool _previewSettled;
	// 流式推进与预览缓存属于哪一条消息：行视图会被回收，身份一变就要重来。
	private (string? Id, long Timestamp) _streamIdentity;

	/// <summary>
	/// 展开的思考：同一个增量 Markdown 视图从流式第一帧用到定稿，不再做纯文本→Markdown 的替换。
	/// 视图里只写打字机已经露出的那段，所以「展开着看流式」也是逐字在长。
	/// </summary>
	private void EnsureThinkingBody(ChatEntry? entry = null)
	{
		var current = entry ?? _entry;
		if (current is null) return;
		var thinking = current.Thinking;
		// 打字机没跟着这条文本走（视图刚建、或行被回收给别的消息）时按全文显示，不能显示半句。
		var shown = ReferenceEquals(_thinkingType.Target, thinking) ? Math.Min(_thinkingType.Shown, thinking.Length) : thinking.Length;
		if (shown == 0)
		{
			if (_thinkingMarkdown is not null) _thinkingMarkdown.Source = string.Empty;
			_thinkingFeed.Clear();
			return;
		}
		_thinkingMarkdown ??= CreateMarkdownView(AppTheme.FontSizeCaption, muted: true);
		SetThinkingContent(_thinkingMarkdown);
		_thinkingMarkdown.IsStreaming = current.IsThinking;
		FeedMarkdown(
			_thinkingMarkdown, _thinkingFeed, thinking, shown, current.IsThinking,
			"markdown.thinking.stream", "markdown.thinking.finalize");
	}

	/// <summary>
	/// 把打字机的可见前缀喂给 Markdown 视图：视图里已经有 text[..feed.Shown]、且新文本仍是上一次的继续时，
	/// 只 Append 新露出的那几个字 —— 增量小，解析仍由控件按尾块增量完成，宿主不整串比较也不整串拷贝。
	/// 文本被改写（编辑重发、换行绑定、收缩）、视图换了或已经定稿时退回整篇赋值；
	/// 可见前缀一个字都没多（只是尾部又长了一点）时什么都不做。
	/// </summary>
	private void FeedMarkdown(
		MarkdownView view,
		MarkdownFeed feed,
		string text,
		int shown,
		bool streaming,
		string streamEvent,
		string finalizeEvent)
	{
		shown = Math.Min(shown, text.Length);
		var previous = feed.Source;
		// 可见前缀仍然成立 = 权威文本还是上一次的继续。同一份字符串实例（帧泵在两个数据帧之间推进）
		// 直接跳过比较：64K 正文每帧做一次整串 StartsWith 是白烧。
		var continues = ReferenceEquals(view, feed.View) && previous is not null &&
			(ReferenceEquals(previous, text) || text.StartsWith(previous, StringComparison.Ordinal));
		if (continues)
		{
			if (shown == feed.Shown)
			{
				// 可见部分一个字都没多：这一帧没有要写的东西（定稿后的重复状态帧同理）。
				feed.Source = text;
				view.IsStreaming = streaming;
				return;
			}
			if (streaming && shown > feed.Shown)
			{
				var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
				view.Append(text.Substring(feed.Shown, shown - feed.Shown));
				if (watch is not null)
				{
					Diagnostics.Record(streamEvent, watch, $"delta={shown - feed.Shown} chars={shown}");
				}
				feed.Source = text;
				feed.Shown = shown;
				return;
			}
		}
		var full = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		view.Source = shown >= text.Length ? text : text.Substring(0, shown);
		if (full is not null)
		{
			Diagnostics.Record(streaming ? streamEvent : finalizeEvent, full, $"chars={shown}");
		}
		feed.View = view;
		feed.Source = text;
		feed.Shown = shown;
	}

	private void SetThinkingContent(UIElement content)
	{
		if (_thinking.Content is Border { Child: var current } && ReferenceEquals(current, content)) return;
		_thinking.Content(new Border()
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, AppTheme.Space2))
			.Child(content));
	}

	internal static MultiLineTextBox CreateCodeText(string code, string? language, TextFocusScrollGuard focus)
	{
		var text = MessageText.Create(code, mono: true, wrap: false, fontSize: AppTheme.FontSizeCaption);
		ConfigureCodeText(text, language, focus, muted: false);
		return text;
	}

	private static void ConfigureCodeText(MultiLineTextBox text, string? language, TextFocusScrollGuard focus, bool muted)
	{
		focus.Track(text);
		// 代码块的横向视口在 Markdown 建块时才出现（MarkdownView 建树早于它）：
		// 这里补装同一个平滑滚动控制器，触控板横向滑动才不会落进 ScrollViewer 的即时跳变。
		for (var parent = text.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent is not ScrollViewer viewport) continue;
			viewport.SmoothScrolling();
			break;
		}
		text.Tag = muted ? AppStyles.RoleMuted : AppStyles.RolePrimary;
		text.Extensions.Classifiers.Add(new KeywordClassifier(language));
		// 换肤后重建视图并重新排布，避免代码块只剩背景（见 AppStyles.RefreshTextView）。
		text.WithTheme((theme, view) => AppStyles.RefreshTextView(view));
	}

	private MarkdownView EnsureBodyMarkdown() => _bodyMarkdown ??= CreateMarkdownView(AppTheme.FontSizeChat);

	/// <summary>
	/// 全部消息视图共享同一份 Options 与 Theme 实例：两者都进高度缓存的 MeasureKey，而 record
	/// 对 HashSet/委托字段按实例比较——每视图新建一份会让跨实例缓存永不命中。
	/// </summary>
	internal static readonly MarkdownOptions SharedMarkdownOptions = new()
	{
		// 与应用层「链接只放行 http/https」一致，下沉为控件库的第二道闸门（图片同样受限）。
		LinkSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" },
	};

	internal static readonly MarkdownTheme SharedMarkdownTheme = new()
	{
		CodeFontFamily = AppTheme.MonoFont,
		CodeFontSize = AppTheme.FontSizeCaption,
		CodeCopyLabel = Strings.TerminalCopy,
		CodeCopyTooltip = Strings.CopyCodeTooltip,
		// 图标一律走 Lucide 内嵌 SVG（tools/fetch-lucide-icons.sh 取的那一套），
		// 控件库自己不画图标；复制成功换成对勾，1.1 秒后退回复制图标。
		CodeCopyIcon = size => AppIcons.Icon("copy", AppStyles.RoleSubtle, size),
		CodeCopiedIcon = size => AppIcons.Icon("check", AppStyles.RoleSuccess, size),
		// 悬浮提示统一走应用的延迟悬浮层（350 ms），与其它提示同一套延迟与样式；
		// 库里缺省的内建 ToolTip 是悬停立即弹出。
		AttachToolTip = (element, text) => element.HoverTip(text),
		BlockSpacing = AppTheme.Space2,
		ListIndent = 28,
		CardCornerRadius = AppTheme.RadiusXl,
		CodeBackgroundSelector = theme => AppTheme.For(theme).CodeBackground,
		InlineCodeBackgroundSelector = theme => AppTheme.For(theme).CardBackgroundAlt,
		InlineCodeBorderSelector = theme => AppTheme.For(theme).Border,
		// GFM alerts：NOTE/TIP/IMPORTANT/WARNING/CAUTION 的语义色与中文标签。
		AlertColorSelector = (theme, kind) => kind switch
		{
			"NOTE" => AppTheme.Info,
			"TIP" => AppTheme.Success,
			"IMPORTANT" => AppTheme.Thinking,
			"WARNING" => AppTheme.Error.Lerp(AppTheme.Accent, 0.45),
			"CAUTION" => AppTheme.Error,
			_ => null,
		},
		AlertLabel = kind => kind switch
		{
			"NOTE" => Strings.AlertNote,
			"TIP" => Strings.AlertTip,
			"IMPORTANT" => Strings.AlertImportant,
			"WARNING" => Strings.AlertWarning,
			"CAUTION" => Strings.AlertCaution,
			_ => null,
		},
	};

	private MarkdownView CreateMarkdownView(double fontSize, bool muted = false)
	{
		// 会话本身已经有纵向视口，每条回复只负责排版；块回收由外层视口驱动。
		var view = new MarkdownView
		{
			FontSize = fontSize,
			FontWeight = FontWeight.Normal,
			Options = SharedMarkdownOptions,
			MarkdownTheme = SharedMarkdownTheme,
		};
		// Markdown 图片：data/本地文件/https 走共享 loader（有上限与失败缓存），
		// 点击交给与附件同一套全尺寸预览。
		view.ImageResolver = MarkdownImageLoader.Shared;
		view.ImageRequested += args =>
		{
			if (args.Image is { } source) _previewImage?.Invoke(source);
		};
		// 丢渲染一旦发生就要在 timings 里留下现场（窗口/偏移/高度），否则只能靠猜：
		// 正常情况下这条分支永远不走，成本为零。
		view.NothingRealized += geometry =>
		{
			Diagnostics.Count("markdown.empty.realize");
			Diagnostics.Record("markdown.empty.realize", 0, geometry);
		};
		view.SelectionChanged += () => PrimarySelectionText.Service?.Publish(
			view.SelectedText.Length <= LinuxPrimarySelection.MaxCharacters ? view.SelectedText : string.Empty);
		_focus.Track(view);
		view.LinkRequested += link => _openLink(link.Url);
		view.CodeTextCreated = (text, language) => ConfigureCodeText(text, language, _focus, muted);
		// Link paragraphs are their own focus targets. Without a guard a click asks the group body
		// and the conversation to scroll the focused paragraph into view, which can move the
		// reader's position even though the clicked word was already visible.
		view.FocusableElementCreated = paragraph => _focus.Track(paragraph);
		if (muted) view.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextMuted);
		return view.SmoothScrolling();
	}

}
