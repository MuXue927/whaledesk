using System.IO;
using System.Text.Json;

namespace WhaleDesk.Monitoring;

/// <summary>One task as the DSH task board ledger records it.</summary>
internal sealed record TaskBoardTask(string Id, string Title, string Status, DateTime UpdatedLocal);

/// <summary>Everything the pet needs from one read of the ledger.</summary>
internal sealed class TaskBoardSnapshot
{
    public IReadOnlyList<TaskBoardTask> Tasks { get; init; } = Array.Empty<TaskBoardTask>();

    public int Count(string status) => Tasks.Count(t => string.Equals(t.Status, status, StringComparison.OrdinalIgnoreCase));

    public int Running => Count("running");
    public int Todo => Count("todo");
    public int Backlog => Count("backlog");
    public int DoneToday => Tasks.Count(t => t.Status is "done" && t.UpdatedLocal.Date == DateTime.Now.Date);
    public int FailedToday => Tasks.Count(t => t.Status is "failed" && t.UpdatedLocal.Date == DateTime.Now.Date);

    /// <summary>"done" or "failed" - the two statuses worth announcing.</summary>
    public static bool IsFinished(string status) => status is "done" or "failed";
}

/// <summary>
/// Reads the DSH task board ledger (<c>~/.dsh/task-board/ledger-v2.json</c>) so the pet can show
/// the counts and announce a long task the moment it finishes.
///
/// The ledger is the same file the task board UI writes; it is read-only for us and the format is
/// versioned (<c>schemaVersion</c>). Parsing is kept in a pure static method that takes the JSON
/// text, so <c>--selftest</c> can exercise it with a fixture and no DSH running.
///
/// Anything unexpected degrades to "no data": the pet must never fail to start because a ledger
/// was half-written, locked, or from a newer schema.
/// </summary>
internal static class TaskBoardWatch
{
    public static string LedgerPath =>
        Path.Combine(Cleanup.CleanupCatalog.DshHome, "task-board", "ledger-v2.json");

    public static TaskBoardSnapshot? Read()
    {
        try
        {
            if (!File.Exists(LedgerPath)) return null;
            // Share ReadWrite: the task board keeps the file open while it works.
            using var stream = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure parse of the ledger text. Returns null when it is not a task board ledger at all.</summary>
    public static TaskBoardSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<TaskBoardTask>();
            foreach (var t in tasks.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var id = Str(t, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var title = Str(t, "title") ?? id;
                var status = Str(t, "status") ?? "todo";
                var updated = Num(t, "updatedAt") ?? Num(t, "createdAt") ?? 0;
                var updatedLocal = updated > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)updated).LocalDateTime
                    : DateTime.MinValue;
                list.Add(new TaskBoardTask(id, title, status, updatedLocal));
            }
            return new TaskBoardSnapshot { Tasks = list };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tasks that were running in <paramref name="previous"/> and are finished in
    /// <paramref name="current"/>. Returns an empty list when there is nothing to announce; a task
    /// that merely disappears from the ledger is not announced (it may have been deleted).
    /// </summary>
    public static IReadOnlyList<TaskBoardTask> FinishedBetween(TaskBoardSnapshot? previous, TaskBoardSnapshot? current)
    {
        if (previous is null || current is null || previous.Tasks.Count == 0) return Array.Empty<TaskBoardTask>();
        var before = previous.Tasks.ToDictionary(t => t.Id, t => t.Status, StringComparer.Ordinal);
        var finished = new List<TaskBoardTask>();
        foreach (var task in current.Tasks)
        {
            if (!TaskBoardSnapshot.IsFinished(task.Status)) continue;
            if (before.TryGetValue(task.Id, out var old) && old == "running") finished.Add(task);
        }
        return finished;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
