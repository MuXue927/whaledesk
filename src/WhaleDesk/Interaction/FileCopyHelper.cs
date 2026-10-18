using System.IO;

namespace WhaleDesk.Interaction;

/// <summary>
/// Copies dropped files into a folder the user picks, renaming on conflict.
///
/// Why: Windows Explorer cannot be intercepted, so pasting our clipboard into the file's OWN
/// folder always pops "the source and destination names are identical". Doing the copy inside
/// the pet lets us pick a free name instead of stopping the whole operation.
/// </summary>
internal static class FileCopyHelper
{
    /// <summary>Where copies would go. Pure: nothing is written here, so it is unit-testable.</summary>
    public static IReadOnlyList<(string Source, string Destination)> Plan(IEnumerable<string> sources, string targetFolder)
    {
        var plan = new List<(string, string)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (!File.Exists(source) && !Directory.Exists(source)) continue;

            var name = Path.GetFileName(source.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) continue;

            var candidate = Path.Combine(targetFolder, name);
            if (File.Exists(candidate) || Directory.Exists(candidate) || taken.Contains(candidate))
            {
                for (var i = 2; i < 1000; i++)
                {
                    var renamed = Path.Combine(targetFolder, AddSuffix(name, i, Directory.Exists(source)));
                    if (!File.Exists(renamed) && !Directory.Exists(renamed) && !taken.Contains(renamed))
                    {
                        candidate = renamed;
                        break;
                    }
                }
            }

            taken.Add(candidate);
            plan.Add((source, candidate));
        }
        return plan;
    }

    /// <summary>"report.txt" -> "report (2).txt"; a folder keeps its name -> "photos (2)".</summary>
    public static string AddSuffix(string name, int index, bool isDirectory)
    {
        if (isDirectory) return $"{name} ({index})";
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        return $"{stem} ({index}){extension}";
    }

    /// <summary>Run a plan; returns how many files were copied and the first error, if any.</summary>
    public static int Execute(IReadOnlyList<(string Source, string Destination)> plan, out string error)
    {
        error = string.Empty;
        var copied = 0;
        foreach (var (source, destination) in plan)
        {
            try
            {
                if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination);
                }
                else
                {
                    var dir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(source, destination, overwrite: false);
                }
                copied++;
            }
            catch (Exception ex)
            {
                if (error.Length == 0) error = $"{Path.GetFileName(source)}: {ex.Message}";
            }
        }
        return copied;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: false);
        }
    }
}
