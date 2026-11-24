using System.Text;

namespace AgentBell;

internal sealed class FileTail
{
    private MemoryStream? partial;
    private long offset;
    private bool skipPartial;
    private DateTime creationTime;

    internal FileTail(string path, bool skipExisting)
    {
        var file = new FileInfo(path);
        creationTime = file.CreationTimeUtc;
        if (!skipExisting)
            return;

        using var stream = Open(path);
        offset = stream.Length;
        if (offset > 0)
        {
            stream.Position = offset - 1;
            skipPartial = stream.ReadByte() != '\n';
        }
    }

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal bool Read(string path, Action<string> onLine)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return false;

        bool reset = file.CreationTimeUtc != creationTime || file.Length < offset;
        if (reset)
        {
            creationTime = file.CreationTimeUtc;
            offset = 0;
            partial = null;
            skipPartial = false;
        }

        if (file.Length == offset)
            return reset;

        using var stream = Open(path);
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(64 * 1024, file.Length - offset)];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            while (start < count)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                if (end < 0)
                {
                    if (!skipPartial)
                    {
                        partial ??= new MemoryStream();
                        partial.Write(buffer, start, count - start);
                    }
                    break;
                }

                if (skipPartial)
                {
                    skipPartial = false;
                }
                else if (partial is not null)
                {
                    partial.Write(buffer, start, end - start);
                    string line = Encoding.UTF8.GetString(partial.GetBuffer(), 0, (int)partial.Length);
                    partial = null;
                    onLine(line);
                }
                else
                {
                    onLine(Encoding.UTF8.GetString(buffer, start, end - start));
                }

                start = end + 1;
            }
            offset += count;
        }

        return reset;
    }
}
