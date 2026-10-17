using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SongSwitcher.Models;

namespace SongSwitcher.Services;

/// <summary>
/// 拖拽时跟随指针移动的"幽灵卡片"（Adorner 实现，浮在所有卡片之上）。
///
/// 之所以自己画而不是用 VisualBrush 直接抓源卡片：源卡片在拖拽中会被调成半透明占位，
/// VisualBrush 会把这个半透明一并画进幽灵里，视觉上就消失了。
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    public const double CardWidthSize = 232;
    public const double CardHeightSize = 132;

    private const double CardWidth = CardWidthSize;
    private const double CardHeight = CardHeightSize;

    private readonly Song _song;
    private readonly Brush _borderBrush;
    private readonly Brush _fillBrush;
    private readonly Brush _accentBrush;
    private readonly Brush _textBrush;
    private readonly Brush _subTextBrush;

    /// <summary>指针按下点在卡片内的相对位置，让幽灵不会"跳"到指针中心。</summary>
    private Point _grabOffset = new(CardWidth / 2, 28);

    /// <summary>
    /// Adorner 继承自 FrameworkElement（不是 UIElement），没有 Left/Top 属性，定位要自己实现。
    /// 但 <b>RenderTransform 的所有权归 AdornerLayer</b>：它在 ArrangeOverride 里通过
    /// AdornerTransform（内部就是 RenderTransform）把幽灵搬到被装饰元素（SongList）的原点，
    /// 且 adorner 每次需要重新 arrange（起拖后第一帧、窗口移动/缩放等）都会重新赋值——
    /// 直接写 RenderTransform 会被一次 arrange 整个冲掉（幽灵瞬移回列表左上角）。
    /// 正确做法是重写 <see cref="GetDesiredTransform"/>（官方扩展点）：把层提议的定位变换
    /// 与我们的位移复合，层什么时候重新定位都不会冲掉我们；拖拽中指针移动频繁、来不及等
    /// arrange，所以 MoveTo 里也立即把同一份复合结果写回 RenderTransform。
    /// 缩放同理不碰这些变换，放在 OnRender 的绘制变换里（见 _scaleTransform）。
    /// </summary>
    private readonly TranslateTransform _position = new();

    /// <summary>层最近一次提议的定位变换（GetDesiredTransform 里记录，MoveTo 复用它复合）。</summary>
    private GeneralTransform? _layerProposed;

    /// <summary>
    /// 缩放变换（绕卡片中心），供 OnRender 绘制时使用。
    /// 动画它自身的 ScaleX/ScaleY（double 类型，类型匹配）；
    /// 绝不能对 RenderTransform 属性本身做 DoubleAnimation（属性类型是 Transform，会抛异常）。
    /// </summary>
    private readonly ScaleTransform _scaleTransform = new(1, 1, CardWidth / 2, CardHeight / 2);

    private double _angle = -3;      // 轻微倾斜，模拟"被拎起来"的物理感
    private double _opacity = 0.0;   // 起始透明，拖拽开始时淡入

    public DragGhostAdorner(UIElement adornedElement, Song song) : base(adornedElement)
    {
        _song = song;
        // 预置一份复合变换：万一渲染发生在首次 arrange 之前，幽灵也不会掉到窗口左上角
        RenderTransform = Compose(_layerProposed ?? ComputeProposedTransform());

        _fillBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        _borderBrush = new SolidColorBrush(Color.FromRgb(0xAF, 0xC6, 0xF3));
        _accentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
        _textBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30));
        _subTextBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0));

        IsHitTestVisible = false;   // 幽灵不能挡住下方卡片的命中测试
    }

    public double CornerRadius { get; set; } = 10;

    /// <summary>设置指针在幽灵内部的抓取点（相对幽灵左上角）。</summary>
    public void SetGrabOffset(Point offset)
    {
        _grabOffset = new Point(
            Math.Max(0, Math.Min(CardWidth, offset.X)),
            Math.Max(0, Math.Min(CardHeight, offset.Y)));
    }

    /// <summary>
    /// 把幽灵移动到指定位置（左上角位置）。坐标为被装饰元素（SongList）的局部坐标，
    /// 与 Card_MouseMove 里 e.GetPosition(SongList) 的语义一致。
    /// </summary>
    public void MoveTo(Point topLeftInAdornedElement)
    {
        // 不做边界收缩，让卡片可以部分移出列表，跟真实拖拽手感一致
        _position.X = topLeftInAdornedElement.X - _grabOffset.X;
        _position.Y = topLeftInAdornedElement.Y - _grabOffset.Y;
        // 层只在 adorner 重新 arrange 时才通过 GetDesiredTransform 应用变换，
        // 拖拽中指针移动频繁，不能等下一轮布局，这里立即写回当前复合结果
        RenderTransform = Compose(_layerProposed ?? ComputeProposedTransform());
        InvalidateVisual();
    }

    /// <summary>
    /// AdornerLayer 每次给幽灵应用定位变换时都会调用（官方扩展点，见 Adorner 文档）：
    /// 把层提议的"搬到被装饰元素原点"与我们的指针位移复合后返回，
    /// 这样层无论何时重新定位幽灵（起拖第一帧、窗口移动/缩放）都不会冲掉位移。
    /// </summary>
    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        _layerProposed = transform;
        return Compose(transform);
    }

    /// <summary>层提议的定位变换与我们的位移复合（两者都是平移，先后顺序无所谓）。</summary>
    private Transform Compose(GeneralTransform? proposed)
    {
        var group = new TransformGroup();
        // TransformGroup.Children 只接受 Transform；层的提议变换来自
        // TransformToAncestor（布局偏移），实际总是 MatrixTransform，
        // 非仿射变换层自己也会丢弃（见 AdornerLayer.ArrangeOverride）
        if (proposed is Transform t) group.Children.Add(t);
        group.Children.Add(_position);
        return group;
    }

    /// <summary>
    /// 层提议变换的兜底计算（GetDesiredTransform 还没被调用过时用）：
    /// 与 AdornerLayer.UpdateElementAdorners 同源——被装饰元素到装饰层父节点的变换。
    /// </summary>
    private GeneralTransform? ComputeProposedTransform()
    {
        try
        {
            var layer = AdornerLayer.GetAdornerLayer(AdornedElement);
            var layerParent = layer != null ? VisualTreeHelper.GetParent(layer) as Visual : null;
            return layerParent == null ? null : AdornedElement.TransformToAncestor(layerParent);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>设置倾斜角度（度）。</summary>
    public void SetAngle(double degrees) => _angle = degrees;

    /// <summary>设置缩放（绕卡片中心）。</summary>
    public void SetScale(double scale) => _scaleTransform.ScaleX = _scaleTransform.ScaleY = scale;

    /// <summary>
    /// 平滑地把幽灵放大/缩小到目标缩放值。
    /// 动画的是 ScaleTransform 的 ScaleX/ScaleY（double 类型），不是 RenderTransform 本身。
    /// </summary>
    public void PlayScaleAnimation(double from, double to, System.TimeSpan duration, IEasingFunction? easing = null)
    {
        void Animate(DependencyProperty property)
        {
            var anim = new DoubleAnimation(from, to, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            };
            // 动画结束把最终值落到变换上（Stop 会让属性回到本地值）
            anim.Completed += (_, _) =>
            {
                if (property == ScaleTransform.ScaleXProperty) _scaleTransform.ScaleX = to;
                else _scaleTransform.ScaleY = to;
            };
            _scaleTransform.BeginAnimation(property, anim);
        }

        Animate(ScaleTransform.ScaleXProperty);
        Animate(ScaleTransform.ScaleYProperty);
    }

    /// <summary>设置整体不透明度。</summary>
    public void SetOpacity(double opacity)
    {
        _opacity = opacity;
        // 元素级 Opacity 必须同步：淡入动画是 FillBehavior=Stop，结束后属性会回落到
        // 本地值（构造时的 0），只改内部字段的话幽灵会在淡入完成后整体消失。
        Opacity = opacity;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // 幽灵不受布局约束影响，永远按自身尺寸绘制
        return new Size(CardWidth, CardHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_opacity <= 0.001) return;

        // 围绕卡片中心做倾斜 + 缩放，视觉上"被拎起"（缩放值由动画驱动，见 PlayScaleAnimation）
        dc.PushTransform(new TransformGroup
        {
            Children =
            {
                new RotateTransform(_angle, CardWidth / 2, CardHeight / 2),
                _scaleTransform
            }
        });

        // 投影：向下偏移两层，营造悬浮感
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(38, 0x1A, 0x1F, 0x2B)),
            null, new Rect(2, 6, CardWidth, CardHeight), CornerRadius + 4, CornerRadius + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(26, 0x1A, 0x1F, 0x2B)),
            null, new Rect(4, 3, CardWidth - 4, CardHeight), CornerRadius + 2, CornerRadius + 2);

        // 本体
        dc.DrawRoundedRectangle(_fillBrush, new Pen(_borderBrush, 1.5),
            new Rect(0, 0, CardWidth, CardHeight), CornerRadius, CornerRadius);

        // 左侧强调条，标示"这是被拖的那个"
        dc.DrawRoundedRectangle(_accentBrush, null,
            new Rect(0, 0, 4, CardHeight), CornerRadius, CornerRadius);

        var pad = 16.0;
        double y = 22;

        dc.DrawText(BuildText(_song.Title, 15, FontWeights.SemiBold, _textBrush),
            new Point(pad, y));
        y += 24;
        dc.DrawText(BuildText(_song.DurationText, 11, FontWeights.Normal, _subTextBrush),
            new Point(pad, y));

        // 快捷键徽标
        y += 20;
        var badgeText = string.IsNullOrEmpty(_song.Hotkey) ? "未设置快捷键" : _song.Hotkey;
        var badgeBrush = string.IsNullOrEmpty(_song.Hotkey) ? _subTextBrush : _accentBrush;
        var badge = BuildText(badgeText, 13, FontWeights.SemiBold, badgeBrush);
        var badgeWidth = badge.Width + 22;
        dc.DrawRoundedRectangle(
            new SolidColorBrush(string.IsNullOrEmpty(_song.Hotkey)
                ? Color.FromRgb(0xF2, 0xF3, 0xF5)
                : Color.FromRgb(0xEE, 0xF3, 0xFE)),
            null, new Rect(pad, y, badgeWidth, 26), 6, 6);
        dc.DrawText(badge, new Point(pad + 11, y + 4));

        // 底部提示线，暗示这是浮层而非真实卡片
        y += 36;
        dc.DrawText(BuildText("松手放到此处", 11, FontWeights.Normal, _subTextBrush),
            new Point(pad, y));

        dc.Pop();
    }

    private FormattedText BuildText(string? text, double size, FontWeight weight, Brush fill)
    {
        var ft = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // 超长标题截断，避免撑破卡片
        const double maxWidth = CardWidth - 28;
        if (ft.Width > maxWidth)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }

        return ft;
    }
}