// 【拆分】热键切格（Ctrl+Alt+N、Alt+QWE/ASD）与空格切格钩子。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Runtime.InteropServices;

internal sealed partial class HostForm
{
    // ── 键盘切格 ───────────────────────────────────────────
    // Ctrl+Alt+1..4 切到第 N 格并交焦点，Ctrl+Alt+0 取消放大。
    //
    // 只在宿主或它的某个格子在前台时注册，切走就注销：RegisterHotKey 是全局的，
    // 一直占着会把别的程序的 Ctrl+Alt+数字吃掉。
    // 为什么不用单 Alt+数字：VS Code / Electron 自己用那套，抢了会影响格子里的客户端。
    const int HotkeyBase = 0xA10;      // Ctrl+Alt+0..6
    const int AltKeyBase = 0xA20;      // Alt+反引号 / Alt+QWERTY / Alt+A
    const int WindowMenuKey = 0xAF0;
    bool _windowMenuHotkey;

    void SyncWindowMenuHotkey(bool active)
    {
        if (!IsHandleCreated || active == _windowMenuHotkey) return;
        if (active)
            _windowMenuHotkey = Native.RegisterHotKey(Handle, WindowMenuKey, Native.MOD_ALT | Native.MOD_NOREPEAT, 0x20);
        else
        {
            Native.UnregisterHotKey(Handle, WindowMenuKey);
            _windowMenuHotkey = false;
        }
    }
    bool _hotkeys;

    // Alt + Q W E / A S D 跳第 1..6 格；Alt+反引号 切下一格；Alt+X 取消放大。
    //
    //     Q W E     ← 1 2 3
    //     A S D     ← 4 5 6
    //
    // 排成两行三列，跟格子的排布对得上，而且全在左手 home row 附近，一只手够得着。
    //
    // 为什么是 Alt 加字母：Ctrl+数字 被编辑器分组占着，Alt+数字 被 Electron 的
    // 菜单栏占着，Alt/Ctrl+反引号 在 VS Code 系里是终端 —— 四家里三家是 Electron。
    // CapsLock 试过，用户否了（那是要用的键），顺带也就省掉了一个全局键盘钩子。
    //
    // 这几个键是实测挑的，不是拍脑袋：2026-09-20 用 RegisterHotKey 挨个探过一遍，
    // Alt+R、Alt+Z、Alt+G 本机已经被别的程序全局占着，所以没用它们。
    static readonly uint[] AltCellKeys = new uint[] { 0x51, 0x57, 0x45, 0x41, 0x53, 0x44 };

