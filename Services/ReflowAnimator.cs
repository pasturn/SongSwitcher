using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SongSwitcher.Services;

/// <summary>
/// 让位动画工具：WrapPanel 重新布局是瞬时的（没有过渡），
/// 所以在每次重排之后，用 RenderTransform 手动补一段"平滑滑过去"的动画。
///
/// 原理：记录每张卡片重排前的位置，重排后算出它被挪动了多少，
/// 先把这个位移当作 RenderTransform 立刻施加（视觉上位置不变），
/// 再动画把 RenderTransform 归零 —— 看起来就是平滑滑动。
///
/// 关键设计：以 **数据对象（Sond）而非 UI 元素** 作为键。
/// 容器在动画过程中可能被 WPF 回收重建，若按元素做键，动画会丢、卡片会瞬间跳位；
/// 按数据对象做键，每帧重新查当前容器，动画始终生效。
/// </summary>
public static class ReflowAnimator
{
    private static readonly Duration MoveDuration = new(System.TimeSpan.FromMilliseconds(180));
    private static readonly Duration FadeDuration = new(System.TimeSpan.FromMilliseconds(140));

    private static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>用歌曲对象定位其当前容器（容器可能被回收重建，故每帧重查）。</summary>
    public static FrameworkElement? FindContainer(object item, IEnumerable<FrameworkElement> containers)
    {
        foreach (var el in containers)
            if (ReferenceEquals(el.DataContext, item)) return el;
        return null;
    }

    /// <summary>
    /// 对一组卡片播放"滑到新位置"的动画。
    /// </summary>
    /// <param name="before">重排之前各卡片的位置</param>
    /// <param name="current">重排之后各卡片的位置</param>
    /// <param name="containers">当前所有容器，用于按数据对象反查元素</param>
    /// <param name="skip">需要跳过的数据对象（被拖拽的那个，它由幽灵代替）</param>
    public static void AnimateToNewPositions(
        IReadOnlyDictionary<object, Point> before,
        IReadOnlyDictionary<object, Point> current,
        IEnumerable<FrameworkElement> containers,
        object? skip = null)
    {
        var containerList = containers as IList<FrameworkElement> ?? new List<FrameworkElement>(containers);

        foreach (var (item, newPos) in current)
        {
            if (skip != null && ReferenceEquals(item, skip)) continue;
            if (!before.TryGetValue(item, out var oldPos)) continue;

            double dx = oldPos.X - newPos.X;
            double dy = oldPos.Y - newPos.Y;
            if (System.Math.Abs(dx) < 0.5 && System.Math.Abs(dy) < 0.5) continue;   // 位置没变，无需动画

            var element = FindContainer(item, containerList);
            if (element == null) continue;   // 容器已被回收，跳过即可

            var translate = new TranslateTransform(dx, dy);
            element.RenderTransform = translate;

            var animX = new DoubleAnimation(0, MoveDuration) { EasingFunction = EaseOut };
            var animY = new DoubleAnimation(0, MoveDuration) { EasingFunction = EaseOut };

            translate.BeginAnimation(TranslateTransform.XProperty, animX, HandoffBehavior.SnapshotAndReplace);
            translate.BeginAnimation(TranslateTransform.YProperty, animY, HandoffBehavior.SnapshotAndReplace);
        }
    }

    /// <summary>清掉元素的动画与位移，避免残留。</summary>
    public static void ResetTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform t)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
        }
        element.RenderTransform = null;
    }

    /// <summary>淡出并降低不透明度（拖拽开始时把原卡片变成占位）。</summary>
    public static void FadeOut(UIElement element, double targetOpacity = 0.3, System.TimeSpan? duration = null)
    {
        var anim = new DoubleAnimation(element.Opacity, targetOpacity, duration ?? FadeDuration)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) => element.Opacity = targetOpacity;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>恢复到完全不透明（拖拽结束时占位卡片复原）。</summary>
    public static void FadeToFull(UIElement element, System.TimeSpan? duration = null)
    {
        var anim = new DoubleAnimation(element.Opacity, 1.0, duration ?? FadeDuration)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) => element.Opacity = 1.0;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// 采集一组卡片在指定参照元素坐标系中的位置，键为卡片的数据对象。
    /// 不依赖 IsVisible：离屏宿主下 IsVisible 恒为 false，会导致采集全空。
    /// </summary>
    public static Dictionary<object, Point> CapturePositions(
        IEnumerable<FrameworkElement> elements,
        UIElement relativeTo)
    {
        var map = new Dictionary<object, Point>();
        foreach (var el in elements)
        {
            var item = el.DataContext;
            if (item == null) continue;
            try
            {
                map[item] = el.TranslatePoint(new Point(0, 0), relativeTo);
            }
            catch
            {
                // 元素已被回收/脱离树，忽略即可
            }
        }
        return map;
    }
}