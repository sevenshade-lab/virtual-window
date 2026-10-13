// 【拆分】组合的保存/恢复（combinations\*.xml）。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    void SaveSlots()
    {
        if (_loadingSlots || _closed) return;
        try
        {
            var kinds = new List<AgentKind>();
            foreach (Slot s in _slots) if (!s.Custom) kinds.Add(s.Kind);
            var key = new StringBuilder(_layoutIx.ToString());
            foreach (AgentKind k in kinds) key.Append('|').Append(k.Id).Append('|').Append(k.Workspace);
            if (_savedProfileKey == key.ToString()) return;
            SlotProfiles.Save(SlotProfiles.LastPath, "上次组合", kinds, _layoutIx);
            _savedProfileKey = key.ToString();
        }
        catch (Exception ex) { Log.W("保存组合失败: " + ex.Message); Toast("组合没有保存成功，请查看日志"); }
    }
    void LoadLastSlots()
    {
        if (!File.Exists(SlotProfiles.LastPath)) return;
        try
        {
            string name; int layout;
            var kinds = SlotProfiles.Load(SlotProfiles.LastPath, out name, out layout);
            _loadingSlots = true;
            _restoreOnFirstClaim = true;
            foreach (AgentKind k in kinds) AddSlot(k);
            _layoutForN = _slots.Count; _layoutIx = layout;
        }
        catch (Exception ex) { Log.W("读取组合失败: " + ex.Message); }
        finally { _loadingSlots = false; }
    }
    void AddProfileMenu(ContextMenuStrip menu)
    {
        var start = new ToolStripMenuItem(L.T("启动未打开的会话"));
        start.Enabled = _slots.Count > 0;
        start.Click += delegate
        {
            foreach (Slot s in _slots.ToArray())
                if (!s.Custom && (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd))) AddOrLaunch(s.Kind);
        };
        menu.Items.Add(start);
        var save = new ToolStripMenuItem(L.T("保存当前组合…"));
        save.Enabled = _slots.Count > 0;
        save.Click += delegate { SaveNamedProfile(); };
        menu.Items.Add(save);
        var restore = new ToolStripMenuItem(L.T("恢复组合"));
        try
        {
            if (Directory.Exists(SlotProfiles.DirectoryPath))
            foreach (string path in Directory.GetFiles(SlotProfiles.DirectoryPath, "preset-*.xml"))
            {
                try
                {
                    string name; int layout; SlotProfiles.Load(path, out name, out layout);
                    string selected = path;
                    var item = new ToolStripMenuItem(L.T(name));
                    item.Click += delegate { RestoreProfile(selected); };
                    restore.DropDownItems.Add(item);
                }
                catch { }
            }
        }
        catch { }
        restore.Enabled = restore.DropDownItems.Count > 0;
        menu.Items.Add(restore);
    }
    void SaveNamedProfile()
    {
        _menuOpen = true;
        try
        {
            using (var dialog = new Form())
            using (var label = new Label())
            using (var input = new TextBox())
            using (var hint = new Label())
            using (var field = new Panel())
            using (var ok = new ShellDialogButton())
            using (var cancel = new ShellDialogButton())
            {
                dialog.Text = L.T("保存组合"); dialog.Font = Font; dialog.AutoScaleMode = AutoScaleMode.None;
                dialog.ClientSize = new Size(D(440), D(226)); dialog.BackColor = ShellStyles.Surface; dialog.ForeColor = Theme.Ink;
                dialog.StartPosition = FormStartPosition.CenterParent; dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false; dialog.MaximizeBox = false;
                label.Text = L.T("给这组窗口起个名字");
                label.SetBounds(D(20), D(20), D(400), D(26));
                field.SetBounds(D(20), D(56), D(400), D(42)); field.BackColor = ShellStyles.Surface;
                input.BorderStyle = BorderStyle.None; input.BackColor = ShellStyles.Surface; input.ForeColor = Theme.Ink;
                input.SetBounds(D(12), D(11), D(376), D(24)); input.AccessibleName = L.T("组合名称");
                input.MaxLength = 48; input.Text = L.F("我的组合 {0}", DateTime.Now.ToString("MM-dd HH:mm"));
                field.Controls.Add(input);
                field.Paint += delegate(object sender, PaintEventArgs e)
                {
                    try
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var outline = Theme.Round(new RectangleF(1, 1, field.Width - 2, field.Height - 2), D(8)))
                        using (var pen = new Pen(input.Focused ? Theme.Brand : ShellStyles.Border)) e.Graphics.DrawPath(pen, outline);
                    }
                    catch (Exception ex) { PaintGuard.Failed(field, ex); }
                };
                input.GotFocus += delegate { field.Invalidate(); }; input.LostFocus += delegate { field.Invalidate(); };
                hint.Text = L.T("记住顺序、排版和 CLI 目录。\n手动添加的外部窗口需重新纳入。"); hint.ForeColor = Theme.Dim;
                hint.SetBounds(D(20), D(111), D(400), D(45));
                ok.Text = L.T("保存"); ok.Primary = true; ok.SetBounds(D(242), D(174), D(84), D(34)); ok.DialogResult = DialogResult.OK;
                cancel.Text = L.T("取消"); cancel.SetBounds(D(336), D(174), D(84), D(34)); cancel.DialogResult = DialogResult.Cancel;
                input.TabIndex = 0; ok.TabIndex = 1; cancel.TabIndex = 2;
                dialog.Controls.AddRange(new Control[] { label, field, hint, ok, cancel });
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) != DialogResult.OK || input.Text.Trim().Length == 0) return;
                var kinds = new List<AgentKind>();
                foreach (Slot s in _slots) if (!s.Custom) kinds.Add(s.Kind);
                if (kinds.Count == 0) { Toast(L.T("当前只有外部窗口，没有可保存的 Agent 会话")); return; }
                string path = Path.Combine(SlotProfiles.DirectoryPath, "preset-" + Guid.NewGuid().ToString("N") + ".xml");
                SlotProfiles.Save(path, input.Text.Trim(), kinds, _layoutIx);
                Toast(L.T("组合已保存；下次可从「恢复组合」选择"));
            }
        }
        catch (Exception ex) { Log.W("保存组合失败: " + ex.Message); Toast("组合保存失败"); }
        finally { _menuOpen = false; }
    }
    void RestoreProfile(string path)
    {
        try
        {
            string name; int layout;
            var kinds = SlotProfiles.Load(path, out name, out layout);
            var ids = new HashSet<string>(); foreach (AgentKind k in kinds) ids.Add(k.Id);
            // Keep matching live sessions attached. Only release slots absent
            // from the requested combination; never close or restart them.
            foreach (Slot s in _slots)
                if (!ids.Contains(s.Kind.Id) && s.Hwnd != IntPtr.Zero && !Embed.ReleaseToDesktop(s, false))
                { Toast(L.T("有窗口未能还原，组合切换已停止")); Relayout(true); return; }
            _loadingSlots = true;
            foreach (Slot s in _slots.ToArray()) if (!ids.Contains(s.Kind.Id)) RemoveSlot(s);
            var ordered = new List<Slot>();
            foreach (AgentKind k in kinds) ordered.Add(SlotOf(k) ?? AddSlot(k));
            _slots.Clear(); _slots.AddRange(ordered); _solo = null; _wantSolo = null;
            _layoutForN = _slots.Count; _layoutIx = layout;
            Attach(HostWins.Visible()); Relayout(true); SaveSolo();
            Toast(L.F("已恢复「{0}」；未打开的会话可点击启动", L.T(name)));
        }
        catch (Exception ex) { Log.W("恢复组合失败: " + ex.Message); Toast("组合读取失败，未能恢复"); }
        finally { _loadingSlots = false; SaveSlots(); }
    }
}
