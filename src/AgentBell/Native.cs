using System.Runtime.InteropServices;

namespace AgentBell;

internal static class Native
{
    internal const uint Paint = 0x000f;
    internal const uint Close = 0x0010;
    internal const uint Destroy = 0x0002;
    internal const uint DisplayChange = 0x007e;
    internal const uint MouseActivate = 0x0021;
    internal const uint Command = 0x0111;
    internal const uint Timer = 0x0113;
    internal const uint RightButtonUp = 0x0205;
    internal const uint LeftButtonUp = 0x0202;
    internal const uint App = 0x8000;

    internal const uint Popup = 0x80000000;
    internal const uint ToolWindow = 0x00000080;
    internal const uint NoActivate = 0x08000000;
    internal const uint ShowWithoutActivation = 0x0050;
    internal const uint MenuChecked = 0x0008;
    internal const uint MenuRightButton = 0x0002;
    internal static readonly nint Topmost = (nint)(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInput
    {
        internal uint Size;
        internal uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Style;
        internal nint Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PaintInfo
    {
        internal nint Dc;
        internal int Erase;
        internal Rect Bounds;
        internal int Restore;
        internal int Update;
        internal fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Bounds;
        internal Rect WorkArea;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIcon
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool MonitorProc(nint monitor, nint dc, nint bounds, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint hwnd, uint message, nuint wparam, nint lparam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    internal static extern bool PostMessageW(nint hwnd, uint message, nuint wparam, nint lparam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(nint hwnd, nuint timer, uint milliseconds, nint callback);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(nint hwnd, nuint timer);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LastInput input);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("user32.dll")]
    internal static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint BeginPaint(nint hwnd, out PaintInfo paint);

    [DllImport("user32.dll")]
    internal static extern bool EndPaint(nint hwnd, ref PaintInfo paint);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern nint LoadIconW(nint instance, nint name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NotifyIcon data);

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);

    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(nint menu);

}
