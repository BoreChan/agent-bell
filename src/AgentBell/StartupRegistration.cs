using Microsoft.Win32;

namespace AgentBell;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentBell";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return IsCurrentCommand(key?.GetValue(ValueName));
    }

    internal static bool Toggle()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsCurrentCommand(key.GetValue(ValueName)))
        {
            key.DeleteValue(ValueName);
            return false;
        }

        key.SetValue(ValueName, Command(), RegistryValueKind.String);
        return true;
    }

    private static bool IsCurrentCommand(object? value) =>
        value is string command && command.Equals(Command(), StringComparison.OrdinalIgnoreCase);

    private static string Command()
    {
        string executable = typeof(Program).Assembly.Location;
        return $"\"{executable}\"";
    }
}
