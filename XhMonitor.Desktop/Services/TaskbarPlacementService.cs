using System.Runtime.InteropServices;
using System.Windows;

namespace XhMonitor.Desktop.Services;

public sealed class TaskbarPlacementService : ITaskbarPlacementService
{
    private const int HorizontalMargin = 8;
    private const int VerticalMargin = 6;
    private const int FallbackTrayReserveWidth = 220;
    private const int FallbackTrayReserveHeight = 120;

    public bool TryGetPlacement(double windowWidth, double windowHeight, out double left, out double top)
    {
        left = 0;
        top = 0;

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
        var edge = DetectTaskbarEdge(taskbarRect);

        switch (edge)
        {
            case TaskbarEdge.Bottom:
            case TaskbarEdge.Top:
                top = taskbarRect.Top + ((taskbarRect.Height - windowHeight) / 2d);
                left = hasTray
                    ? trayRect.Left - windowWidth - HorizontalMargin
                    : taskbarRect.Right - windowWidth - FallbackTrayReserveWidth;
                left = Math.Clamp(left, taskbarRect.Left + HorizontalMargin, taskbarRect.Right - windowWidth - HorizontalMargin);
                break;

            case TaskbarEdge.Left:
            case TaskbarEdge.Right:
                left = taskbarRect.Left + ((taskbarRect.Width - windowWidth) / 2d);
                top = hasTray
                    ? trayRect.Top - windowHeight - VerticalMargin
                    : taskbarRect.Bottom - windowHeight - FallbackTrayReserveHeight;
                top = Math.Clamp(top, taskbarRect.Top + VerticalMargin, taskbarRect.Bottom - windowHeight - VerticalMargin);
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

        // Win10/Win11 经典路径
        var tray = FindWindowEx(taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero)
        {
            // 部分系统中 TrayNotifyWnd 可能嵌套在子容器中
            var rebar = FindWindowEx(taskbarHwnd, IntPtr.Zero, "ReBarWindow32", null);
            if (rebar != IntPtr.Zero)
            {
                tray = FindWindowEx(rebar, IntPtr.Zero, "TrayNotifyWnd", null);
            }
        }

        if (tray == IntPtr.Zero)
        {
            return false;
        }

        return GetWindowRect(tray, out trayRect);
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
