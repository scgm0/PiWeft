using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 行高度不定的虚拟化面板，会话时间线与执行过程组体共用。替换 MewUI 的
/// <c>VariableHeightItemsPresenter</c>：每行有自己的估算高度，实测只改这一行，
/// 不会因为一行 10k DIP 的离群行拉动「已测量行的运行平均」，让整段 extent 一起收缩。
/// </summary>
/// <remarks>
/// 高度缓存分三段：<c>_heights</c>（按行缓存，含行边距）、<c>_measured</c>（是否实测过）、
/// <c>_prefix</c>（前缀和，布局/命中/定位都用它）。三者按几何增长复用；前缀和只在高度/行数/宽度
/// 变化时失效，滚动通知里的阅读点捕获是 O(log n)。
///
/// 面板不持有行数据：行数与取行走构造时的委托，行列表的所有权与差异计算留在调用方
/// （<c>MessageRows</c> / <c>StepRows</c>），流式期每帧的 <c>SameEntry</c> 比较只做一次。
/// </remarks>
internal sealed class VirtualRowsPanel<TItem, THost> : Panel
    where TItem : class
    where THost : FrameworkElement
{
    private readonly Func<int> _count;
    private readonly Func<int, TItem?> _itemAt;
    private readonly Func<THost> _createHost;
    private readonly Action<THost, TItem, int> _bind;
    private readonly Action<THost> _release;
    private readonly Dictionary<int, THost> _realized = [];
    private readonly Stack<THost> _pool = new();
    private readonly List<int> _releaseScratch = [];
    private readonly Dictionary<object, int> _heightByKey = [];
    private double[] _heights = [];
    private bool[] _measured = [];
    private double[] _prefix = [];
    private double[] _scratchHeights = [];
    private bool[] _scratchMeasured = [];
    private int _count2;
    private bool _prefixValid;
    private double _total;
    private double _width;
    private bool _disposed;
    private int _scrollTargetIndex = -1;
    private double _scrollTargetWithin;
    private double _pendingShift;

    public VirtualRowsPanel(
        Func<int> count,
        Func<int, TItem?> itemAt,
        Func<THost> createHost,
        Action<THost, TItem, int> bind,
        Action<THost> release)
    {
        _count = count;
        _itemAt = itemAt;
        _createHost = createHost;
        _bind = bind;
        _release = release;
    }

    /// <summary>视口。布局中用它读偏移与视口高度，并在需要时校正偏移。</summary>
    public ScrollViewer? Scroll { get; set; }

    /// <summary>视口外多实现的距离：上滚时行在被看到之前就已测好，少一次跳变。</summary>
    public double Overscan { get; set; } = 320;

    /// <summary>行身份：上下文里插入/删除/重排时用它迁移高度缓存。</summary>
    public Func<TItem, object?>? Key { get; set; }

    /// <summary>两行内容是否等价（据此沿用已测高度）。默认按引用相等。</summary>
    public Func<TItem, TItem, bool>? Same { get; set; }

    /// <summary>未测量行的估算高度；<c>width</c> 是当前行宽。</summary>
    public Func<TItem, double, double>? Estimate { get; set; }

    /// <summary>估算缺失时的兜底高度。</summary>
    public double DefaultEstimate { get; set; } = 48;

    /// <summary>
    /// 高度实测改变内容坐标系时，是否把「锚点上方的高度差」补进偏移。
    /// 浏览态（会话时间线未跟底）为 true；跟底态交给 <see cref="SmoothScroll"/> 追末尾。
    /// 为 null 时始终补偿（组体与旧 presenter 的行为一致）。
    /// </summary>
    public Func<bool>? PreserveReadingAnchor { get; set; }

    public double TotalHeight => _total;

    internal int RealizedCount => _realized.Count;

    public THost? HostAt(int index) => _realized.TryGetValue(index, out var host) ? host : null;

    protected override Size MeasureContent(Size availableSize)
    {
        SyncCount(_count());
        if (!double.IsFinite(availableSize.Width) || availableSize.Width <= 0)
        {
            // 无约束测量（弹出层/分栏首轮）只是探自然尺寸，用上一次的视口宽度。
            var fallback = _width > 0 ? _width : Math.Max(280, Scroll?.ViewportWidth ?? 0);
            _width = fallback > 0 ? fallback : 700;
        }
        else if (Math.Abs(availableSize.Width - _width) > 0.5)
        {
            _width = availableSize.Width;
            ClearEstimates();
        }
        EnsurePrefix();
        var width = availableSize.Width > 0 && double.IsFinite(availableSize.Width) ? availableSize.Width : _width;
        return new Size(Math.Max(0, width), Math.Max(0, _total));
    }

    protected override void ArrangeContent(Rect bounds)
    {
        if (_disposed) return;
        SyncCount(_count());
        if (_count2 == 0)
        {
            ReleaseAll();
            _total = 0;
            InvalidatePrefix();
            return;
        }

        var width = bounds.Width;
        if (!double.IsFinite(width) || width <= 0) width = _width;
        if (!double.IsFinite(width) || width <= 0) width = 700;
        if (Math.Abs(width - _width) > 0.5)
        {
            _width = width;
            ClearEstimates();
        }

        var scroll = Scroll;
        var offset = scroll?.VerticalOffset ?? Math.Max(0, -bounds.Y);
        // 布局中段 ScrollViewer 的 ViewportHeight 已经就位；没有它时退回安排给我们的高度。
        var viewport = scroll is { ViewportHeight: > 0 } measured ? measured.ViewportHeight : Math.Max(0, bounds.Height);
        var placementY = bounds.Y;

        // 先补上结构变化欠下的坐标系平移，再按最新偏移重算前缀。
        ApplyShift(scroll, ref offset, ref placementY);
        EnsurePrefix();

        if (_scrollTargetIndex >= 0 && scroll is not null)
        {
            if (_scrollTargetIndex >= _count2)
            {
                _scrollTargetIndex = -1;
            }
            else
            {
                var desired = Math.Clamp(_prefix[_scrollTargetIndex] + _scrollTargetWithin, 0, Math.Max(0, _total - viewport));
                if (Math.Abs(desired - offset) > 0.5)
                {
                    var before = offset;
                    scroll.SetScrollOffsets(scroll.HorizontalOffset, desired);
                    offset = scroll.VerticalOffset;
                    placementY -= offset - before;
                    EnsurePrefix();
                }
                else
                {
                    _scrollTargetIndex = -1;
                }
            }
        }

        var anchorIndex = FindIndexByY(offset);
        var anchorTopBefore = _prefix[anchorIndex];
        var first = FindIndexByY(Math.Max(0, offset - Overscan));
        var last = FindIndexByY(Math.Min(_total, offset + viewport + Overscan));
        ReleaseOutside(first, last);
        var changed = false;
        for (var round = 0; round < 2; round++)
        {
            if (!MeasureRange(first, last, width)) break;
            changed = true;
            EnsurePrefix();
            first = FindIndexByY(Math.Max(0, offset - Overscan));
            last = FindIndexByY(Math.Min(_total, offset + viewport + Overscan));
            ReleaseOutside(first, last);
        }

        // 高度实测改了内容坐标系：浏览态下把「锚点上方的高度变化」补到当前偏移上。
        // 只用相对量，不用测量时的绝对 offset，动画可能已经又走了一帧。
        if (changed && scroll is not null && (PreserveReadingAnchor?.Invoke() ?? true))
        {
            var shift = _prefix[Math.Min(anchorIndex, _count2 - 1)] - anchorTopBefore;
            _pendingShift += shift;
            ApplyShift(scroll, ref offset, ref placementY);
        }

        // 一轮放置：已实现的行 + 视口外被焦点钉住的行都按最新 prefix 摆位，
        // 后者不摆位会在 prefix 变化后停在旧坐标上，可能盖住可见行。
        var anchorTop = _prefix[Math.Min(anchorIndex, _count2 - 1)];
        PlaceAll(bounds.X, placementY, width);

        // 行在自己的 arrange 里改高：Markdown 的 arrange 会补实现新块并就地测量，这时行已经
        // 摆过位，下一行还停在旧位置，长出来的正文就压在它上面。同一帧把变脏的行重测一次再摆
        // 一遍，行高与后续行的位置一起落到最终值；再长也只补一轮，剩下的交给下一次布局。
        if (SettleRowsAfterArrange(first, last, width, ref changed))
        {
            EnsurePrefix();
            // 变高的行在阅读锚点上方时，内容坐标系又平移了一次。浏览态要像前一阶段那样把这段
            // 差额补进偏移，否则读者正看的那一行会往上跳一截。
            if (scroll is not null && (PreserveReadingAnchor?.Invoke() ?? true))
            {
                var shift = _prefix[Math.Min(anchorIndex, _count2 - 1)] - anchorTop;
                if (Math.Abs(shift) > 0.5)
                {
                    _pendingShift += shift;
                    ApplyShift(scroll, ref offset, ref placementY);
                }
            }
            PlaceAll(bounds.X, placementY, width);
        }

        if (changed)
        {
            // ScrollViewer 的 extent 来自 Measure 后的 DesiredSize；这里主动失效一次，
            // 让下一轮把新的总高度发布成滚动条范围。
            InvalidateMeasure();
            scroll?.InvalidateMeasure();
        }
    }

    /// 把待补的坐标系平移写进视口。被部分夹掉时留下差额，下一轮再补；
    /// 一点没生效说明已顶在边界，差额丢弃；否则内容变长时它会突然补上，变成一次没有输入来源的跳动。
    private void ApplyShift(ScrollViewer? scroll, ref double offset, ref double placementY)
    {
        if (Math.Abs(_pendingShift) <= 0.5 || scroll is null) return;
        var shift = _pendingShift;
        _pendingShift = 0;
        var before = scroll.VerticalOffset;
        scroll.SetScrollOffsets(scroll.HorizontalOffset, before + shift);
        var applied = scroll.VerticalOffset - before;
        if (Math.Abs(applied) > 0.5 && Math.Abs(applied - shift) > 0.5) _pendingShift = shift - applied;
        offset = scroll.VerticalOffset;
        placementY -= offset - before;
    }

    /// <summary>
    /// 重测上一轮放置时变脏的行：它们在 arrange 里改了高度，面板手里的高度晚一轮；
    /// 返回 true 表示调用方要把所有行再摆一遍。
    /// </summary>
    private bool SettleRowsAfterArrange(int first, int last, double width, ref bool changed)
    {
        var limit = Math.Min(last, _count2 - 1);
        var dirty = false;
        for (var index = Math.Max(0, first); index <= limit; index++)
        {
            if (_realized.TryGetValue(index, out var host) && host.IsMeasureDirty)
            {
                dirty = true;
                break;
            }
        }
        if (!dirty) return false;
        if (MeasureRange(first, last, width)) changed = true;
        return true;
    }

    private bool MeasureRange(int first, int last, double width)
    {
        var changed = false;
        var limit = Math.Min(last, _count2 - 1);
        var heights = _heights;
        var measured = _measured;
        for (var index = Math.Max(0, first); index <= limit; index++)
        {
            if (_itemAt(index) is not { } item) continue;
            var host = GetOrCreate(index);
            _bind(host, item, index);
            host.Measure(new Size(width, double.PositiveInfinity));
            var height = host.DesiredSize.Height;
            if (!double.IsFinite(height) || height <= 0) height = 24;
            if (Math.Abs(height - heights[index]) > 0.5) changed = true;
            heights[index] = height;
            measured[index] = true;
        }
        if (changed) InvalidatePrefix();
        return changed;
    }

    private void PlaceAll(double x, double y, double width)
    {
        var prefix = _prefix;
        var heights = _heights;
        foreach (var (index, host) in _realized)
        {
            if (index < 0 || index >= _count2) continue;
            var height = heights[index];
            if (!(height > 0)) height = Math.Max(1, host.DesiredSize.Height);
            host.Arrange(new Rect(x, y + prefix[index], width, height));
        }
    }

    private THost GetOrCreate(int index)
    {
        if (_realized.TryGetValue(index, out var host)) return host;
        host = _pool.Count > 0 ? _pool.Pop() : _createHost();
        Add(host);
        _realized[index] = host;
        return host;
    }

    private void ReleaseOutside(int first, int last)
    {
        if (_realized.Count == 0) return;
        _releaseScratch.Clear();
        foreach (var index in _realized.Keys)
        {
            if (index < first || index > last) _releaseScratch.Add(index);
        }
        var scratch = CollectionsMarshal.AsSpan(_releaseScratch);
        for (var i = 0; i < scratch.Length; i++)
        {
            var index = scratch[i];
            if (!_realized.Remove(index, out var host)) continue;
            // 焦点还在这一行里时先留着：滚动离开视口不应把正在选中的文本焦点一起回收。
            if (ContainsFocus(host))
            {
                _realized[index] = host;
                continue;
            }
            _release(host);
            Remove(host);
            _pool.Push(host);
        }
    }

    private static bool ContainsFocus(FrameworkElement host)
    {
        if (host.FindVisualRoot() is not Window window) return false;
        var focused = window.FocusManager.FocusedElement;
        return focused is not null && VisualTree.IsInSubtreeOf(focused, host);
    }

    public void CancelScrollTarget() => _scrollTargetIndex = -1;

    private void ReleaseAll()
    {
        if (_realized.Count > 0)
        {
            foreach (var host in _realized.Values) _release(host);
            _realized.Clear();
        }
        // Panel.Clear() 无条件作废测量。空面板在 Arrange 里每帧调用会让布局自激：
        // 空会话每帧 Arrange → Clear → InvalidateMeasure → 再 Measure/Arrange，桌面卡死。
        if (Children.Count > 0) Clear();
        // 池里的空宿主也要走一次释放：UiLifetime 会回收它们持有的图片与文本资源，只丢引用要等 GC。
        foreach (var host in _pool) UiLifetime.Release(host);
        _pool.Clear();
        _pendingShift = 0;
        _scrollTargetIndex = -1;
    }

    /// <summary>
    /// 结构变化前记下顶部行的身份与行内偏移；迁移后按同一行补偿坐标系平移。
    /// 返回的 <c>Top</c> 是这一行的前缀和起点。
    /// </summary>
    public (object? Key, double Within, double Top) CaptureShiftAnchor()
    {
        SyncCount(_count());
        if (_count2 == 0) return default;
        EnsurePrefix();
        var offset = Scroll?.VerticalOffset ?? 0;
        var index = FindIndexByY(offset);
        var item = _itemAt(index);
        return (item is null ? null : KeyOf(item), offset - _prefix[index], _prefix[index]);
    }

    /// <summary>顶部行的原始阅读位置（<c>Within</c> 可以为负：行顶在视口上方）。</summary>
    public (object? Key, double Within) CaptureReadingPoint()
    {
        SyncCount(_count());
        if (_count2 == 0) return default;
        EnsurePrefix();
        var offset = Scroll?.VerticalOffset ?? 0;
        var index = FindIndexByY(offset);
        var item = _itemAt(index);
        return (item is null ? null : KeyOf(item), offset - _prefix[index]);
    }

    /// <summary>把阅读位置映射回当前内容坐标系。</summary>
    public bool RestoreReadingPoint(object? key, double withinRow)
    {
        var index = IndexOfKey(key);
        if (index < 0) return false;
        ScrollToIndex(index, withinRow);
        return true;
    }

    public void ScrollToIndex(int index, double withinRow = 0)
    {
        var count = _count();
        if (index < 0 || index >= count || Scroll is null) return;
        SyncCount(count);
        EnsurePrefix();
        var viewport = Math.Max(0, Scroll.ViewportHeight);
        var target = Math.Clamp(_prefix[index] + withinRow, 0, Math.Max(0, _total - viewport));
        _scrollTargetIndex = index;
        _scrollTargetWithin = withinRow;
        Scroll.SetScrollOffsets(Scroll.HorizontalOffset, target);
        InvalidateArrange();
    }

    /// <summary>视口上沿往下 <paramref name="withinViewport"/> 处落在哪一行。</summary>
    public int IndexAtViewportAnchor(double withinViewport)
    {
        SyncCount(_count());
        if (_count2 == 0) return -1;
        EnsurePrefix();
        return FindIndexByY((Scroll?.VerticalOffset ?? 0) + withinViewport);
    }

    /// <summary>
    /// 行列表变化后同步高度缓存。调用方在改动自己的行列表之前拍一张
    /// <see cref="RowsSnapshot{TItem}"/>，并给出新列表里「内容未变」的下标（升序），
    /// 面板据此沿用已测高度、其余行回到估算值。
    /// </summary>
    /// <param name="previous">改动前的行快照。</param>
    /// <param name="rows">改动后的行数。</param>
    /// <param name="keptRows">新列表里可以沿用高度的下标（升序）。</param>
    /// <param name="anchor">改动前用 <see cref="CaptureShiftAnchor"/> 记录的阅读位置。</param>
    public void ApplyRows(
        RowsSnapshot<TItem> previous,
        int rows,
        List<int> keptRows,
        (object? Key, double Within, double Top) anchor)
    {
        var oldCount = previous.Count;
        var shifted = oldCount != rows;
        // 先对齐行数：新增的尾部在这里清零，再由迁移按身份填回（重排时可能落到任意下标）。
        SyncCount(rows);
        if (oldCount > 0 && rows > 0)
        {
            if (!shifted)
            {
                // 下标不变：按 kept（升序）保留同内容/原位更新的行高，其余打回估算值。
                if (keptRows.Count < rows) KeepByIndex(rows, keptRows);
            }
            else
            {
                KeepByKey(previous, rows, keptRows);
            }
        }
        EnsurePrefix();
        // 行身份和下标都没变时 prefix 一定没动，不必再按 key 找一遍（那是 O(n) 比较）。
        if (shifted && anchor.Key is not null && (PreserveReadingAnchor?.Invoke() ?? true))
        {
            var index = IndexOfKey(anchor.Key);
            if (index >= 0) _pendingShift += _prefix[index] - anchor.Top;
        }
        if (_scrollTargetIndex >= rows) _scrollTargetIndex = -1;
        InvalidateMeasure();
    }

    /// 下标一一对应：只保留 <paramref name="keptRows"/>（下标升序）里的行高。O(n)，零分配。
    private void KeepByIndex(int count, List<int> keptRows)
    {
        var kept = CollectionsMarshal.AsSpan(keptRows);
        var pointer = 0;
        var heights = _heights;
        var measured = _measured;
        for (var index = 0; index < count; index++)
        {
            if (pointer < kept.Length && kept[pointer] == index)
            {
                pointer++;
                continue;
            }
            heights[index] = 0;
            measured[index] = false;
        }
        InvalidatePrefix();
    }

    /// 行下标不再一一对应（插入/删除/重排）：按行身份建一次索引，再按身份搬运。
    /// 字典与两块临时数组都是复用的；只有真正换行（过程组合并等）才会走到这里。
    private void KeepByKey(RowsSnapshot<TItem> previous, int count, List<int> keptRows)
    {
        var oldCount = previous.Count;
        PrepareCapacity(Math.Max(oldCount, count));
        var oldHeights = _scratchHeights.AsSpan(0, oldCount);
        var oldMeasured = _scratchMeasured.AsSpan(0, oldCount);
        _heights.AsSpan(0, oldCount).CopyTo(oldHeights);
        _measured.AsSpan(0, oldCount).CopyTo(oldMeasured);
        _heights.AsSpan(0, count).Clear();
        _measured.AsSpan(0, count).Clear();

        var byKey = _heightByKey;
        byKey.Clear();
        for (var index = 0; index < oldCount; index++)
        {
            if (previous[index] is { } item) byKey[KeyOf(item)] = index;
        }
        var kept = CollectionsMarshal.AsSpan(keptRows);
        var pointer = 0;
        var same = Same;
        for (var index = 0; index < count; index++)
        {
            var item = _itemAt(index);
            if (item is null) continue;
            while (pointer < kept.Length && kept[pointer] < index) pointer++;
            var isKept = pointer < kept.Length && kept[pointer] == index;
            if (!byKey.TryGetValue(KeyOf(item), out var oldIndex)) continue;
            if (!isKept && same is not null && previous[oldIndex] is { } oldItem && !same(oldItem, item)) continue;
            _heights[index] = oldHeights[oldIndex];
            _measured[index] = oldMeasured[oldIndex];
        }
        InvalidatePrefix();
    }

    /// <summary>视口宽度变化：未测量的行按新宽度重新估算，已测行保留到重新进入实现范围时校正。</summary>
    public void InvalidateHeightsForWidth()
    {
        ClearEstimates();
        InvalidatePrefix();
        InvalidateMeasure();
    }

    private void ClearEstimates()
    {
        for (var index = 0; index < _count2; index++)
        {
            if (!_measured[index]) _heights[index] = 0;
        }
        _prefixValid = false;
    }

    private int IndexOfKey(object? key)
    {
        if (key is null) return -1;
        var count = _count();
        for (var index = 0; index < count; index++)
        {
            if (_itemAt(index) is { } item && Equals(KeyOf(item), key)) return index;
        }
        return -1;
    }

    private object KeyOf(TItem item) => Key?.Invoke(item) ?? item;

    private void PrepareCapacity(int needed)
    {
        var capacity = _heights.Length;
        if (capacity >= needed) return;
        capacity = Math.Max(needed, Math.Max(16, capacity + (capacity >> 1)));
        Array.Resize(ref _heights, capacity);
        Array.Resize(ref _measured, capacity);
        Array.Resize(ref _prefix, capacity + 1);
        Array.Resize(ref _scratchHeights, capacity);
        Array.Resize(ref _scratchMeasured, capacity);
    }

    /// 行数与缓存对齐。新增的行没有实测值（清零），缩短时只改有效长度：
    /// 之后若再变长，SyncCount 会重新清零，不会读到上一次会话残留的高度。
    private void SyncCount(int count)
    {
        PrepareCapacity(count);
        if (count == _count2) return;
        if (count > _count2)
        {
            _heights.AsSpan(_count2, count - _count2).Clear();
            _measured.AsSpan(_count2, count - _count2).Clear();
        }
        _count2 = count;
        InvalidatePrefix();
    }

    private void InvalidatePrefix() => _prefixValid = false;

    private void EnsurePrefix()
    {
        if (_prefixValid) return;
        var count = _count2;
        if (_prefix.Length < count + 1) Array.Resize(ref _prefix, count + 1);
        var heights = _heights;
        var prefix = _prefix;
        var sum = 0.0;
        prefix[0] = 0;
        for (var index = 0; index < count; index++)
        {
            var height = heights[index];
            if (!(height > 0))
            {
                height = EstimateHeight(index);
                heights[index] = height;
            }
            sum += height;
            prefix[index + 1] = sum;
        }
        _total = sum;
        _prefixValid = true;
    }

    private double EstimateHeight(int index)
    {
        if (_itemAt(index) is not { } item) return DefaultEstimate;
        var estimate = Estimate?.Invoke(item, _width > 0 ? _width : 700) ?? DefaultEstimate;
        return double.IsFinite(estimate) && estimate > 0 ? estimate : DefaultEstimate;
    }

    private int FindIndexByY(double y)
    {
        var count = _count2;
        if (count <= 0) return 0;
        y = Math.Clamp(y, 0, Math.Max(0, _total));
        var prefix = _prefix;
        int low = 0, high = count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (prefix[middle + 1] <= y) low = middle + 1;
            else high = middle;
        }
        return Math.Min(low, count - 1);
    }

    /// <summary>释放所有行宿主与缓存。与 <see cref="FrameworkElement"/> 的释放路径同名，
    /// 但这里只收拾面板自己的引用（树由 <c>UiLifetime.Release</c> 负责）。</summary>
    public void DisposePanel()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAll();
        _heights = [];
        _measured = [];
        _prefix = [];
        _scratchHeights = [];
        _scratchMeasured = [];
        _heightByKey.Clear();
        _count2 = 0;
        _prefixValid = false;
        _total = 0;
    }
}

/// <summary>
/// 结构变化前的行快照：复用同一块数组，只带有效长度。调用方在本次同步更新内读完即弃。
/// </summary>
internal readonly struct RowsSnapshot<T>(T[] items, int count)
{
    public int Count => count;
    public T this[int index] => items[index];
}
