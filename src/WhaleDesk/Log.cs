using System.IO;
using System.Text;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>
/// Minimal logger: everything goes to the console (captured by the verification
/// scripts) and to build/whaledesk.log so a failure can be read after the
/// process is gone.
///
/// v0.24.4 opened and closed the file on EVERY line (File.AppendAllText) and only
/// checked the 2 MB limit in the constructor. Follow-cursor alone writes a line every
/// 50 ms step, so a long-running session grew the log without bound (1.38 MB /
/// 11 707 lines in one day on the machine this was found on). The handle is now kept
/// open, and the rotation check also runs while writing.
/// </summary>
internal sealed class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _bytes;

    public Log(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        lock (_gate)
        {
            try
            {
                // Append instead of truncate: several verification runs in a row must all
                // survive in one file, otherwise the evidence of an earlier run is lost
                // the moment the next one starts.
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                OpenLocked();
                _writer!.WriteLine($"# WhaleDesk run {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId})");
            }
            catch { /* logging must never take the app down */ }
        }
    }

    private void OpenLocked()
    {
        _writer?.Dispose();
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        // No BOM: the logs are read by scripts and grep, and a BOM breaks the first comparison.
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _bytes = stream.Length;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Diag(string key, object? value) => Write("DIAG", $"{key} = {Format(value)}");

    private static string Format(object? value)
    {
        if (value is null) return "null";
        if (value is System.Collections.IEnumerable e and not string)
        {
            var sb = new StringBuilder("[");
            var first = true;
            foreach (var item in e)
            {
                if (!first) sb.Append(", ");
                sb.Append(item);
                first = false;
            }
            return sb.Append(']').ToString();
        }
        return value.ToString() ?? "null";
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            try
            {
                if (_writer is null) OpenLocked();
                _writer!.WriteLine(line);
                _bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_bytes > MaxBytes) RotateLocked();
            }
            catch { /* logging must never take the app down */ }
        }
    }

    /// <summary>Rotate while running, not just at startup, so a week-long session stays bounded.</summary>
    private void RotateLocked()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            File.Move(_path, _path + ".1", overwrite: true);
            OpenLocked();
            _writer!.WriteLine($"# log rotated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch
        {
            // Move failed (file locked): reopen the current file and keep logging; the size
            // check simply retries on the next line.
            try { if (_writer is null) OpenLocked(); } catch { }
        }
    }

    public void Json(string label, object value)
    {
        try { Write("DIAG", $"{label} = {JsonSerializer.Serialize(value)}"); }
        catch (Exception ex) { Write("WARN", $"could not serialize {label}: {ex.Message}"); }
    }
}
