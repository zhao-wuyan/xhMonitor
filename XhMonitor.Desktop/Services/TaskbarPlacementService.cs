using System.Runtime.InteropServices;
using System.Windows;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public sealed class TaskbarPlacementService : ITaskbarPlacementService
{
    private const int HorizontalMargin = 8;
    private const int VerticalMargin = 6;
    private const int FallbackTrayReserveWidth = 220;
    private const int FallbackTrayReserveHeight = 120;

    public bool TryGetPlacement(double windowWidth, double windowHeight, out double left, out double top, out EdgeDockSide dockSide)
    {
        left = 0;
        top = 0;
        dockSide = EdgeDockSide.Bottom;

        var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(taskbarHwnd, out var taskbarRect))
        {
            return false;
        }

        var hasTray = TryGetTrayRect(taskbarHwnd, out var trayRect);
        var hasTaskList = TryGetTaskListRect(taskbarHwnd, out var taskListRect);
        var edge = DetectTaskbarEdge(taskbarRect);
        dockSide = edge switch
        {
            TaskbarEdge.Left => EdgeDockSide.Left,
            TaskbarEdge.Top => EdgeDockSide.Top,
            TaskbarEdge.Right => EdgeDockSide.Right,
            _ => EdgeDockSide.Bottom
        };

        switch (edge)
        {
            case TaskbarEdge.Bottom:
            case TaskbarEdge.Top:
                top = taskbarRect.Top + ((taskbarRect.Height - windowHeight) / 2d);
                left = hasTray
                    ? trayRect.Left - windowWidth - HorizontalMargin
                    : taskbarRect.Right - windowWidth - FallbackTrayReserveWidth;
                var minLeft = taskbarRect.Left + HorizontalMargin;
                if (hasTaskList)
                {
                    minLeft = Math.Max(minLeft, taskListRect.Right + HorizontalMargin);
                }

                var maxLeft = taskbarRect.Right - windowWidth - HorizontalMargin;
                left = minLeft <= maxLeft
                    ? Math.Clamp(left, minLeft, maxLeft)
                    : maxLeft;
                break;

            case TaskbarEdge.Left:
            case TaskbarEdge.Right:
                left = taskbarRect.Left + ((taskbarRect.Width - windowWidth) / 2d);
                top = hasTray
                    ? trayRect.Top - windowHeight - VerticalMargin
                    : taskbarRect.Bottom - windowHeight - FallbackTrayReserveHeight;
                var minTop = taskbarRect.Top + VerticalMargin;
                if (hasTaskList)
                {
                    minTop = Math.Max(minTop, taskListRect.Bottom + VerticalMargin);
                }

                var maxTop = taskbarRect.Bottom - windowHeight - VerticalMargin;
                top = minTop <= maxTop
                    ? Math.Clamp(top, minTop, maxTop)
                    : maxTop;
                break;

            default:
                return false;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        left = Math.Clamp(left, virtualLeft, virtualRight - windowWidth);
        top = Math.Clamp(top, virtualTop, virtualBottom - windowHeight);

        return true;
    }

    private static TaskbarEdge DetectTaskbarEdge(RECT taskbarRect)
    {
        if (taskbarRect.Width >= taskbarRect.Height)
        {
            var virtualTop = SystemParameters.VirtualScreenTop;
            var topDistance = Math.Abs(taskbarRect.Top - virtualTop);
            return topDistance <= 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var leftDistance = Math.Abs(taskbarRect.Left - virtualLeft);
        return leftDistance <= 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static bool TryGetTrayRect(IntPtr taskbarHwnd, out RECT trayRect)
    {
        trayRect = default;

        // Win10/Win11 上 TrayNotifyWnd 可能在不同层级，递归查找更稳妥。
        var tray = FindDescendantByClass(taskbarHwnd, "TrayNotifyWnd");

        if (tray == IntPtr.Zero)
        {
            return false;
        }

        return GetWindowRect(tray, out trayRect);
    }

    private static bool TryGetTaskListRect(IntPtr taskbarHwnd, out RECT taskListRect)
    {
        taskListRect = default;

        var taskList = FindDescendantByClass(taskbarHwnd, "MSTaskListWClass");
        if (taskList == IntPtr.Zero)
        {
            return false;
        }

        return GetWindowRect(taskList, out taskListRect);
    }

    private static IntPtr FindDescendantByClass(IntPtr parentHwnd, string className)
    {
        // 先找当前层级，命中后立即返回。
        var direct = FindWindowEx(parentHwnd, IntPtr.Zero, className, null);
        if (direct != IntPtr.Zero)
        {
            return direct;
        }

        // 再递归遍历所有子节点，兼容 Win11 多层容器结构。
        var child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(parentHwnd, child, null, null);
            if (child == IntPtr.Zero)
            {
                break;
            }

            var nested = FindDescendantByClass(child, className);
            if (nested != IntPtr.Zero)
            {
                return nested;
            }
        }

        return IntPtr.Zero;
    }

    private enum TaskbarEdge
    {
        Unknown = 0,
        Left = 1,
        Top = 2,
        Right = 3,
        Bottom = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
