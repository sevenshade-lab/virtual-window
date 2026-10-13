// 【拆分】自绘标题栏的系统菜单。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Windows.Forms;

internal static class WindowMenuCommands
{
    internal static IntPtr Create(FormWindowState state)
    {
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return menu;
        bool normal = state == FormWindowState.Normal;
        bool ok = Native.AppendMenu(menu, normal ? 1u : 0u, (UIntPtr)0xF120, L.T("还原(&R)"));
        ok &= Native.AppendMenu(menu, normal ? 0u : 1u, (UIntPtr)0xF010, L.T("移动(&M)"));
        ok &= Native.AppendMenu(menu, normal ? 0u : 1u, (UIntPtr)0xF000, L.T("大小(&S)"));
        ok &= Native.AppendMenu(menu, state == FormWindowState.Minimized ? 1u : 0u, (UIntPtr)0xF020, L.T("最小化(&N)"));
        ok &= Native.AppendMenu(menu, state == FormWindowState.Maximized ? 1u : 0u, (UIntPtr)0xF030, L.T("最大化(&X)"));
        ok &= Native.AppendMenu(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        ok &= Native.AppendMenu(menu, 0, (UIntPtr)0xF060, L.T("关闭(&C)\tAlt+F4"));
        if (!ok) { Native.DestroyMenu(menu); return IntPtr.Zero; }
        return menu;
    }
}
