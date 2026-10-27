using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Fangcun
{
    /// <summary>单个桌面图标的枚举结果。</summary>
    public class DesktopIcon
    {
        public int Index { get; set; }
        public string Text { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>
    /// 跨进程枚举桌面 SysListView32 中的真实图标（文本 + 坐标）。
    /// 这是“真实接管”的第一步：能读，下一步就能写（移动/reparent）。
    /// </summary>
    internal static class DesktopIconEnumerator
    {
        public static List<DesktopIcon> Enumerate()
        {
            var result = new List<DesktopIcon>();
            IntPtr listView = FindDesktopListView();
            if (listView == IntPtr.Zero) return result;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(listView, out pid);
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                false, pid);
            if (hProcess == IntPtr.Zero) return result;

            IntPtr pLvItem = IntPtr.Zero, pText = IntPtr.Zero, pPoint = IntPtr.Zero;
            try
            {
                int count = (int)NativeMethods.SendMessage(listView, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0) return result;

                const int textBufBytes = 512; // 256 个 WCHAR
                int lvItemSize = Marshal.SizeOf<NativeMethods.LVITEM>();
                pLvItem = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, lvItemSize, NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                pText = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, textBufBytes, NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                pPoint = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero, Marshal.SizeOf<NativeMethods.POINT>(), NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (pLvItem == IntPtr.Zero || pText == IntPtr.Zero || pPoint == IntPtr.Zero) return result;

                for (int i = 0; i < count; i++)
                {
                    // 读文本
                    var lvi = new NativeMethods.LVITEM
                    {
                        mask = NativeMethods.LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = pText,
                        cchTextMax = textBufBytes / 2,
                    };
                    int written;
                    NativeMethods.WriteProcessMemory(hProcess, pLvItem, ref lvi, lvItemSize, out written);
                    NativeMethods.SendMessage(listView, NativeMethods.LVM_GETITEMTEXTW, (IntPtr)i, pLvItem);

                    var sb = new StringBuilder(textBufBytes / 2);
                    int read;
                    NativeMethods.ReadProcessMemory(hProcess, pText, sb, textBufBytes, out read);

                    // 读坐标
                    NativeMethods.SendMessage(listView, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)i, pPoint);
                    var pt = new NativeMethods.POINT();
                    NativeMethods.ReadProcessMemory(hProcess, pPoint, ref pt, Marshal.SizeOf<NativeMethods.POINT>(), out read);

                    result.Add(new DesktopIcon { Index = i, Text = sb.ToString(), X = pt.X, Y = pt.Y });
                }
            }
            finally
            {
                if (pLvItem != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, pLvItem, 0, NativeMethods.MEM_RELEASE);
                if (pText != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, pText, 0, NativeMethods.MEM_RELEASE);
                if (pPoint != IntPtr.Zero) NativeMethods.VirtualFreeEx(hProcess, pPoint, 0, NativeMethods.MEM_RELEASE);
                NativeMethods.CloseHandle(hProcess);
            }
            return result;
        }

        /// <summary>
        /// 定位桌面图标所在的 SysListView32。
        /// 兼容两种层级：Progman→SHELLDLL_DefView→SysListView32，
        /// 或 WorkerW→SHELLDLL_DefView→SysListView32（Win10/11 常见）。
        /// </summary>
        private static IntPtr FindDesktopListView()
        {
            IntPtr progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr list = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (list != IntPtr.Zero) return list;
                }
            }

            IntPtr worker = IntPtr.Zero;
            while ((worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                IntPtr defView = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr list = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (list != IntPtr.Zero) return list;
                }
            }
            return IntPtr.Zero;
        }
    }
}
