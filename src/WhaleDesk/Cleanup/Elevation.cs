using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.IO;

namespace WhaleDesk.Cleanup;

/// <summary>
/// UAC elevation for the admin-only cleanup targets.
///
/// Security shape: the elevated process is told a TARGET ID from the fixed catalog —
/// never a path — and the helper refuses any id that is not in the admin whitelist.
/// So elevation cannot be turned into "run this delete as administrator".
///
/// The elevated run writes a small JSON result that the non-elevated instance reads
/// back, because the two processes share no memory.
/// </summary>
internal static class Elevation
{
    public sealed record ElevatedResult(string TargetId, bool Ran, long Bytes, int Deleted, int Skipped, string? Error, string? Output, bool WasElevated);

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_SHOWNORMAL = 1;

    /// <summary>
    /// Launch the same executable with the "runas" verb so Windows shows the UAC
    /// consent dialog, then wait for it and return its exit code.
    /// Returns false with an error when the user declines (ERROR_CANCELLED = 1223).
    /// </summary>
    public static bool TryRunElevated(string exePath, string arguments, int timeoutMs, out int exitCode, out string? error)
    {
        exitCode = -1;
        error = null;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS,
            lpVerb = "runas",
            lpFile = exePath,
            lpParameters = arguments,
            lpDirectory = Path.GetDirectoryName(exePath),
            nShow = SW_SHOWNORMAL,
        };
        try
        {
            if (!ShellExecuteEx(ref info))
            {
                var code = Marshal.GetLastWin32Error();
                error = code == 1223 ? "你取消了 UAC 授权" : $"提权启动失败（错误码 {code}）";
                return false;
            }
            if (info.hProcess == IntPtr.Zero)
            {
                error = "提权进程句柄无效";
                return false;
            }
            try
            {
                var waited = WaitForSingleObject(info.hProcess, (uint)timeoutMs);
                if (waited != 0)
                {
                    error = "提权进程超时（可能仍在运行）";
                    return false;
                }
                if (!GetExitCodeProcess(info.hProcess, out var code))
                {
                    error = "无法读取提权进程退出码";
                    return false;
                }
                exitCode = (int)code;
                return true;
            }
            finally { CloseHandle(info.hProcess); }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The elevated half: runs one whitelisted admin cleanup and writes the result.</summary>
    public static int RunElevatedClean(string targetId, string resultPath, Log log)
    {
        var wasElevated = IsElevated;
        log.Info($"elevated-clean: target={targetId} elevated={wasElevated}");
        ElevatedResult result;

        var target = CleanupCatalog.Build().FirstOrDefault(t => t.Id == targetId);
        if (target is null)
        {
            result = new ElevatedResult(targetId, false, 0, 0, 0, $"未知的清理项 id: {targetId}", null, wasElevated);
        }
        else if (!CleanupCatalog.MayExecuteElevated(target))
        {
            result = new ElevatedResult(targetId, false, 0, 0, 0, $"「{target.Label}」不在可提权清理的白名单内", null, wasElevated);
        }
        else if (!wasElevated)
        {
            result = new ElevatedResult(targetId, false, 0, 0, 0, "没有管理员权限，已拒绝执行", null, false);
        }
        else
        {
            var r = CleanupRunner.CleanElevated(target, log);
            result = new ElevatedResult(targetId, true, r.Bytes, r.Deleted, r.Skipped, r.Error, r.CommandOutput, true);
        }

        try
        {
            var dir = Path.GetDirectoryName(resultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            log.Info($"elevated-clean: wrote result to {resultPath}");
        }
        catch (Exception ex)
        {
            log.Error("elevated-clean: could not write the result file: " + ex.Message);
            return 1;
        }
        log.Info($"elevated-clean done: ran={result.Ran} bytes={result.Bytes} deleted={result.Deleted} error={result.Error ?? "none"}");
        return result.Error is null ? 0 : 1;
    }

    /// <summary>Read the result the elevated process wrote.</summary>
    public static ElevatedResult? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}
