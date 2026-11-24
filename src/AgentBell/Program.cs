using System.ComponentModel;

namespace AgentBell;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\AgentBell", out bool first);
        if (!first)
            return 0;

        var log = new AgentLog();
        try
        {
            var app = new OverlayApp(log);
            return app.Run();
        }
        catch (Exception error)
        {
            log.Write("Agent Bell stopped due to an error: " + error);
            return 1;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    internal static Win32Exception LastError(string operation) =>
        new(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), operation);
}
