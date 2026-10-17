namespace SongSwitcher.Services;

/// <summary>
/// 拖拽排序中的坐标换算。抽成纯函数以便脱离界面独立验证。
/// </summary>
public static class DragPlacement
{
    /// <summary>
    /// 依据指针相对目标卡片的位置，计算应当插入到哪个索引。
    /// 落在左半边插到目标之前，右半边插到目标之后。
    /// </summary>
    /// <param name="pointerXInTarget">指针在目标卡片坐标系内的 X 值</param>
    /// <param name="targetWidth">目标卡片宽度</param>
    /// <param name="targetIndex">目标卡片当前索引</param>
    public static int ComputeInsertIndex(double pointerXInTarget, double targetWidth, int targetIndex)
        => pointerXInTarget > targetWidth / 2 ? targetIndex + 1 : targetIndex;

    /// <summary>位移是否已达到拖拽阈值（用于区分点击与拖拽）。</summary>
    public static bool ExceedsDragThreshold(double dx, double dy, double threshold)
        => Math.Abs(dx) >= threshold || Math.Abs(dy) >= threshold;
}