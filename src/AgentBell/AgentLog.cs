namespace AgentBell;

internal sealed class AgentLog
{
    private readonly object gate = new();

    internal string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBell", "agent-bell.log");

    internal void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
