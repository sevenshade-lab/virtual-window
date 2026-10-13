// 外观切换（照 Que）：工具栏一个图标按钮，太阳 = 浅色、月亮 = 深色、显示器 = 跟随系统；
// 点一下直接切明暗，右键出三选一的小菜单；「设置 › 外观」里也有一份。切的时候整窗盖一张当前画面、底下换好颜色再淡出，
// 不生硬地一下跳色。只管虚拟窗口自己的外观，嵌进来的各家有自己的主题。

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    GlassButton _themeBtn;

    static readonly string[] ThemeIds = { "light", "dark", "auto" };
    static readonly string[] ThemeNames = { "浅色", "深色", "跟随系统" };

    // Segoe Fluent Icons（Win11）/ Segoe MDL2 Assets（Win10）里的字形：太阳、月亮、显示器
    static string ThemeGlyph(string id)
    {
        return id == "light" ? "" : id == "dark" ? "" : "";
    }

    static Font _iconFont;
    static Font IconFont(float size)
    {
        if (_iconFont != null) return _iconFont;
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using (var f = new Font(name, size))
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) { _iconFont = new Font(name, size); break; }
        }
        return _iconFont ?? (_iconFont = new Font(SystemFonts.MessageBoxFont.FontFamily, size));
    }

    static string ThemeTip(string id)
    {
        return L.F("外观：{0}（点一下切明暗，右键可选跟随系统）", L.T(ThemeNames[Array.IndexOf(ThemeIds, id)]));
    }

    void CreateThemeButton()
    {
        _themeBtn = MkBtn(ThemeGlyph(Theme.Preference), 0, 8, 36, 32);
        _themeBtn.Font = IconFont(Font.Size * 1.05f);
        // 点一下直接切明暗（用户：点了还要再选子菜单太麻烦）；右键出三选一，「跟随系统」也在「设置 › 外观」里
        _themeBtn.Click += delegate { SetThemePreference(Theme.Light ? "dark" : "light"); };
        _themeBtn.MouseUp += delegate(object s2, MouseEventArgs me) { if (me.Button == MouseButtons.Right) ShowThemeMenu(s2, me); };
        Tip(_themeBtn, ThemeTip(Theme.Preference));
        Controls.Add(_themeBtn);
    }

    void ShowThemeMenu(object sender, EventArgs e)
    {
        var menu = new ContextMenuStrip();
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        // 跟「设置」菜单一样：弹出前把焦点收回宿主（不然键盘进不了菜单），弹出后别再动它
        try { Native.SetForegroundWindow(Handle); } catch { }
        Native.SetFocus(Handle);
        menu.Closed += delegate
        {
            _menuOpen = false;
            if (giveBack != IntPtr.Zero && Native.IsWindow(giveBack) && SlotWithHwnd(giveBack) != null)
                BeginInvoke((MethodInvoker)delegate { if (!_menuOpen) GiveFocus(giveBack, true); });
        };
        AddThemeItems(menu.Items);
        ShellStyles.Menu(menu, Font, _scale);
        menu.Show(_themeBtn, new Point(0, _themeBtn.Height));
    }

    // 三个单选项，「设置」菜单的「外观」子菜单也用这个
    void AddThemeItems(ToolStripItemCollection items)
    {
        string cur = Theme.Preference;
        for (int i = 0; i < ThemeIds.Length; i++)
        {
            string id = ThemeIds[i];
            var it = new ToolStripMenuItem(L.T(ThemeNames[i])) { Checked = id == cur };
            it.Click += delegate { BeginInvoke((MethodInvoker)delegate { SetThemePreference(id); }); };
            items.Add(it);
        }
        items.Add(new ToolStripSeparator());
        string pal = Theme.Palette;
        for (int i = 0; i < PaletteIds.Length; i++)
        {
            string id = PaletteIds[i];
            var it = new ToolStripMenuItem(L.T(PaletteNames[i])) { Checked = id == pal };
            it.Click += delegate { BeginInvoke((MethodInvoker)delegate { SetPalette(id); }); };
            items.Add(it);
        }
    }

    void SetThemePreference(string id)
    {
        if (Theme.Preference == id) return;
        bool was = Theme.Light;
        Prefs.SetStr("theme", id);
        Theme.Load();
        bool now = Theme.Light;
        Theme.Light = was;   // 先还原，交给 ReloadTheme 判断变没变、统一换色
        _themeBtn.Text = ThemeGlyph(id);
        Tip(_themeBtn, ThemeTip(id));
        bool windows = WindowsThemeWouldChange();
        if (was == now && !windows) { _themeBtn.Invalidate(); return; }   // 比如「跟随系统」正好也是这个颜色
        // 先截图盖上，再改颜色。以前先改注册表（嵌进来的几家立刻开始换色）再截图，截到的可能是某一家换到一半的画面
        LayoutTransition tr = null;
        if (WindowState != FormWindowState.Minimized)
        {
            if (_transition != null && !_transition.IsDisposed) _transition.Close();
            tr = LayoutTransition.CoverFade(this, ClientRectangle);
            _transition = tr;
        }
        SyncWindowsAppsTheme();
        if (was != now)
        {
            ReloadTheme();
            Update();
            UpdateChrome();
        }
        FinishThemeCover(tr, windows);
    }

    // 换色的浮层怎么揭开：连带 Windows 一起换了的话，嵌进来的几家要各自读到新配色、重画一遍（几百毫秒不等），
    // 等它们换完、画面稳住再淡出（用户 2026-09-26：深色切浅色会闪一下 —— 以前宿主自己一画完就淡，
    // 那几家还是深色，淡完再一家家翻成白的）。淡得稍慢一点，整窗大变样时看着顺。
    void FinishThemeCover(LayoutTransition tr, bool agentsChange)
    {
        if (tr == null) return;
        if (agentsChange) tr.WaitForSettle(RectangleToScreen(ClientRectangle), 1500);
        tr.FadeDurationMs = 240;
        tr.Begin(new Rectangle[0], null, null);
        tr.HostPainted();
    }

    // 系统配色变了（在 Windows 设置里切的，或者我们连动切完广播回来的）。
    // 跟随系统时宿主也跟着换，同样盖一层、等各家换完再淡出；固定了浅色 / 深色的宿主不变（ReloadTheme 自己判）。
    void OnSystemColorsChanged()
    {
        if (Theme.Preference != "auto") { ReloadTheme(); return; }
        bool was = Theme.Light;
        Theme.Load();
        bool now = Theme.Light;
        Theme.Light = was;
        if (was == now) return;
        LayoutTransition tr = null;
        if (WindowState != FormWindowState.Minimized)
        {
            if (_transition != null && !_transition.IsDisposed) _transition.Close();
            tr = LayoutTransition.CoverFade(this, ClientRectangle);
            _transition = tr;
        }
        ReloadTheme();
        Update();
        UpdateChrome();
        FinishThemeCover(tr, true);
    }

    // 这次切换会不会连带改 Windows 的配色（固定了浅色 / 深色、开着连动、而且 Windows 现在不是这个颜色）
    bool WindowsThemeWouldChange()
    {
        string pref = Theme.Preference;
        if (pref == "auto" || !Prefs.Get("theme.syncwindows", true)) return false;
        int want = pref == "light" ? 1 : 0;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (k == null) return true;
                object apps = k.GetValue("AppsUseLightTheme"), sys = k.GetValue("SystemUsesLightTheme");
                return !(apps is int && (int)apps == want && sys is int && (int)sys == want);
            }
        }
        catch { return false; }
    }

    // 配色（谷歌蓝 / 经典橙）：明暗不变，只换一套颜色；同样盖一层淡出过渡
    static readonly string[] PaletteIds = { "google", "classic" };
    static readonly string[] PaletteNames = { "谷歌蓝", "经典橙" };

    void SetPalette(string id)
    {
        if (Theme.Palette == id) return;
        Prefs.SetStr("palette", id);
        LayoutTransition tr = null;
        if (WindowState != FormWindowState.Minimized)
        {
            if (_transition != null && !_transition.IsDisposed) _transition.Close();
            tr = LayoutTransition.CoverFade(this, ClientRectangle);
            _transition = tr;
        }
        ReloadTheme(true);
        Update();
        UpdateChrome();
        if (tr != null)
        {
            tr.Begin(new Rectangle[0], null, null);
            tr.HostPainted();
        }
    }

    // 嵌进来的五家（Claude / Codex / Cursor / OpenCode / Antigravity）都跟随 Windows 的「应用模式」配色，
    // 虚拟窗口改不了它们网页里的颜色 —— 所以切浅色 / 深色时顺带把 Windows 的应用模式也切了，
    // 它们立刻跟着变，不用重启（用户 2026-09-26 选的方案）。代价：资源管理器等跟随系统的程序也一起变。
    // 「跟随系统」时不动 Windows；设置页里能关掉这个连动（Prefs "theme.syncwindows"）。
    // 跟「个性化 › 颜色 › 选择模式」一样两项一起改：应用模式（AppsUseLightTheme）和 Windows 模式
    //（SystemUsesLightTheme，任务栏 / 开始菜单）。以前只改前者，任务栏还是白的（用户 2026-09-26）。
    void SyncWindowsAppsTheme()
    {
        string pref = Theme.Preference;
        if (pref == "auto" || !Prefs.Get("theme.syncwindows", true)) return;
        int want = pref == "light" ? 1 : 0;
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
            {
                object apps = k.GetValue("AppsUseLightTheme"), sys = k.GetValue("SystemUsesLightTheme");
                if (apps is int && (int)apps == want && sys is int && (int)sys == want) return;
                k.SetValue("AppsUseLightTheme", want, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", want, Microsoft.Win32.RegistryValueKind.DWord);
            }
            Log.W("windows apps theme -> " + (want == 1 ? "light" : "dark"));
        }
        catch (Exception ex) { Log.W("windows apps theme failed: " + ex.Message); return; }
        // 广播「配色变了」：各程序收到才会重新读；有程序卡着就别等它（SMTO_ABORTIFHUNG），放后台线程发
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            UIntPtr r;
            try { SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero, "ImmersiveColorSet", 0x0002, 1500, out r); } catch { }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