    void SyncHotkeys()
    {
        IntPtr fg = Native.GetForegroundWindow();
        bool ours = fg == Handle || Native.IsChild(Handle, fg);
        // 键盘钩子和悬停切格都要问「现在是不是在用这个窗口」，但它们不能自己去
        // GetForegroundWindow —— 钩子是每一次按键都要跑的。心跳里算好放这儿。
        _hostActive = ours;
        SyncWindowMenuHotkey(ours);
        if (ours == _hotkeys || !IsHandleCreated) return;
        _hotkeys = ours;
        for (int i = 0; i <= Math.Min(9, MaxSlots); i++)
        {
            if (ours)
                Native.RegisterHotKey(Handle, HotkeyBase + i,
                    Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)('0' + i));
            else
                Native.UnregisterHotKey(Handle, HotkeyBase + i);
        }
        // Alt+QWE/ASD = 第 1..6 格，Alt+反引号 = 下一格，Alt+X = 取消放大。
        // 跟上面一样只在宿主这一家在前台时占着 —— RegisterHotKey 是全局的，
        // 一直占着会把别的程序的 Alt+Q 吃掉。
        for (int i = 0; i < AltCellKeys.Length; i++)
        {
            if (ours)
                Native.RegisterHotKey(Handle, AltKeyBase + i,
                    Native.MOD_ALT | Native.MOD_NOREPEAT, AltCellKeys[i]);
            else
                Native.UnregisterHotKey(Handle, AltKeyBase + i);
        }
        int cyc = AltKeyBase + 100, unzoom = AltKeyBase + 101;
        if (ours)
        {
            Native.RegisterHotKey(Handle, cyc, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_OEM_3);
            Native.RegisterHotKey(Handle, unzoom, Native.MOD_ALT | Native.MOD_NOREPEAT, 0x58 /* X */);
        }
        else
        {
            Native.UnregisterHotKey(Handle, cyc);
            Native.UnregisterHotKey(Handle, unzoom);
        }
    }

    // 「宿主这一家在不在前台」—— 悬停切格每 120ms 要问一次，空格钩子每一次按键
    // 都要问一次，都不能自己去 GetForegroundWindow。心跳里算好放这儿。
    volatile bool _hostActive;

    // 一 空格当切格层（**默认关着**）一一一一一一一一一一一一一一一一
    //
    // 2026-09-20 用户提的，做完把代价摆出来之后用户说「那算了」—— 所以默认不开，
    // 整段逻辑连同那个键盘钩子都不会装起来。留着是因为它本身是好使的，
    // 哪天想试在「设置」菜单里勾上就行。日常切格走 Alt + QWE/ASD。
    //
    //   轻敲空格            还是空格
    //   按住空格 + Q W E    跳第 1 2 3 格
    //   按住空格 + A S D    跳第 4 5 6 格
    //   按住空格 + 1..6     同上
    //   按住空格 + X / 0    取消放大
    //
    // 拇指本来就搁在空格上，一只手就够 —— 但代价很实在：**空格必须先吃掉、
    // 等松手再补发**，因为按下的那一刻还不知道你是要打空格还是要切格。
    // 于是每打一个空格都要走一遍这段逻辑，而出问题的地方正好是你在给 agent
    // 写提示词的时候。所以三条保命规则：
    //
    //  1) 防串位：挂着空格时来了一个**不是**切格的键，先把空格补发出去再放行。
    //     不然快速滚键（打完一个词按空格、下一个字母紧跟着）会变成字母在前、
    //     空格在后。这是 SpaceFN 那一类方案最常见的翻车点。
    //  2) 超时兜底：挂了 400 毫秒还没等到别的键，就把空格发出去（按住空格翻页、
    //     游戏里按住跳，都还得能用）。这一条在悬停那个 120ms 的计时器里查。
    //  3) 自己补发的那一下带 INJECTED 标记，钩子看见就直接放行，不会自己咬自己。
    //
    // 宿主这一家不在前台时，整段逻辑碰都不碰空格。菜单里也能整个关掉。
    IntPtr _kbHook = IntPtr.Zero;
    Native.LowLevelKeyboardProc _kbProc;   // 必须自己拿着，不然被 GC 掉、钩子静默失效
    bool _spacePending, _spaceUsed;
    // 超时兜底已经把这一下空格发出去了，这次按住剩下的自动重复就别再拦
    // —— 不然「按住空格翻页」会变成 400 毫秒才一下（系统本来是每秒三十下）。
    bool _spaceFlushed;
    DateTime _spaceAt;

    void SyncSpaceHook()
    {
        bool want = Prefs.Get("spaceswitch", false);
        if (want == (_kbHook != IntPtr.Zero)) return;
        if (want)
        {
            _kbProc = KbProc;
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc,
                                              Native.GetModuleHandle(null), 0);
        }
        else
        {
            try { Native.UnhookWindowsHookEx(_kbHook); }
            catch { }
            _kbHook = IntPtr.Zero;
            _kbProc = null;
            FlushSpace();
        }
    }

    static void SendSpace()
    {
        Native.keybd_event(Native.VK_SPACE, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Native.VK_SPACE, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // 把挂着的那个空格放出去。
    void FlushSpace()
    {
        if (!_spacePending) return;
        _spacePending = false;
        if (!_spaceUsed) SendSpace();
        _spaceUsed = false;
    }

    void SpaceTimeoutFlush()
    {
        if (!_spacePending || _spaceUsed) return;
        FlushSpace();
        _spaceFlushed = true;      // 这次按住剩下的重复直接放行
    }

    // 空格键按下去之后、还没决定是空格还是切格的那段时间里，把它按住不动
    // 也得能用（翻页、游戏）。400 毫秒没等到别的键就当普通空格发出去。
    void SpaceTimeout()
    {
        if (_spacePending && !_spaceUsed && (DateTime.Now - _spaceAt).TotalMilliseconds > 400)
            SpaceTimeoutFlush();
    }

    // 按住空格时，哪个键跳到第几格。返回 -1 表示「不是切格键」。
    static int SpaceCell(int vk)
    {
        if (vk == 0x51) return 1;            // Q
        if (vk == 0x57) return 2;            // W
        if (vk == 0x45) return 3;            // E
        if (vk == 0x41) return 4;            // A
        if (vk == 0x53) return 5;            // S
        if (vk == 0x44) return 6;            // D
        if (vk == 0x58) return 0;            // X = 取消放大
        if (vk >= 0x30 && vk <= 0x36) return vk - 0x30;
        if (vk >= 0x60 && vk <= 0x66) return vk - 0x60;
        return -1;
    }

    IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 这个回调每一次按键都跑，而且跑在宿主的界面线程上 —— 宿主跟四家共用
        // 输入队列，这里卡一下全机打字都跟着卡。只做判断，活儿 BeginInvoke 出去。
        if (nCode >= 0)
        {
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & Native.LLKHF_INJECTED) == 0)     // 自己补发的那一下直接放行
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x0100 || msg == 0x0104;
                bool up = msg == 0x0101 || msg == 0x0105;
                int vk = Marshal.ReadInt32(lParam);
                if (!_hostActive)
                {
                    FlushSpace();                          // 切走了别把空格扣着
                    _spaceFlushed = false;
                }
                else if (vk == Native.VK_SPACE)
                {
                    if (up) { FlushSpace(); _spaceFlushed = false; return (IntPtr)1; }
                    if (down)
                    {
                        if (_spaceFlushed) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                        if (!_spacePending) { _spacePending = true; _spaceUsed = false; _spaceAt = DateTime.Now; }
                        return (IntPtr)1;                  // 先扣着，松手再说
                    }
                }
                else if (_spacePending && down)
                {
                    int cell = SpaceCell(vk);
                    if (cell >= 0)
                    {
                        _spaceUsed = true;                 // 这一次空格是当层用的，别补发
                        int c = cell;
                        BeginInvoke(new Action(delegate { HotkeyCell(c); }));
                        return (IntPtr)1;
                    }
                    // 不是切格键：先把空格补出去再放行，顺序才不会乱。
                    FlushSpace();
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }


    // 切到下一个还活着的格子（Alt+反引号）。
    void CycleCell()
    {
        if (_slots.Count == 0) return;
        int cur = -1;
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Hwnd != IntPtr.Zero && _slots[i].Hwnd == _lastFocused) cur = i;
        for (int step = 1; step <= _slots.Count; step++)
        {
            int j = (cur + step) % _slots.Count;
            if (j < 0) j += _slots.Count;
            if (_slots[j].Hwnd != IntPtr.Zero && Native.IsWindow(_slots[j].Hwnd))
            {
                HotkeyCell(j + 1);
                return;
            }
        }
    }

    void HotkeyCell(int n)
    {
        // 队列视图：只有中间那张看得见，焦点交给藏着的格子等于打字打进看不见的窗口。
        // 所以 Ctrl+Alt+N = 把第 N 家拉到中间；Ctrl+Alt+0 = 放开，回到按队列排。
        if (_queueMode)
        {
            if (n == 0) { SkipFront(); return; }
            if (n > _slots.Count) return;
            Slot q = _slots[n - 1];
            if (q != QueueFront) PinFront(q);
            if (q.Hwnd != IntPtr.Zero && Native.IsWindow(q.Hwnd)) { _focusByUser = true; GiveFocus(q.Hwnd); }
            return;
        }
        if (n == 0)
        {
            // 取消放大，回到格子
            if (_solo == null) return;
            _solo = null;
            SaveSolo();
            Relayout(true);
            return;
        }
        if (n > _slots.Count) return;
        Slot s = _slots[n - 1];
        if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) return;
        // 正在放大别的格时，把放大切到这一格（不然按了没反应，看着像坏了）
        if (_solo != null && _solo != s)
        {
            _solo = s;
            SaveSolo();
            Relayout(true);
        }
        // 跟真人点进来一样：算看过、灭黄灯、补输入法
        Seen.Touch(s.Kind.Id);
        s.DoneUnseen = false;
        RefreshAgentIndicator(s);
        _focusByUser = true;
        GiveFocus(s.Hwnd);
    }
}
