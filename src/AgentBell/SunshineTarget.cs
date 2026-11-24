namespace AgentBell;

internal sealed class SunshineTarget(string path)
{
    private const string Started = "New streaming session started [active sessions: ";
    private const string DisplaySelected = "Using client-specified display: ";
    private FileTail? tail;
    private int activeSessions;
    private bool connected;
    private string? display;
    private DateTime creationTime;
    private long lastLength;

    internal string? ReadDisplay()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                Reset();
                return null;
            }

            if (tail is null || file.CreationTimeUtc != creationTime || file.Length < lastLength)
            {
                Reset();
                creationTime = file.CreationTimeUtc;
                tail = new FileTail(path, skipExisting: false);
            }

            tail.Read(path, Apply);
            lastLength = file.Length;
            return ConnectedDisplay;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal string? ConnectedDisplay => connected && activeSessions == 1 ? display : null;

    internal void Apply(string line)
    {
        if (line.IndexOf("streaming session", StringComparison.Ordinal) < 0 &&
            line.IndexOf("CLIENT ", StringComparison.Ordinal) < 0 &&
            line.IndexOf("client-specified display", StringComparison.Ordinal) < 0)
            return;

        string text = line.TrimEnd();
        int start = text.IndexOf(Started, StringComparison.Ordinal);
        if (start >= 0)
        {
            int countStart = start + Started.Length;
            int end = text.IndexOf(']', countStart);
            if (end > countStart && int.TryParse(text.Substring(countStart, end - countStart), out int parsed))
                activeSessions = parsed;

            connected = false;
            display = null;
        }
        else if (text.EndsWith("CLIENT DISCONNECTED", StringComparison.Ordinal))
        {
            activeSessions = Math.Max(0, activeSessions - 1);
            connected = false;
            display = null;
        }
        else if (text.EndsWith("CLIENT CONNECTED", StringComparison.Ordinal))
        {
            connected = true;
        }
        else if (connected && activeSessions == 1)
        {
            int selected = text.IndexOf(DisplaySelected, StringComparison.Ordinal);
            if (selected >= 0)
                display = text.Substring(selected + DisplaySelected.Length).Trim();
        }
    }

    private void Reset()
    {
        tail = null;
        lastLength = 0;
        activeSessions = 0;
        connected = false;
        display = null;
    }
}
