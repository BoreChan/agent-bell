using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AgentBell;

internal sealed class CodexRollouts(string directory, AgentLog log, Action onCompleted)
{
    private sealed record Rollout(FileTail Tail, string? SessionId);

    private readonly Dictionary<string, Rollout> rollouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Session, string Turn)> seen = [];
    private DateTimeOffset started;

    internal void Run(CancellationToken cancellation)
    {
        while (!Directory.Exists(directory))
        {
            if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
                return;
        }

        using var signal = new AutoResetEvent(false);
        using var watcher = new FileSystemWatcher(directory, "rollout-*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += (_, args) => Signal(args.FullPath);
        watcher.Created += (_, args) => Signal(args.FullPath);
        watcher.Renamed += (_, args) => Signal(args.FullPath);
        watcher.Error += (_, _) => { if (!cancellation.IsCancellationRequested) signal.Set(); };
        watcher.EnableRaisingEvents = true;

        foreach (var path in FindRollouts())
        {
            try { rollouts[path] = new Rollout(new FileTail(path, skipExisting: true), null); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        started = DateTimeOffset.UtcNow;
        log.Write("Codex watcher ready.");
        var scanTimer = Stopwatch.StartNew();
        WaitHandle[] waits = [signal, cancellation.WaitHandle];

        while (!cancellation.IsCancellationRequested)
        {
            WaitHandle.WaitAny(waits, 1000);
            foreach (string path in changes.Keys)
            {
                if (changes.TryRemove(path, out _))
                    Read(path);
            }

            if (scanTimer.Elapsed >= TimeSpan.FromSeconds(20))
            {
                foreach (string path in FindRollouts())
                    Read(path);
                scanTimer.Restart();
            }
        }

        void Signal(string path)
        {
            if (cancellation.IsCancellationRequested)
                return;
            changes.TryAdd(path, 0);
            signal.Set();
        }
    }

    private string[] FindRollouts()
    {
        try
        {
            return Directory.GetFiles(directory, "rollout-*.jsonl", SearchOption.AllDirectories);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private void Read(string path)
    {
        try
        {
            if (!rollouts.TryGetValue(path, out var rollout))
            {
                rollout = new Rollout(new FileTail(path, skipExisting: false), null);
                rollouts[path] = rollout;
            }

            if (rollout.SessionId is null)
            {
                string? session = ReadMainSession(path);
                if (session is null)
                    return;

                rollout = rollout with { SessionId = session };
                rollouts[path] = rollout;
            }

            if (rollout.SessionId.Length > 0)
                rollout.Tail.Read(path, line => ReadEvent(rollout.SessionId, line));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string? ReadMainSession(string path)
    {
        using var stream = FileTail.Open(path);
        using var reader = new StreamReader(stream);
        string? line = reader.ReadLine();
        if (line is null)
            return null;

        try
        {
            if (ParseJson(line) is not Dictionary<string, object> root ||
                Value(root, "type") is not "session_meta" ||
                Value(root, "payload") is not Dictionary<string, object> payload)
                return string.Empty;

            if (Value(payload, "thread_source") is "subagent" or "guardian_review")
                return string.Empty;
            if (Value(payload, "source") is Dictionary<string, object> source && source.ContainsKey("subagent"))
                return string.Empty;

            return Value(payload, "id") as string ?? string.Empty;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
    }

    private void ReadEvent(string session, string line)
    {
        string? turn = ReadCompletion(line, started);
        if (turn is null || !seen.Add((session, turn)))
            return;

        log.Write($"Codex completed session={session} turn={turn}.");
        onCompleted();
    }

    internal static string? ReadCompletion(string line, DateTimeOffset started)
    {
        if (line.IndexOf("\"task_complete\"", StringComparison.Ordinal) < 0 &&
            line.IndexOf("\"turn_complete\"", StringComparison.Ordinal) < 0)
            return null;

        try
        {
            if (ParseJson(line) is not Dictionary<string, object> root ||
                Value(root, "type") is not "event_msg" ||
                Value(root, "payload") is not Dictionary<string, object> payload ||
                Value(payload, "type") is not ("task_complete" or "turn_complete"))
                return null;

            if (Value(root, "timestamp") is string timestamp &&
                DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var writtenAt) &&
                writtenAt < started)
                return null;

            return Value(payload, "turn_id") as string;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
    }

    private static object? ParseJson(string line) =>
        new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }.DeserializeObject(line);

    private static object? Value(Dictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;
}
