using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentBell;

internal sealed class OverlayApp(AgentLog log)
{
    private const uint CompletedMessage = Native.App + 1;
    private const uint TrayMessage = Native.App + 2;
    private const nuint MouseTimer = 1;
    private const nuint PreviewCommand = 1;
    private const nuint StartupCommand = 2;
    private const nuint ExitCommand = 3;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisplayCheckInterval = TimeSpan.FromSeconds(1);

    private readonly SunshineTarget sunshine = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "config", "sunshine.log"));
    private Native.WindowProc? procedure;
    private nint window;
    private uint taskbarCreated;
    private Native.NotifyIcon tray;
    private Native.Point anchor;
    private uint inputAnchor;
    private readonly Stopwatch alertTimer = new();
    private readonly Stopwatch displayTimer = new();
    private string? display;
    private Direct2DArtwork? artwork;
    private bool tracking;

    internal int Run()
    {
        nint instance = Native.GetModuleHandleW(null);
        procedure = OnMessage;
        var windowClass = new Native.WindowClass
        {
            Instance = instance,
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            ClassName = "AgentBell.Overlay"
        };
        if (Native.RegisterClassW(ref windowClass) == 0)
            throw Program.LastError("RegisterClassW");

        window = Native.CreateWindowExW(Native.ToolWindow | Native.NoActivate, windowClass.ClassName,
            "Agent Bell", Native.Popup, 0, 0, 1, 1, 0, 0, instance, 0);
        if (window == 0)
            throw Program.LastError("CreateWindowExW");

        taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        tray = new Native.NotifyIcon
        {
            Size = (uint)Marshal.SizeOf<Native.NotifyIcon>(),
            Window = window,
            Id = 1,
            Flags = 0x0007,
            Callback = TrayMessage,
            Icon = Native.LoadIconW(0, 32512),
            Tip = "Agent Bell",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        AddTrayIcon();

        string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        using var cancellation = new CancellationTokenSource();
        var watch = new CodexRollouts(Path.Combine(home, "sessions"), log,
            () => Native.PostMessageW(window, CompletedMessage, 0, 0));
        var worker = Task.Run(() =>
        {
            try { watch.Run(cancellation.Token); }
            catch (Exception error) { log.Write("Codex watcher stopped: " + error); }
        });

        log.Write("Agent Bell started.");
        try
        {
            int status;
            while ((status = Native.GetMessageW(out var message, 0, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }

            if (status < 0)
                throw Program.LastError("GetMessageW");

            return 0;
        }
        finally
        {
            cancellation.Cancel();
            worker.GetAwaiter().GetResult();
            GC.KeepAlive(procedure);
        }
    }

    private nint OnMessage(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        if (taskbarCreated != 0 && message == taskbarCreated)
        {
            AddTrayIcon();
            return 0;
        }

        switch (message)
        {
            case CompletedMessage:
                BeginAlert(preview: false);
                return 0;
            case TrayMessage:
                if ((uint)lparam is Native.RightButtonUp or Native.LeftButtonUp)
                    ShowTrayMenu();
                return 0;
            case Native.Command:
                RunTrayCommand(wparam & 0xffff);
                return 0;
            case Native.Timer when wparam == MouseTimer:
                CheckInput();
                return 0;
            case Native.DisplayChange:
                if (display is not null)
                    EndAlert("display changed");
                break;
            case Native.MouseActivate:
                return 3;
            case Native.Paint:
                Paint(hwnd);
                return 0;
            case Native.Close:
                Native.DestroyWindow(hwnd);
                return 0;
            case Native.Destroy:
                EndAlert("exit");
                Native.Shell_NotifyIconW(2, ref tray);
                Native.PostQuitMessage(0);
                return 0;
        }

        return Native.DefWindowProcW(hwnd, message, wparam, lparam);
    }

    private void BeginAlert(bool preview)
    {
        if (tracking || !ReadInput(out anchor, out inputAnchor))
            return;

        if (Native.SetTimer(window, MouseTimer, 100, 0) == 0)
        {
            log.Write("Could not start the input timer.");
            return;
        }

        tracking = true;
        alertTimer.Restart();
        log.Write(preview ? "Preview requested." : "Alert armed.");
        if (preview)
            ShowAlert();
    }

    private static bool ReadInput(out Native.Point point, out uint timestamp)
    {
        var input = new Native.LastInput { Size = (uint)Marshal.SizeOf<Native.LastInput>() };
        bool hasInput = Native.GetLastInputInfo(ref input);
        timestamp = input.Time;
        return Native.GetCursorPos(out point) && hasInput;
    }

    private void CheckInput()
    {
        if (!tracking)
            return;

        if (!ReadInput(out var point, out uint timestamp))
        {
            EndAlert("input unavailable");
            return;
        }

        if (point.X != anchor.X || point.Y != anchor.Y)
        {
            EndAlert("cursor moved");
            return;
        }

        if (timestamp != inputAnchor)
        {
            EndAlert("keyboard or mouse input");
            return;
        }

        if (display is null)
        {
            if (alertTimer.Elapsed >= Delay)
                ShowAlert();
        }
        else if (displayTimer.Elapsed >= DisplayCheckInterval)
        {
            displayTimer.Restart();
            if (!string.Equals(display, sunshine.ReadDisplay(), StringComparison.OrdinalIgnoreCase) ||
                FindDisplay(display) is null)
                EndAlert("Sunshine capture target lost");
        }
    }

    private void ShowAlert()
    {
        string? selected = sunshine.ReadDisplay();
        var bounds = selected is not null ? FindDisplay(selected) : null;
        if (bounds is not { Width: > 0, Height: > 0 })
        {
            EndAlert("no single Sunshine capture display");
            return;
        }

        try { artwork = Direct2DArtwork.Load(); }
        catch (Exception error) when (error is IOException or COMException or System.Xml.XmlException)
        {
            log.Write("Could not load the notification artwork: " + error.Message);
            EndAlert("artwork unavailable");
            return;
        }

        var rect = bounds.Value;
        if (!Native.SetWindowPos(window, Native.Topmost, rect.Left, rect.Top, rect.Width, rect.Height,
                Native.ShowWithoutActivation))
        {
            EndAlert("window could not be shown");
            return;
        }

        display = selected;
        displayTimer.Restart();
        Native.InvalidateRect(window, 0, false);
        Native.UpdateWindow(window);
        if (display is null)
            return;
        log.Write($"Alert shown display={display} bounds={rect.Left},{rect.Top},{rect.Width},{rect.Height} elapsed_ms={alertTimer.Elapsed.TotalMilliseconds:F0}.");
    }

    private void EndAlert(string reason)
    {
        if (!tracking)
            return;

        Native.KillTimer(window, MouseTimer);
        if (display is not null)
        {
            Native.ShowWindow(window, 0);
            log.Write($"Alert hidden reason={reason}.");
        }
        else
        {
            log.Write($"Alert canceled reason={reason} elapsed_ms={alertTimer.Elapsed.TotalMilliseconds:F0}.");
        }

        tracking = false;
        display = null;
        artwork?.Dispose();
        artwork = null;
    }

    private static Native.Rect? FindDisplay(string device)
    {
        Native.Rect? result = null;
        Native.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = string.Empty };
            if (!Native.GetMonitorInfoW(monitor, ref info) ||
                !string.Equals(device, info.Device, StringComparison.OrdinalIgnoreCase))
                return true;

            result = info.Bounds;
            return false;
        }, 0);
        return result;
    }

    private void AddTrayIcon()
    {
        if (!Native.Shell_NotifyIconW(0, ref tray))
            log.Write("Windows did not add the tray icon.");
    }

    private void ShowTrayMenu()
    {
        if (!Native.GetCursorPos(out var point))
            return;

        nint menu = Native.CreatePopupMenu();
        if (menu == 0)
            return;

        try
        {
            Native.AppendMenuW(menu, 0, PreviewCommand, "预览提醒");
            Native.AppendMenuW(menu, ReadStartupState() ? Native.MenuChecked : 0, StartupCommand, "开机启动");
            Native.AppendMenuW(menu, 0, ExitCommand, "退出");
            Native.SetForegroundWindow(window);
            Native.TrackPopupMenu(menu, Native.MenuRightButton, point.X, point.Y, 0, window, 0);
            Native.PostMessageW(window, 0, 0, 0);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private bool ReadStartupState()
    {
        try { return StartupRegistration.IsEnabled(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            log.Write("Could not read Windows startup registration: " + error.Message);
            return false;
        }
    }

    private void RunTrayCommand(nuint command)
    {
        switch (command)
        {
            case PreviewCommand:
                BeginAlert(preview: true);
                break;
            case StartupCommand:
                try
                {
                    bool enabled = StartupRegistration.Toggle();
                    log.Write(enabled ? "Windows startup enabled." : "Windows startup disabled.");
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
                {
                    log.Write("Could not change Windows startup registration: " + error.Message);
                }
                break;
            case ExitCommand:
                Native.DestroyWindow(window);
                break;
        }
    }

    private void Paint(nint hwnd)
    {
        Native.BeginPaint(hwnd, out var paint);
        try
        {
            if (artwork is not null && Native.GetClientRect(hwnd, out var bounds))
                artwork.Draw(hwnd, bounds.Width, bounds.Height);
        }
        catch (COMException error)
        {
            log.Write("Direct2D could not draw the notification: " + error.Message);
            EndAlert("Direct2D rendering failed");
        }
        finally
        {
            Native.EndPaint(hwnd, ref paint);
        }
    }

}
