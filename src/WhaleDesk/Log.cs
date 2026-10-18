using System.IO;
using System.Text;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>
/// Minimal logger: everything goes to the console (captured by the verification
/// scripts) and to build/whaledesk.log so a failure can be read after the
/// process is gone.
/// </summary>
internal sealed class Log
{
    private readonly string _path;
    private readonly object _gate = new();

    public Log(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            // Append instead of truncate: several verification runs in a row must all
            // survive in one file, otherwise the evidence of an earlier run is lost
            // the moment the next one starts. Rotate at 2 MB to stay bounded.
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(_path, $"# WhaleDesk run {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}){Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
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
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }

    public void Json(string label, object value)
    {
        try { Write("DIAG", $"{label} = {JsonSerializer.Serialize(value)}"); }
        catch (Exception ex) { Write("WARN", $"could not serialize {label}: {ex.Message}"); }
    }
}
