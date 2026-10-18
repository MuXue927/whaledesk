using System.Diagnostics;
using System.IO;

namespace WhaleDesk.Cleanup;

/// <summary>
/// Rehearsal for the riskiest feature: the cleaner deletes the user's files, so its
/// selection rules are proven on a synthetic tree before it ever runs for real.
/// Verifies: the path guard, the age filter, locked files, empty-directory pruning
/// and — most importantly — that a junction inside a target is never followed, so
/// nothing outside the target can be deleted through it.
/// </summary>
internal static class Rehearsal
{
    public static int Run(string verifyDir, Log log)
    {
        var pass = 0;
        var fail = 0;
        var degraded = new List<string>();

        void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; log.Info($"rehearsal PASS {name}: {detail}"); }
            else { fail++; log.Error($"rehearsal FAIL {name}: {detail}"); }
        }

        // ---------- 1. the guard (pure, no file system writes) ----------
        Check("guard-accepts-path-inside-profile",
            CleanupCatalog.IsAllowedPath(Path.Combine(CleanupCatalog.UserProfile, "AppData", "Local", "Temp")),
            "a path under the user profile is allowed");
        Check("guard-rejects-outside-profile",
            !CleanupCatalog.IsAllowedPath(@"C:\Windows\System32") && !CleanupCatalog.IsAllowedPath(@"D:\anything"),
            "paths outside the user profile are rejected");
        Check("guard-rejects-profile-root",
            CleanupCatalog.IsForbiddenTarget(CleanupCatalog.UserProfile) &&
            CleanupCatalog.IsForbiddenTarget(CleanupCatalog.LocalAppData) &&
            CleanupCatalog.IsForbiddenTarget(@"C:\"),
            "the profile root, LocalAppData and a drive root can never be a target");
        Check("guard-rejects-protected-segments",
            CleanupCatalog.IsForbiddenTarget(Path.Combine(CleanupCatalog.UserProfile, "proj", "node_modules", "x")) &&
            CleanupCatalog.IsForbiddenTarget(Path.Combine(CleanupCatalog.UserProfile, "proj", ".git", "x")),
            "node_modules and .git paths are refused even inside the profile");
        Check("guard-rejects-the-real-windows-directory",
            CleanupCatalog.IsForbiddenTarget(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")),
            "C:\\Windows\\... is refused");
        Check("guard-allows-appdata-microsoft-windows",
            !CleanupCatalog.IsForbiddenTarget(Path.Combine(CleanupCatalog.LocalAppData, "Microsoft", "Windows", "Explorer")) &&
            CleanupCatalog.IsAllowedPath(Path.Combine(CleanupCatalog.LocalAppData, "Microsoft", "Windows", "Explorer")),
            "the thumbnail cache lives under ...\\Microsoft\\Windows\\ and must NOT be mistaken for C:\\Windows (a substring match rejected it)");

        var catalog = CleanupCatalog.Build();
        Check("catalog-admin-targets-are-report-only",
            catalog.Where(t => t.Tier == CleanupTier.AdminOnly).All(t => !CleanupCatalog.MayExecute(t) && t.Method == CleanupMethod.ReportOnly),
            $"{catalog.Count(t => t.Tier == CleanupTier.AdminOnly)} admin target(s) can never be executed");
        Check("catalog-hands-off-targets-are-report-only",
            catalog.Where(t => t.Tier == CleanupTier.HandsOff).All(t => !CleanupCatalog.MayExecute(t)),
            $"{catalog.Count(t => t.Tier == CleanupTier.HandsOff)} handled-off target(s) are reported with a reason, never cleaned");
        Check("catalog-pnpm-store-uses-vendor-command",
            catalog.Single(t => t.Id == "pnpm-store").Method == CleanupMethod.OfficialCommand,
            "the pnpm store is hard-linked into installed projects, so it is only pruned, never deleted");
        var outsideProfile = catalog.Where(t =>
            !CleanupCatalog.IsAllowedPath(t.Path)
            && t.Tier is not (CleanupTier.AdminOnly or CleanupTier.HandsOff)   // reported only, never executed
            && t.Method != CleanupMethod.EmptyRecycleBin).ToList();
        Check("catalog-paths-are-allowed-or-admin",
            outsideProfile.Count == 0,
            outsideProfile.Count == 0
                ? $"all {catalog.Count} targets live under the user profile (or need elevation / are the recycle bin)"
                : "outside the allowed roots: " + string.Join(", ", outsideProfile.Select(t => $"{t.Id}='{t.Path}'")));

        // The user's requirement: every single item must explain itself.
        var unexplained = catalog.Where(t =>
            string.IsNullOrWhiteSpace(t.Detail) || string.IsNullOrWhiteSpace(t.Impact) ||
            string.IsNullOrWhiteSpace(t.Verdict) || string.IsNullOrWhiteSpace(t.Label)).ToList();
        Check("every-target-explains-itself",
            unexplained.Count == 0,
            unexplained.Count == 0
                ? $"all {catalog.Count} targets carry 作用 / 删除影响 / 结论"
                : "missing text: " + string.Join(", ", unexplained.Select(t => t.Id)));
        Check("every-safe-target-says-it-is-safe",
            catalog.Where(t => t.Tier == CleanupTier.Safe).All(t => t.Verdict.Contains("放心") || t.Verdict.Contains("可以清")),
            "each 放心 tier item tells the user it is safe to clean regularly");
        Check("coverage-is-broad-enough",
            catalog.Count >= 35,
            $"{catalog.Count} targets across {catalog.Select(t => t.Group).Distinct().Count()} groups "
            + "(user temp, browsers, JetBrains, npm/pnpm/pip/nuget/go/gradle/cargo, VS Code, crash dumps, shader caches, INetCache, WER, admin caches, recycle bin)");
        Check("temp-age-is-configurable",
            CleanupCatalog.Build(0).Single(t => t.Id == "user-temp").MinAgeDays == 0 &&
            CleanupCatalog.Build(30).Single(t => t.Id == "user-temp").MinAgeDays == 30,
            "the temp threshold can be deepened (7 days -> 1 day -> everything) for a bigger cleanup");

        // ---- the safe batch must never reach outside the Safe tier ----
        var batch = CleanupCatalog.SafeBatch();
        Check("safe-batch-contains-only-safe-tiers",
            batch.All(t => t.Tier == CleanupTier.Safe),
            $"{batch.Count} items, tiers={string.Join(",", batch.Select(t => t.Tier).Distinct())}");
        Check("safe-batch-excludes-recycle-bin-and-vendor-commands",
            batch.All(t => t.Method != CleanupMethod.EmptyRecycleBin) &&
            batch.All(t => t.Method != CleanupMethod.OfficialCommand || t.SafeRegularly),
            "the batch never empties the recycle bin by surprise");
        Check("safe-batch-excludes-caution-items",
            !batch.Any(t => t.Id is "jetbrains" or "gradle-caches"),
            "expensive-to-rebuild items stay out of the one-click batch");

        // ---- the confirm state machine (the missing way out) ----
        var arm = new CleanupArmState();
        Check("arm-first-click-only-arms", !arm.Click("user-temp", 1000) && arm.IsArmed("user-temp"), "first click arms, does not delete");
        Check("arm-second-click-confirms", arm.Click("user-temp", 2000) && arm.ArmedId is null, "second click within the window confirms");
        arm.Click("user-temp", 3000);
        arm.Cancel();
        Check("arm-cancel-clears", arm.ArmedId is null && !arm.Click("user-temp", 3500), "cancel disarms, and the next click only re-arms");
        arm.Click("npm-cache", 5000);
        arm.Click("crashdumps", 5100);
        Check("arm-switching-rows-moves-the-arm", arm.IsArmed("crashdumps") && !arm.IsArmed("npm-cache"), "arming another row disarms the previous one");
        Check("arm-expires-and-reports",
            arm.ExpireIfNeeded(5100 + CleanupArmState.ConfirmWindowMs + 1) && arm.ArmedId is null,
            "an expired arm is reported so the UI can visibly revert the row");
        arm.Click("pip-cache", 9000);
        Check("arm-expired-click-re-arms-instead-of-deleting",
            !arm.Click("pip-cache", 9000 + CleanupArmState.ConfirmWindowMs + 1),
            "clicking long after the window re-arms rather than deleting");

        // ---------- 2. synthetic tree ----------
        var sandbox = Path.Combine(verifyDir, "cleanup-sandbox");
        try
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
        catch (Exception ex) { log.Warn("rehearsal: could not reset sandbox: " + ex.Message); }
        Directory.CreateDirectory(sandbox);

        var outside = Path.Combine(sandbox, "outside");
        Directory.CreateDirectory(outside);
        var keepFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(keepFile, new string('k', 2048));

        // -- age-filtered target
        var temp = Path.Combine(sandbox, "user-temp");
        Directory.CreateDirectory(Path.Combine(temp, "sub"));
        var oldFile = Path.Combine(temp, "old.txt");
        var oldFileSub = Path.Combine(temp, "sub", "old2.txt");
        var newFile = Path.Combine(temp, "new.txt");
        var lockedFile = Path.Combine(temp, "locked.txt");
        File.WriteAllText(oldFile, new string('o', 4096));
        File.WriteAllText(oldFileSub, new string('o', 4096));
        File.WriteAllText(newFile, new string('n', 4096));
        File.WriteAllText(lockedFile, new string('l', 4096));
        var old = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(oldFile, old);
        File.SetLastWriteTimeUtc(oldFileSub, old);
        File.SetLastWriteTimeUtc(lockedFile, old);

        var junctionMade = TryJunction(Path.Combine(temp, "link"), outside, log);
        if (!junctionMade) degraded.Add("junction-in-age-target (mklink unavailable)");

        var tempTarget = new CleanupTarget("rehearsal-temp", "演练-临时",
            "合成的临时目录，用来验证年龄过滤与占用跳过", "只应删掉旧文件", "演练用",
            CleanupTier.Safe, CleanupMethod.RecycleContents, temp, MinAgeDays: 7);

        var scan = CleanupScanner.Scan(tempTarget, log);
        Check("scan-counts-only-old-files",
            scan.Files == 3 && scan.Skipped >= 1,
            $"files={scan.Files} bytes={scan.Bytes} skipped={scan.Skipped} (3 old files: old.txt, sub/old2.txt, locked.txt; new.txt filtered by age)");

        // hold the locked file open with no sharing
        using (var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = CleanupRunner.CleanUnchecked(tempTarget, log, hardDelete: true);
            Check("clean-deletes-old-files-only",
                !File.Exists(oldFile) && !File.Exists(oldFileSub) && File.Exists(newFile),
                $"old.txt gone={!File.Exists(oldFile)} old2.txt gone={!File.Exists(oldFileSub)} new.txt kept={File.Exists(newFile)}");
            Check("clean-skips-locked-file",
                File.Exists(lockedFile) && result.Skipped >= 1,
                $"locked.txt still present={File.Exists(lockedFile)} skipped={result.Skipped} (deleted={result.Deleted})");
            Check("clean-reports-bytes", result.Bytes >= 8192, $"reported {result.Bytes} bytes for the two deleted files");
            Check("clean-ignores-recent-files", File.Exists(newFile), "a file newer than the age threshold was left alone");
            if (junctionMade)
            {
                Check("clean-does-not-follow-junction",
                    File.Exists(keepFile) && Directory.Exists(Path.Combine(temp, "link")),
                    "the file behind the junction survived and the junction itself was left in place");
            }
            else
            {
                log.Warn("rehearsal: junction checks SKIPPED (mklink unavailable in this environment)");
            }
        }

        // -- plain target with a junction (no age filter): direct children are recycled
        var cache = Path.Combine(sandbox, "cache");
        Directory.CreateDirectory(cache);
        var plain = Path.Combine(cache, "plain.bin");
        File.WriteAllText(plain, new string('p', 3072));
        var junction2 = TryJunction(Path.Combine(cache, "link"), outside, log);
        if (!junction2) degraded.Add("junction-in-plain-target (mklink unavailable)");

        var cacheTarget = new CleanupTarget("rehearsal-cache", "演练-缓存",
            "合成的缓存目录，用来验证重解析点不被跟随", "只应删掉普通子项", "演练用",
            CleanupTier.Safe, CleanupMethod.RecycleContents, cache);
        var result2 = CleanupRunner.CleanUnchecked(cacheTarget, log, hardDelete: true);
        Check("clean-removes-plain-children",
            !File.Exists(plain) && result2.Deleted >= 1,
            $"plain.bin gone={!File.Exists(plain)} deleted={result2.Deleted}");
        if (junction2)
        {
            Check("clean-keeps-junction-and-its-target",
                Directory.Exists(Path.Combine(cache, "link")) && File.Exists(keepFile),
                "a reparse point is skipped, so nothing behind it can be destroyed");
        }
        else
        {
            log.Warn("rehearsal: second junction check SKIPPED");
        }

        // -- the detection mechanism itself, proven on a real system junction
        var legacyJunction = Path.Combine(CleanupCatalog.UserProfile, "Application Data");
        if (Directory.Exists(legacyJunction))
        {
            var attributes = new DirectoryInfo(legacyJunction).Attributes;
            Check("reparse-point-detection-works-on-a-real-junction",
                (attributes & FileAttributes.ReparsePoint) != 0,
                $"'Application Data' carries ReparsePoint ({attributes}) and would be skipped");
        }
        else
        {
            degraded.Add("system legacy junction not present for the detection check");
        }

        // ---------- 3. the real (recycle-bin) path, on one throwaway file ----------
        var throwaway = Path.Combine(sandbox, "recycle-me.txt");
        File.WriteAllText(throwaway, "rehearsal");
        var recycled = RecycleBin.TryRecycle(new[] { throwaway }, log, out _);
        Check("recycle-bin-path-removes-the-file",
            recycled && !File.Exists(throwaway),
            recycled ? "SHFileOperation(FOF_ALLOWUNDO) succeeded and the file is gone" : "SHFileOperation refused (environment restriction) - the production path could not be exercised here");

        // ---------- summary ----------
        var code = fail == 0 ? 0 : 1;
        log.Info($"rehearsal summary: {pass} pass / {fail} fail / {degraded.Count} degraded"
                 + (degraded.Count > 0 ? " [" + string.Join("; ", degraded) + "]" : ""));
        log.Info($"rehearsal cleanup of the sandbox: {(Directory.Exists(sandbox) ? "left in place for inspection" : "already clean")} -> {sandbox}");
        Console.WriteLine($"rehearsal: {pass} pass, {fail} fail, {degraded.Count} degraded");
        return code;
    }

    private static bool TryJunction(string link, string target, Log log)
    {
        try
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            var psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var output = (proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()).Trim();
            proc.WaitForExit(10_000);
            var ok = proc.ExitCode == 0 && Directory.Exists(link) &&
                     (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
            log.Info($"rehearsal: junction {link} -> {target} created={ok} (exit {proc.ExitCode}) {output}");
            return ok;
        }
        catch (Exception ex)
        {
            log.Warn($"rehearsal: junction creation failed: {ex.Message}");
            return false;
        }
    }
}
