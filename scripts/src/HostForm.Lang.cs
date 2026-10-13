// 界面语言切换（中文 / English，见 I18n.cs）：右上角最小化左边一个小按钮，中文界面显示「EN」、英文界面显示「中」，
// 点一下就切（用户 2026-09-26 指定放在那块空白处）。切的时候跟换主题一样盖一层、换好再淡出。

using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    GlassButton _langBtn;

    // 提示文字按中文记下来，切语言时统一重设（ToolTip 只认设进去那一刻的字）
    readonly Dictionary<Control, string> _tipText = new Dictionary<Control, string>();

    void Tip(Control c, string zh)
    {
        if (c == null) return;
        _tipText[c] = zh;
        _tips.SetToolTip(c, L.T(zh));
    }

    void ReapplyTips()
    {
        foreach (var kv in new List<KeyValuePair<Control, string>>(_tipText))
        {
            if (kv.Key.IsDisposed) { _tipText.Remove(kv.Key); continue; }
            _tips.SetToolTip(kv.Key, L.T(kv.Value));
        }
    }

    void CreateLangButton()
    {
        _langBtn = MkBtn("", 0, 0, D(46), D(36));
        _langBtn.Quiet = true;
        _langBtn.Radius = D(6);
        _langBtn.Font = Fonts.Medium(9f);
        _langBtn.Click += delegate { SetLanguage(L.En ? "zh" : "en"); };
        Controls.Add(_langBtn);
        SyncLangButton();
    }

    void SyncLangButton()
    {
        _langBtn.Text = L.En ? "中" : "EN";
        Tip(_langBtn, L.En ? "切换到中文界面" : "切换到英文界面");
    }

    void SetLanguage(string lang)
    {
        LayoutTransition tr = null;
        if (WindowState != FormWindowState.Minimized)
        {
            if (_transition != null && !_transition.IsDisposed) _transition.Close();
            tr = LayoutTransition.CoverFade(this, ClientRectangle);
            _transition = tr;
        }
        Prefs.SetStr("lang", lang);
        L.Load();
        SyncLangButton();
        ReapplyTips();
        RefitLabels(this);
        Relayout(false);   // 工具栏按钮、侧栏名字的宽度都按新文字重排
        Invalidate(true);
        Update();
        UpdateChrome();
        if (tr != null)
        {
            tr.Begin(new Rectangle[0], null, null);
            tr.HostPainted();
        }
    }

    static void RefitLabels(Control root)
    {
        foreach (Control c in root.Controls)
        {
            var gl = c as GlassLabel;
            if (gl != null) gl.Refit();
            if (c.HasChildren && !(c is SitePanel)) RefitLabels(c);
        }
    }
}
