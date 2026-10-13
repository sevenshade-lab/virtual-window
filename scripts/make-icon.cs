// 生成 AgentHost.ico：暖白圆角底 + 2×2 四张小卡片（一张陶土色，代表「当前格子」）。
// 用法：csc make-icon.cs && make-icon.exe AgentHost.ico
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var gp = new GraphicsPath();
        float d = rad * 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    static byte[] Frame(int n)
    {
        using (var bm = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bm))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float u = n / 32f;
            using (var bg = Round(new RectangleF(1 * u, 1 * u, 30 * u, 30 * u), 7 * u))
            using (var br = new SolidBrush(Color.FromArgb(255, 245, 240, 232)))
            using (var pen = new Pen(Color.FromArgb(60, 120, 90, 70), Math.Max(1f, u)))
            {
                g.FillPath(br, bg);
                g.DrawPath(pen, bg);
            }
            Color brand = Color.FromArgb(255, 201, 100, 66);
            Color card = Color.FromArgb(255, 255, 255, 255);
            Color edge = Color.FromArgb(90, 120, 90, 70);
            float gap = 2.5f * u, m = 6f * u, w = (32 * u - 2 * m - gap) / 2f;
            for (int i = 0; i < 4; i++)
            {
                float x = m + (i % 2) * (w + gap), y = m + (i / 2) * (w + gap);
                using (var gp = Round(new RectangleF(x, y, w, w), 2.2f * u))
                using (var b = new SolidBrush(i == 0 ? brand : card))
                using (var p = new Pen(i == 0 ? brand : edge, Math.Max(1f, 0.8f * u)))
                {
                    g.FillPath(b, gp);
                    g.DrawPath(p, gp);
                }
            }
            using (var ms = new MemoryStream())
            {
                bm.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static void Main(string[] a)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var frames = new List<byte[]>();
        foreach (int s in sizes) frames.Add(Frame(s));
        using (var fs = File.Create(a.Length > 0 ? a[0] : "AgentHost.ico"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int off = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(off);
                off += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
    }
}
