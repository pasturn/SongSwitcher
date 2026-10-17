using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SongSwitcher.Models;
using SongSwitcher.Services;
using SongSwitcher.ViewModels;

namespace SongSwitcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Save();
        Closed += (_, _) => _viewModel.Save();

        // 拖拽途中若鼠标在窗口外松开或窗口失去焦点，MouseUp 可能收不到，
        // 必须兜底清理，否则幽灵卡片会永久残留在界面上。
        SongList.LostMouseCapture += (_, _) => AbortDrag();
        Deactivated += (_, _) => AbortDrag();

        // 进度条拖动期间挂起 ViewModel 的 200ms 进度推送：OneWay 绑定在拖动中仍然存活，
        // 定时器每次推送都会把拇指拉回当前播放位置（拇指回跳、松手落在错误位置）。
        // 松手时统一 Seek 并恢复推送。
        ProgressSlider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _viewModel.SuppressProgressPush = true));
        ProgressSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _viewModel.SuppressProgressPush = false;
                _viewModel.SeekTo(ProgressSlider.Value);
            }));
    }

    /// <summary>强制中止一次进行中的拖拽，并清理所有动画残留。</summary>
    private void AbortDrag()
    {
        if (!_isDragging && _ghost == null && _placeholder == null) return;

        if (_dragCandidate != null) _dragCandidate.IsDragPlaceholder = false;

        if (_ghost != null)
        {
            if (AdornerLayer.GetAdornerLayer(SongList) is { } layer && layer.IsAncestorOf(_ghost))
                layer.Remove(_ghost);
            _ghost = null;
        }

        if (_placeholder != null)
        {
            _placeholder.BeginAnimation(UIElement.OpacityProperty, null);
            ReflowAnimator.ResetTransform(_placeholder);
            _placeholder.Opacity = 1.0;
            _placeholder = null;
        }

        foreach (var item in GetAllContainers())
        {
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 1.0;
            ReflowAnimator.ResetTransform(item);
        }

        _isDragging = false;
        _dragCandidate = null;
        _positionsBeforeMove.Clear();
        Cursor = Cursors.Arrow;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 录入快捷键时独占按键，先于 InputBindings 与按钮处理
        if (_viewModel.IsCapturingHotkey)
        {
            _viewModel.OnPreviewKeyDown(e);
            e.Handled = true;
            return;
        }

            // KeyBinding 不支持 CommandParameter，故 F2 / Delete / 排序键在此直接处理。
            // 注意修饰键必须用 == 精确匹配而非 HasFlag：
            // Ctrl+Shift+方向键 的 Modifiers=Control|Shift，HasFlag(Control) 同样为 true，
            // 若排序 case 用 HasFlag 会排在前面把音量 case 全部挡死（曾出过这个 bug）。
            switch (e.Key)
            {
                case Key.Space when !_viewModel.IsSpaceHotkeyActive:
                    // 全局空格被关闭或被其他程序占用时，退回窗口内快捷方式，
                    // 否则本窗口内按空格会完全没反应
                    _viewModel.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.F2:
                    _viewModel.StartCaptureCommand.Execute(_viewModel.SelectedSong);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    _viewModel.RemoveCommand.Execute(_viewModel.SelectedSong);
                    e.Handled = true;
                    break;
                case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                case Key.Up when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel.MoveEarlierCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                case Key.Down when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel.MoveLaterCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Home when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel.MoveToStartCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.End when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel.MoveToEndCommand.Execute(null);
                    e.Handled = true;
                    break;
                // 音量微调用 Ctrl+Shift+上下（排序键已改为精确匹配 Ctrl，不再抢占这个组合）
                case Key.Up when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                    _viewModel.VolumeUpCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                    _viewModel.VolumeDownCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.M when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel.ToggleMuteCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        // 录入过程中吞掉抬起事件，避免按键触发按钮默认行为
        if (_viewModel.IsCapturingHotkey) e.Handled = true;
    }

    private void Card_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF 默认不会因为右键而选中卡片，这里补上，
        // 否则"右键 → 删除"作用在未选中的项上会让人困惑。
        // 同样需要从事件源向上找 ListBoxItem，sender(ListBox) 上没有 Song。
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is Song song)
        {
            _viewModel.SelectedSong = song;
            e.Handled = false;   // 继续弹菜单
        }
    }

    private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _viewModel.SeekTo(ProgressSlider.Value);

    /// <summary>
    /// 音量滑块拖动结束时立即落盘。
    /// ViewModel 里已经做了 500ms 节流，这里再存一次是为了保证"拖完就关程序"也不丢。
    /// </summary>
    private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _viewModel.FlushVolume();

    /// <summary>在音量滑块上滚动滚轮调节音量（步长与 Ctrl+Shift+方向键一致）。</summary>
    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.AdjustVolumeByWheel(e.Delta);
        e.Handled = true;
    }

    // ---------- 卡片拖拽排序 ----------

    private const double DragThreshold = 4;   // 超过该位移才判定为拖拽，避免点击误触
    private Song? _dragCandidate;
    private Point _dragStartPoint;
    private bool _isDragging;

    // ---------- 拖拽动画状态 ----------
    private DragGhostAdorner? _ghost;              // 跟随指针的浮层卡片
    private ListBoxItem? _placeholder;            // 原卡片降级成的半透明占位
    private Dictionary<object, Point> _positionsBeforeMove = new();
    private Point _grabOffsetInList;              // 按下点在 ListBox 内的位置（用于对齐幽灵抓取点）

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 只在卡片空白处起拖；按钮上的点击交还给按钮自己处理
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

        // 注意：sender 是 SongList，其 DataContext 是 ViewModel；
        // Song 挂在每个 ListBoxItem 上，必须从事件源向上找容器再取。
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is Song song)
        {
            _dragCandidate = song;
            // 记录为 SongList 的局部坐标，后续命中测试与它保持同一坐标系
            _dragStartPoint = e.GetPosition(SongList);
            _grabOffsetInList = e.GetPosition(container);
            _isDragging = false;
        }
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;

        // 全程使用 SongList 局部坐标，避免坐标系混用导致命中失败
        var pos = e.GetPosition(SongList);
        if (!_isDragging)
        {
            // 未超过阈值前不算拖拽，保持普通点击行为
            if (!DragPlacement.ExceedsDragThreshold(pos.X - _dragStartPoint.X,
                                                    pos.Y - _dragStartPoint.Y, DragThreshold))
                return;

            _isDragging = true;
            _viewModel.SelectedSong = _dragCandidate;
            Cursor = Cursors.SizeAll;
            BeginDragAnimation();
        }

        // 幽灵始终跟随指针
        _ghost?.MoveTo(pos);
        MoveDraggedCardToPointer(pos);
    }

    /// <summary>
    /// 拖拽正式开始：淡出原卡片、挂上跟随指针的幽灵。
    /// </summary>
    private void BeginDragAnimation()
    {
        if (_dragCandidate == null) return;

        // 1) 原卡片降级为半透明占位，留在布局中继续"占坑"
        _placeholder = FindAncestor<ListBoxItem>(SongList.InputHitTest(_dragStartPoint) as DependencyObject);
        if (_placeholder?.DataContext is Song s && ReferenceEquals(s, _dragCandidate))
        {
            _placeholder.Opacity = 1.0;
            ReflowAnimator.FadeOut(_placeholder, 0.3, TimeSpan.FromMilliseconds(120));
            _placeholder.RenderTransform = null;

            // 卡片描边变成虚线空槽，提示"这里会放下来"
            _dragCandidate.IsDragPlaceholder = true;
        }

        // 2) 挂上幽灵浮层
        var layer = AdornerLayer.GetAdornerLayer(SongList);
        if (layer != null)
        {
            _ghost = new DragGhostAdorner(SongList, _dragCandidate)
            {
                // 指针在卡片内的相对位置映射到 232x132 的幽灵尺寸上
                Opacity = 0,
            };
            _ghost.SetGrabOffset(new Point(
                _grabOffsetInList.X * (DragGhostAdorner.CardWidthSize / Math.Max(1, _placeholder?.ActualWidth ?? 1)),
                _grabOffsetInList.Y * (DragGhostAdorner.CardHeightSize / Math.Max(1, _placeholder?.ActualHeight ?? 1))));
            layer.Add(_ghost);
            _ghost.MoveTo(_dragStartPoint);

            // 淡入 + 微微放大，做出"被拎起来"的弹出感
            var fade = new DoubleAnimation(0, 0.97, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => _ghost?.SetOpacity(0.97);
            _ghost.BeginAnimation(UIElement.OpacityProperty, fade);

            // 缩放动画走 ScaleTransform 的 ScaleX/ScaleY（详见 DragGhostAdorner.PlayScaleAnimation），
            // 绝不能对 RenderTransform 属性本身做 DoubleAnimation
            _ghost.PlayScaleAnimation(1.0, 1.03, TimeSpan.FromMilliseconds(150),
                new CubicEase { EasingMode = EasingMode.EaseOut });
        }
    }

    /// <summary>拖拽结束：移除幽灵、占位卡片淡回原状。</summary>
    private void EndDragAnimation()
    {
        if (_ghost != null)
        {
            var ghost = _ghost;
            _ghost = null;

            // 淡出缩小后移除，制造"落位"的收尾感
            var fade = new DoubleAnimation(0.97, 0, TimeSpan.FromMilliseconds(130))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) =>
            {
                if (AdornerLayer.GetAdornerLayer(SongList) is { } l && l.IsAncestorOf(ghost)) l.Remove(ghost);
            };
            ghost.BeginAnimation(UIElement.OpacityProperty, fade);

            // 同步缩小，制造"落位"的收尾感（同样只动画 ScaleX/ScaleY）
            ghost.PlayScaleAnimation(1.03, 0.98, TimeSpan.FromMilliseconds(150),
                new CubicEase { EasingMode = EasingMode.EaseIn });
        }

        if (_placeholder != null)
        {
            var ph = _placeholder;
            _placeholder = null;
            ph.BeginAnimation(UIElement.OpacityProperty, null);
            ph.Opacity = 0.3;
            ReflowAnimator.ResetTransform(ph);
            ReflowAnimator.FadeToFull(ph);
        }

        // 复原虚线空槽
        if (_dragCandidate != null) _dragCandidate.IsDragPlaceholder = false;
    }

    /// <summary>根据指针位置算出目标索引，并把被拖卡片移过去。</summary>
    private void MoveDraggedCardToPointer(Point pointer)
    {
        if (_dragCandidate == null) return;

        // 重排会立即改变布局，先记下当前各卡片位置以便播让位动画
        var containers = GetAllContainers().ToList();
        _positionsBeforeMove = ReflowAnimator.CapturePositions(containers, SongList);

        // 命中失败时可能是因为卡片刚好移开了，此时退化为"就近插入"，
        // 避免拖到行与行之间（空白区）时卡住不动。
        var container = FindContainerUnderPoint(pointer);
        if (container?.DataContext is not Song target)
        {
            InsertByNearestPosition(pointer);
            AnimateReflow();
            return;
        }
        if (ReferenceEquals(target, _dragCandidate)) return;

        int targetIndex = _viewModel.Songs.IndexOf(target);
        if (targetIndex < 0) return;

        // 指针在目标卡片右半边时插到它后面，否则插到前面。
        // 注意：pointer 是 SongList 局部坐标，卡片原点也必须转到 SongList 坐标系——
        // TranslatePoint 的 relativeTo 传 null 得到的是根(窗口)坐标，两者混用会带上
        // SongList 原点的偏移，让左右半判定系统性偏差
        double localX = pointer.X - container.TranslatePoint(new Point(0, 0), SongList).X;
        int desired = DragPlacement.ComputeInsertIndex(localX, container.ActualWidth, targetIndex);

        _viewModel.MoveSongTo(_dragCandidate, desired);
        AnimateReflow();
    }

    /// <summary>
    /// 让其余卡片平滑滑到新位置。
    /// 必须在布局刷新之后调用，否则拿到的还是旧坐标。
    /// </summary>
    private void AnimateReflow()
    {
        if (_positionsBeforeMove.Count == 0) return;

        // ObservableCollection.Move 已改变顺序，强制刷新布局拿新坐标
        SongList.UpdateLayout();

        var containers = GetAllContainers().ToList();
        var current = ReflowAnimator.CapturePositions(containers, SongList);

        ReflowAnimator.AnimateToNewPositions(_positionsBeforeMove, current, containers, _dragCandidate);
        _positionsBeforeMove = current;
    }

    /// <summary>
    /// 指针落在卡片之间的空隙时，按几何距离找最近的插入位置。
    /// 保证拖到行间空白处也能继续移动，而不会"卡住"。
    /// </summary>
    private void InsertByNearestPosition(Point pointer)
    {
        if (_dragCandidate == null || SongList == null) return;

        double best = double.MaxValue;
        int desired = -1;

        foreach (var item in GetAllContainers())
        {
            if (item.DataContext is not Song s) continue;
            int idx = _viewModel.Songs.IndexOf(s);
            if (idx < 0) continue;

            // 与 pointer 同处 SongList 局部坐标系（relativeTo=null 会得到窗口坐标，见上）
            var origin = item.TranslatePoint(new Point(0, 0), SongList);
            double centerX = origin.X + item.ActualWidth / 2;
            double centerY = origin.Y + item.ActualHeight / 2;
            double dist = Math.Abs(pointer.X - centerX) + Math.Abs(pointer.Y - centerY) * 2;
            if (dist >= best) continue;

            best = dist;
            // 落在卡片右半边则插到它后面
            desired = DragPlacement.ComputeInsertIndex(pointer.X - origin.X, item.ActualWidth, idx);
        }

        if (desired >= 0) _viewModel.MoveSongTo(_dragCandidate, desired);
    }

    private IEnumerable<ListBoxItem> GetAllContainers()
    {
        if (SongList == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(SongList);
        for (int i = 0; i < count; i++)
        {
            foreach (var found in Walk(VisualTreeHelper.GetChild(SongList, i)))
                yield return found;
        }

        static IEnumerable<ListBoxItem> Walk(DependencyObject node)
        {
            if (node is ListBoxItem item) { yield return item; yield break; }
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
                foreach (var f in Walk(VisualTreeHelper.GetChild(node, i)))
                    yield return f;
        }
    }

    private ListBoxItem? FindContainerUnderPoint(Point pointer)
    {
        if (SongList == null) return null;
        var hit = SongList.InputHitTest(pointer) as DependencyObject;
        return FindAncestor<ListBoxItem>(hit);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_isDragging && _dragCandidate != null)
            {
                _viewModel.CommitOrder(_viewModel.Songs.Count);
            }
        }
        finally
        {
            // 无论成功与否都要收尾，否则幽灵会永久留在屏幕上
            if (_isDragging) EndDragAnimation();

            _isDragging = false;
            _dragCandidate = null;
            _placeholder = null;
            _ghost = null;
            _positionsBeforeMove.Clear();
            Cursor = Cursors.Arrow;
        }
    }

    /// <summary>
    /// 向上查找指定类型的祖先元素。
    /// 必须同时兼容可视树与逻辑树：e.OriginalSource 常是 Run 等非 Visual 元素，
    /// 直接用 VisualTreeHelper.GetParent 会抛 InvalidOperationException。
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T typed) return typed;
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            else
                node = LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}