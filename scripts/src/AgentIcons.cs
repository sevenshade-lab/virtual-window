// 「＋ 新会话」面板上每家的图标。没在跑的也要有图标，所以不能只靠窗口的 WM_GETICON：
//   安装版 → 从 exe 取；商店版（Claude / Codex）→ 走外壳 shell:AppsFolder\<AUMID> 取；
//   只有开始菜单快捷方式的 → 从 .lnk 取；都没有 → 首字母圆形头像。取一次缓存。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

internal static class AgentIcons
{
    static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

    public static Bitmap Get(AgentKind k, int px)
    {
        string key = k.Id + "@" + px;
        Bitmap b;
        if (_cache.TryGetValue(key, out b)) return b;
        b = null;
        try
        {
            if (!string.IsNullOrEmpty(k.AppsFolder)) b = FromShell(@"shell:AppsFolder\" + k.AppsFolder, px);
            if (b == null)
            {
                string exe = Catalog.DesktopExe(k);
                if (exe != null) b = FromFile(exe, px);
            }
            if (b == null && k.Shortcuts != null && k.Shortcuts.Length > 0)
            {
                string lnk = Launch.FindShortcut(k.Shortcuts);
                if (lnk != null) b = FromShell(lnk, px) ?? FromFile(lnk, px);
            }
            if (b == null && Catalog.HasCli(k))
            {
                string cli = Catalog.CliPath(k);
                if (cli != null && cli.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b = FromFile(cli, px);
            }
        }
        catch { b = null; }
        if (b == null) b = Letter(k.Name, px);
        _cache[key] = b;
        return b;
    }

    static Bitmap FromFile(string path, int px)
    {
        if (!File.Exists(path)) return null;
        using (Icon ic = Icon.ExtractAssociatedIcon(path))
        {
            if (ic == null) return null;
            var bmp = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (Bitmap src = ic.ToBitmap()) g.DrawImage(src, new Rectangle(0, 0, px, px));
            }
            return bmp;
        }
    }

    // IShellItemImageFactory：商店应用、快捷方式都能拿到跟开始菜单里一样的图标
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static Bitmap FromShell(string parsingName, int px)
    {
        IShellItemImageFactory f;
        try { SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f); }
        catch { return null; }
        IntPtr hbm;
        // SIIGBF_ICONONLY(0x4)：要图标，不要缩略图
        if (f.GetImage(new SIZE { cx = px, cy = px }, 0x4, out hbm) != 0 || hbm == IntPtr.Zero) return null;
        try { return FromHBitmapWithAlpha(hbm); }
        finally { DeleteObject(hbm); }
    }

    // Image.FromHbitmap 会丢掉 alpha（透明处变黑），自己按 32 位位图拷出来
    static Bitmap FromHBitmapWithAlpha(IntPtr hbm)
    {
        using (Bitmap tmp = Image.FromHbitmap(hbm))
        {
            var rect = new Rectangle(0, 0, tmp.Width, tmp.Height);
            var src = tmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, tmp.PixelFormat);
            try
            {
                if (tmp.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppRgb &&
                    tmp.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                    return new Bitmap(tmp);
                var dst = new Bitmap(tmp.Width, tmp.Height, src.Stride, System.Drawing.Imaging.PixelFormat.Format32bppArgb, src.Scan0);
                return new Bitmap(dst);   // 拷一份，脱离 LockBits 的内存
            }
            finally { tmp.UnlockBits(src); }
        }
    }

    static Bitmap Letter(string name, int px)
    {
        var bmp = new Bitmap(px, px);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int h = 0;
            foreach (char c in name ?? "") h = h * 31 + c;
            Color bg = Color.FromArgb(255, 120 + Math.Abs(h % 90), 110 + Math.Abs((h / 7) % 80), 150 + Math.Abs((h / 13) % 70));
            using (var br = new SolidBrush(bg)) g.FillEllipse(br, 0, 0, px - 1, px - 1);
            string s = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            using (var f = new Font("Segoe UI Semibold", px * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(s, f, Brushes.White, new RectangleF(0, 0, px, px + 1), sf);
        }
        return bmp;
    }
}
