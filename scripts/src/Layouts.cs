// 【拆分】几格几套排版的几何计算。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;

internal static class Layouts
{
    public static int VariantCount(int n)
    {
        return Names(n).Length;
    }

    public static string NameAt(int n, int index)
    {
        return L.T(KeyAt(n, index));
    }

    // 不翻译的原名：写进 status.json 给别的程序读，不能跟着界面语言变。
    public static string KeyAt(int n, int index)
    {
        string[] names = Names(n);
        if (names.Length == 0) return "";
        return names[Wrap(index, names.Length)];
    }

    public static Rectangle[] Compute(int n, int index, Rectangle box, int gap)
    {
        if (n <= 0) return new Rectangle[0];
        int i = Wrap(index, VariantCount(n));
        if (n == 1) return Fill(box, gap, 1);
        if (n == 2) return i == 0 ? Split2(box, gap, true) : Split2(box, gap, false);
        if (n == 3)
        {
            if (i == 0) return Equal(n, 2, 2, box, gap, true);
            if (i == 1) return Equal(n, 1, 3, box, gap, false);
            if (i == 2) return Equal(n, 3, 1, box, gap, false);
            if (i == 3) return FocusLeft(box, gap, 2);
            return FocusTop(box, gap, 2);
        }
        if (n == 4)
        {
            if (i == 0) return Equal(n, 2, 2, box, gap, false);
            if (i == 1) return Equal(n, 1, 4, box, gap, false);
            if (i == 2) return Equal(n, 4, 1, box, gap, false);
            if (i == 3) return FocusLeft(box, gap, 3);
            return FocusTop(box, gap, 3);
        }
        if (n == 5)
        {
            if (i == 0) return Rows(box, gap, new int[] { 2, 3 });
            if (i == 1) return Rows(box, gap, new int[] { 3, 2 });
            if (i == 2) return Cols(box, gap, new int[] { 2, 3 });
            return Equal(n, 2, 3, box, gap, true);
        }
        if (n == 6)
        {
            if (i == 0) return Equal(n, 2, 3, box, gap, false);
            if (i == 1) return Equal(n, 3, 2, box, gap, false);
            return Cols(box, gap, new int[] { 2, 4 });
        }
        if (i == 0) return AutoEqual(n, box, gap);
        if (i == 1) return Equal(n, (n + 3) / 4, Math.Min(n, 4), box, gap, true);
        return Equal(n, n, 1, box, gap, false);
    }

    static string[] Names(int n)
    {
        if (n <= 0) return new string[0];
        if (n == 1) return new string[] { "整面" };
        if (n == 2) return new string[] { "左右", "上下" };
        if (n == 3) return new string[] { "田字空一格", "三列", "三行", "左主右二", "上主下二" };
        if (n == 4) return new string[] { "田字", "四列", "四行", "左主右三", "上主下三" };
        if (n == 5) return new string[] { "上二下三", "上三下二", "左二右三", "六宫空一格" };
        if (n == 6) return new string[] { "两行三列", "三行两列", "左二右四" };
        return new string[] { "近方形", "偏横", "偏竖" };
    }

    static int Wrap(int i, int n)
    {
        if (n <= 0) return 0;
        i %= n;
        if (i < 0) i += n;
        return i;
    }

    static Rectangle[] Fill(Rectangle box, int gap, int n)
    {
        var a = new Rectangle[n];
        a[0] = Inset(box, gap);
        return a;
    }

    static Rectangle Inset(Rectangle box, int gap)
    {
        return new Rectangle(box.X + gap, box.Y + gap,
            Math.Max(80, box.Width - 2 * gap), Math.Max(80, box.Height - 2 * gap));
    }

    static Rectangle[] Split2(Rectangle box, int gap, bool horizontal)
    {
        Rectangle inner = Inset(box, gap);
        if (horizontal)
        {
            int w = (inner.Width - gap) / 2;
            return new Rectangle[] {
                new Rectangle(inner.X, inner.Y, w, inner.Height),
                new Rectangle(inner.X + w + gap, inner.Y, inner.Width - w - gap, inner.Height)
            };
        }
        int h = (inner.Height - gap) / 2;
        return new Rectangle[] {
            new Rectangle(inner.X, inner.Y, inner.Width, h),
            new Rectangle(inner.X, inner.Y + h + gap, inner.Width, inner.Height - h - gap)
        };
    }

