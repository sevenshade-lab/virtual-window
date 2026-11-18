// 每格只用本次窗口、最近一次样式修复之后采到的画面。时间来自单调时钟。
internal enum BlankAction { None, Wake, Rejoin }

internal sealed class BlankRecovery
{
    public int Generation { get; private set; }
    bool repaired;
    bool attemptedOnWindow;
    int streak;
    long nextWake, nextRejoin;

    public void WindowChanged()
    {
        Generation++;
        repaired = false;
        attemptedOnWindow = false;
        streak = 0;
        nextWake = 0;
        // 冷却跟着格子，换 HWND 也不能绕过。
    }

    public bool DeferDetachedWindow(long now)
    {
        return attemptedOnWindow && now < nextRejoin;
    }

    public void StyleRepaired()
    {
        Generation++;
        repaired = true;
        streak = 0;
    }

    public BlankAction Observe(int generation, bool painted, long now)
    {
        if (generation != Generation) return BlankAction.None;
        if (painted)
        {
            repaired = false;
            streak = 0;
            return BlankAction.None;
        }
        if (++streak < 2) return BlankAction.None;
        if (repaired && now >= nextRejoin)
        {
            attemptedOnWindow = true;
            repaired = false;
            streak = 0;
            nextRejoin = now + 60000;
            nextWake = now + 10000;
            return BlankAction.Rejoin;
        }
        if (now < nextWake) return BlankAction.None;
        streak = 0;
        nextWake = now + 10000;
        return BlankAction.Wake;
    }
}
