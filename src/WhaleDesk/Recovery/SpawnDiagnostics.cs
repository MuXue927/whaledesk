using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WhaleDesk.Recovery;

/// <summary>
/// Why can't this process start children? (Reported symptom: a child DSH died with
/// STATUS_BREAKPOINT 0x80000003, and the Explorer fallback died with
/// STATUS_DLL_INIT_FAILED 0xc0000142 — the classic sign of a *restricted token*:
/// protected system binaries refuse to initialise under a low/AppContainer token,
/// and Chromium CHECK-fails.)
///
/// This logs the facts needed to tell "restricted token" apart from "out of memory"
/// or "the app itself is broken", plus a control spawn of a tiny signed binary.
/// </summary>
internal static class SpawnDiagnostics
{
    public static string DescribeTrust()
    {
        var parts = new List<string>();
        parts.Add($"pid={Environment.ProcessId}");
        parts.Add($"elevated={IsElevated()}");

        // The kernel pseudo-handle needs no access rights. Process.Handle is denied inside a
        // restricted sandbox ("access denied"), which is itself a useful signal.
        var handle = GetCurrentProcess();
        if (!OpenProcessToken(handle, TOKEN_QUERY, out var token))
        {
            parts.Add($"token=unavailable(win32={Marshal.GetLastWin32Error()})");
            return string.Join(" ", parts);
        }

        try
        {
            parts.Add($"appContainer={ReadInt(token, TokenIsAppContainer, 4) == 1}");
            parts.Add($"isElevatedFlag={ReadInt(token, TokenElevation, 4) == 1}");
            parts.Add($"integrity={ReadIntegrity(token)}");
        }
        finally
        {
            CloseHandle(token);
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// True when this process runs at Low/Untrusted integrity. A restricted token cannot
    /// create a normal child: protected system binaries fail to initialise (0xc0000142) and
    /// Chromium CHECK-fails (0x80000003). Detecting it lets the pet say so instead of
    /// pretending to try.
    /// </summary>
    public static bool IsRestricted()
    {
        try
        {
            var handle = GetCurrentProcess();
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token)) return false;
            try
            {
                var integrity = ReadIntegrity(token);
                return integrity.StartsWith("Low", StringComparison.Ordinal) ||
                       integrity.StartsWith("Untrusted", StringComparison.Ordinal);
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    public static string CurrentIntegrity()
    {
        try
        {
            var handle = GetCurrentProcess();
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token)) return "unknown";
            try { return ReadIntegrity(token); }
            finally { CloseHandle(token); }
        }
        catch { return "unknown"; }
    }
    public static string DescribeEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot") ?? "(missing)";
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? "(missing)";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var windir = Environment.GetEnvironmentVariable("windir") ?? "(missing)";
        return $"SystemRoot='{root}' windir='{windir}' TEMP='{temp}' PATH_len={path.Length} " +
               $"user='{Environment.UserName}' interactive={Environment.UserInteractive} " +
               $"64bit={Environment.Is64BitProcess} cwd='{Environment.CurrentDirectory}'";
    }

    /// <summary>
    /// Spawns a tiny signed Windows binary and reports the exit code. This is the control
    /// test: if a trivial child cannot even start (or dies with 0xc0000142), the problem is
    /// this process's context/environment, not DeepSeek Harness.
    /// </summary>
    public static string RunControlChild(string arguments, int timeoutMs = 6_000)
    {
        var comspec = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\WINDOWS", "System32", "cmd.exe");
        if (!File.Exists(comspec)) return $"control-child: cmd.exe not found at '{comspec}'";

        var sw = Stopwatch.StartNew();
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = comspec,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return "control-child: Start returned null";

            if (!process.WaitForExit(timeoutMs))
                return $"control-child: still running after {timeoutMs}ms (started ok)";

            sw.Stop();
            var code = process.ExitCode;
            var hex = $"0x{unchecked((uint)code):X8}";
            process.Dispose();
            return $"control-child: exitCode={code} ({hex}) after {sw.ElapsedMilliseconds}ms";
        }
        catch (Exception ex)
        {
            return $"control-child: threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static int ReadInt(IntPtr token, int infoClass, int size)
    {
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, (uint)size, out _)) return -1;
            return Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadIntegrity(IntPtr token)
    {
        // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { Sid, Attributes } }
        if (!GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed) && needed <= 0)
            return "unknown";

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return "unknown";
            var sid = Marshal.ReadIntPtr(buffer);
            var count = GetSidSubAuthorityCount(sid);
            if (count == IntPtr.Zero) return "unknown";
            var last = Marshal.ReadByte(count) - 1;
            var rid = GetSidSubAuthority(sid, last);
            if (rid == IntPtr.Zero) return "unknown";
            var value = Marshal.ReadInt32(rid);
            return value switch
            {
                0x0000 => $"Untrusted(0x{value:X})",
                0x1000 => $"Low(0x{value:X})",
                0x2000 => $"Medium(0x{value:X})",
                0x2100 => $"MediumPlus(0x{value:X})",
                0x3000 => $"High(0x{value:X})",
                0x4000 => $"System(0x{value:X})",
                _ => $"0x{value:X}",
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, uint size, out uint returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