    static Rectangle[] Equal(int n, int rows, int cols, Rectangle box, int gap, bool centerLast)
    {
        if (rows < 1) rows = 1;
        if (cols < 1) cols = 1;
        int cw = Math.Max(80, (box.Width - gap * (cols + 1)) / cols);
        int ch = Math.Max(80, (box.Height - gap * (rows + 1)) / rows);
        var a = new Rectangle[n];
        int i = 0;
        for (int r = 0; r < rows && i < n; r++)
        {
            int inRow = n - i;
            if (inRow > cols) inRow = cols;
            int rowW = inRow * cw + (inRow + 1) * gap;
            int x0 = (centerLast && inRow < cols)
                ? box.X + (box.Width - rowW) / 2 + gap
                : box.X + gap;
            int y = box.Y + gap + r * (ch + gap);
            for (int c = 0; c < inRow; c++)
                a[i++] = new Rectangle(x0 + c * (cw + gap), y, cw, ch);
        }
        return a;
    }

    static Rectangle[] AutoEqual(int n, Rectangle box, int gap)
    {
        int bestC = n, bestR = 1;
        double best = double.MaxValue;
        for (int c = 1; c <= n; c++)
        {
            int r = (n + c - 1) / c;
            int cw = (box.Width - gap * (c + 1)) / c;
            int ch = (box.Height - gap * (r + 1)) / r;
            if (cw < 80 || ch < 80) continue;
            double score = Math.Abs(Math.Log((double)cw / (double)ch)) + 0.08 * (r * c - n);
            if (score < best) { best = score; bestC = c; bestR = r; }
        }
        return Equal(n, bestR, bestC, box, gap, true);
    }

    static Rectangle[] FocusLeft(Rectangle box, int gap, int right)
    {
        Rectangle inner = Inset(box, gap);
        int leftW = (inner.Width - gap) / 2;
        int rightW = inner.Width - gap - leftW;
        var a = new Rectangle[1 + right];
        a[0] = new Rectangle(inner.X, inner.Y, leftW, inner.Height);
        int rh = (inner.Height - gap * (right - 1)) / right;
        for (int i = 0; i < right; i++)
            a[1 + i] = new Rectangle(inner.X + leftW + gap, inner.Y + i * (rh + gap), rightW, rh);
        return a;
    }

    static Rectangle[] FocusTop(Rectangle box, int gap, int bottom)
    {
        Rectangle inner = Inset(box, gap);
        int topH = (inner.Height - gap) / 2;
        int botH = inner.Height - gap - topH;
        var a = new Rectangle[1 + bottom];
        a[0] = new Rectangle(inner.X, inner.Y, inner.Width, topH);
        int cw = (inner.Width - gap * (bottom - 1)) / bottom;
        for (int i = 0; i < bottom; i++)
            a[1 + i] = new Rectangle(inner.X + i * (cw + gap), inner.Y + topH + gap, cw, botH);
        return a;
    }

    static Rectangle[] Rows(Rectangle box, int gap, int[] counts)
    {
        int rows = counts.Length;
        int ch = Math.Max(80, (box.Height - gap * (rows + 1)) / rows);
        var list = new List<Rectangle>();
        int y = box.Y + gap;
        for (int r = 0; r < rows; r++)
        {
            int cols = counts[r];
            int cw = Math.Max(80, (box.Width - gap * (cols + 1)) / cols);
            int x = box.X + gap;
            for (int c = 0; c < cols; c++)
            {
                list.Add(new Rectangle(x, y, cw, ch));
                x += cw + gap;
            }
            y += ch + gap;
        }
        return list.ToArray();
    }

    static Rectangle[] Cols(Rectangle box, int gap, int[] counts)
    {
        int cols = counts.Length;
        int cw = Math.Max(80, (box.Width - gap * (cols + 1)) / cols);
        var list = new List<Rectangle>();
        int x = box.X + gap;
        for (int c = 0; c < cols; c++)
        {
            int rows = counts[c];
            int ch = Math.Max(80, (box.Height - gap * (rows + 1)) / rows);
            int y = box.Y + gap;
            for (int r = 0; r < rows; r++)
            {
                list.Add(new Rectangle(x, y, cw, ch));
                y += ch + gap;
            }
            x += cw + gap;
        }
        return list.ToArray();
    }
}
