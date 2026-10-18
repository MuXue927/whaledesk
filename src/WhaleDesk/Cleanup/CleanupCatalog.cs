using System.IO;

namespace WhaleDesk.Cleanup;

internal enum CleanupTier
{
    /// <summary>Regenerated automatically; no downside worth mentioning.</summary>
    Safe,
    /// <summary>Recoverable, but costs a re-download, a rebuild or a re-index.</summary>
    Caution,
    /// <summary>Needs elevation. Never executed, only reported.</summary>
    AdminOnly,
    /// <summary>Shown for transparency; the cleaner refuses to touch it and says why.</summary>
    HandsOff,
}

internal enum CleanupMethod
{
    /// <summary>Move the directory's contents to the recycle bin.</summary>
    RecycleContents,
    /// <summary>Move matching files (pattern) to the recycle bin.</summary>
    RecyclePattern,
    /// <summary>Run the vendor's own maintenance command (never delete the store by hand).</summary>
    OfficialCommand,
    /// <summary>Report only; the cleaner refuses to touch it.</summary>
    ReportOnly,
    /// <summary>Empty the recycle bin itself (needs an explicit confirmation).</summary>
    EmptyRecycleBin,
}

/// <summary>
/// A cleanup target. Every target MUST carry a human explanation: what it is, what
/// deleting it does, and a verdict — the user asked to be told the consequence of each
/// item instead of being handed a list of numbers. <see cref="Detail"/>,
/// <see cref="Impact"/> and <see cref="Verdict"/> are asserted non-empty by --selftest.
/// </summary>
internal sealed record CleanupTarget(
    string Id,
    string Label,
    string Detail,          // 这是什么 / 有什么用
    string Impact,          // 删了会怎样
    string Verdict,         // 一句话结论（含是否可放心定期清理）
    CleanupTier Tier,
    CleanupMethod Method,
    string Path,
    string? Pattern = null,
    string? Command = null,
    int MinAgeDays = 0,
    string Group = "",
    bool SkipMeasure = false)
{
    /// <summary>Whether this row may be executed by the cleaner at all.</summary>
    public bool Executable => Tier is CleanupTier.Safe or CleanupTier.Caution
                              && Method != CleanupMethod.ReportOnly && Method != CleanupMethod.EmptyRecycleBin;

    public bool SafeRegularly => Tier == CleanupTier.Safe;
}

/// <summary>
/// The only paths the cleaner will ever touch. Every rule that keeps it safe lives
/// here so --rehearsal can assert it without deleting anything:
///  * a target must resolve INSIDE the user profile (or be an explicitly
///    admin/report-only entry the cleaner refuses to execute);
///  * the cleaner never deletes a target directory itself, only its contents;
///  * the pnpm store is hard-linked into installed projects, so deleting it can
///    break them — it is only ever pruned through the vendor command;
///  * a reparse point is never followed.
/// </summary>
internal static class CleanupCatalog
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string DshHome => Path.Combine(UserProfile, ".dsh");

    /// <summary>Temp files younger than this are left alone in the normal pass.</summary>
    public const int DefaultTempAgeDays = 7;

    public static IReadOnlyList<CleanupTarget> Build(int tempAgeDays = DefaultTempAgeDays)
    {
        var local = LocalAppData;
        var roaming = RoamingAppData;
        var win = WindowsDir;
        var profile = UserProfile;
        var t = new List<CleanupTarget>();

        void Add(string id, string label, string detail, string impact, string verdict,
                 CleanupTier tier, CleanupMethod method, string path,
                 string? pattern = null, string? command = null, int minAge = 0, bool skipMeasure = false) =>
            t.Add(new CleanupTarget(id, label, detail, impact, verdict, tier, method, path, pattern, command, minAge, GroupOf(tier), skipMeasure));

        // ---------------------------------------------------------------- 放心清理
        Add("user-temp", $"用户临时文件（{tempAgeDays} 天前）",
            "程序运行时产生的临时文件，正常退出本该自己删掉，崩溃或异常退出时会留下。",
            $"只删 {tempAgeDays} 天没被动过、且当前没被占用的文件；正在运行的程序占用的文件会自动跳过。",
            "可放心定期清理，系统与软件会按需重建。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Temp"), minAge: tempAgeDays);

        Add("crashdumps", "崩溃转储",
            "程序崩溃时写下的内存快照（.dmp），用于事后调试。",
            "如果近期没有在排查崩溃，这些文件对你没有价值；已有的 dump 不会影响程序运行。",
            "可放心删除；若正在排查崩溃，建议先备份。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "CrashDumps"));

        Add("thumbnails", "缩略图缓存",
            "资源管理器为图片/视频生成的小预览图，用来加速文件夹预览。",
            "下次打开文件夹会自动重建（首次滚动稍慢）。文件常被资源管理器占用，被占用的会自动跳过。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecyclePattern, Path.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db");

        Add("iconcache", "图标缓存",
            "程序与文件类型图标的缓存数据库。",
            "下次会重建，图标显示可能短暂变慢。同样常被占用。",
            "可放心清理；若图标显示异常，清理它反而常常是修复办法。",
            CleanupTier.Safe, CleanupMethod.RecyclePattern, Path.Combine(local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db");

        Add("d3dscache", "DirectX 着色器缓存",
            "显卡驱动为游戏/应用预编译的 GPU 着色器缓存，用来减少卡顿。",
            "游戏首次运行时会重新编译着色器（首次进入可能略慢），之后恢复。",
            "可放心删除；若常玩游戏、空间又不紧张，留着反而更好。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "D3DSCache"));

        Add("amd-dxcache", "AMD 着色器缓存",
            "AMD 驱动侧的 DX 着色器缓存（DxCache）。",
            "与 DirectX 缓存同理，会在游戏运行时重建。",
            "可放心删除。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "AMD", "DxCache"));

        Add("nvidia-dxcache", "NVIDIA 着色器缓存",
            "NVIDIA 驱动的着色器缓存（DXCache）。",
            "会在游戏运行时重建，首次可能略慢。",
            "可放心删除。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "NVIDIA", "DXCache"));

        Add("nvidia-glcache", "NVIDIA OpenGL 缓存",
            "NVIDIA 驱动的 OpenGL 着色器缓存（GLCache）。",
            "会在运行 OpenGL 程序时重建。",
            "可放心删除。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "NVIDIA", "GLCache"));

        Add("inetcache", "Windows 网络缓存（INetCache）",
            "系统与老式程序通过 WinINet 下载的网页/图片缓存。",
            "只是缓存，联网后会重新下载；不影响已保存的密码与收藏。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Microsoft", "Windows", "INetCache"));

        Add("dsl-cache", "DSH 缓存",
            "DeepSeek Harness 自身的缓存目录。",
            "DSH 会按需重建；若 DSH 正在运行且占用文件，会被自动跳过。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(DshHome, "cache"));

        // ---------------------------------------------------------------- 浏览器
        Add("chrome-cache", "Chrome 网页缓存",
            "Chrome 缓存的网页图片、脚本等资源，用来加快二次打开速度。",
            "不删书签、不删密码、不删登录状态；只是下次访问网站要重新下载，首次加载略慢。清理前最好关闭 Chrome，否则占用中的文件会被跳过。",
            "可放心定期清理（建议先关浏览器）。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"));

        Add("chrome-code-cache", "Chrome 脚本缓存（Code Cache）",
            "Chrome 编译 JavaScript 后的字节码缓存。",
            "网站会重新编译脚本，首次打开略慢；不影响数据。建议关闭 Chrome 后再清。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Code Cache"));

        Add("chrome-gpucache", "Chrome GPU 缓存",
            "Chrome 的 GPU 着色器与图形缓存。",
            "会重建；页面首帧可能略慢。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Google", "Chrome", "User Data", "Default", "GPUCache"));

        Add("edge-cache", "Edge 网页缓存",
            "Edge 缓存的网页资源（你本机没在用 Edge，装了就会出现）。",
            "与 Chrome 同理：不动书签/密码，只让网站重新下载资源。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"));

        Add("edge-code-cache", "Edge 脚本缓存",
            "Edge 的 JavaScript 字节码缓存。",
            "会重建，首次加载略慢。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"));

        Add("firefox-cache", "Firefox 缓存",
            "Firefox 各配置文件的网络缓存。",
            "同样的逻辑：不动书签/密码/Cookie，只让网站重新下载。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Mozilla", "Firefox", "Profiles"));

        // ---------------------------------------------------------------- 开发工具
        Add("jetbrains", "JetBrains IDE 缓存（本机最大项）",
            "IDEA/PyCharm/WebStorm 等 IDE 的索引、缓存与日志目录。",
            "删除后 IDE 下次打开项目会重新建立索引：首次打开或大项目会明显变慢（几分钟），期间功能正常；不删项目代码与配置。建议在 IDE 关闭时清理。",
            "可以清，但会换来一次重新索引；空间紧张时值得。",
            CleanupTier.Caution, CleanupMethod.RecycleContents, Path.Combine(local, "JetBrains"));

        Add("npm-cache", "npm 缓存",
            "npm 下载过的包压缩包缓存，安装时先查这里。",
            "用官方 npm cache clean 清理；以后安装会重新下载（需要联网）。不会破坏已装好的项目。",
            "可放心定期清理（需要联网重下）。",
            CleanupTier.Caution, CleanupMethod.OfficialCommand, Path.Combine(local, "npm-cache"), command: "npm-cache-clean");

        Add("pnpm-store", "pnpm 存储",
            "pnpm 的全局内容寻址存储；已装项目的 node_modules 里是硬链接指向它。",
            "绝不能直接删目录——那会破坏所有已装项目（包括 DSH 自己的依赖）。这里只执行官方 pnpm store prune，仅移除没有任何项目引用的包。",
            "可以 prune，安全；不要用别的工具删它。",
            CleanupTier.Caution, CleanupMethod.OfficialCommand, Path.Combine(local, "pnpm", "store"), command: "pnpm-store-prune");

        Add("yarn-cache", "Yarn 缓存",
            "Yarn v1 下载过的包缓存。",
            "安装时会重新下载；不影响已装项目。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Yarn", "Cache"));

        Add("pip-cache", "Python pip 缓存",
            "pip 下载的 wheel/sdist 缓存。",
            "以后安装会重新下载；已安装的包不受影响。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "pip", "Cache"));

        Add("nuget-v3", "NuGet HTTP 缓存",
            "NuGet 的 HTTP 响应缓存（包本体缓存在 ~/.nuget/packages，二者不同）。",
            "只是网络缓存，还原项目时会重新请求。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "NuGet", "v3-cache"));

        Add("go-build", "Go 构建缓存",
            "go build/test 的编译产物缓存。",
            "下次编译会重新编译，变慢；用官方 go clean -cache 更规范。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "go-build"));

        Add("gradle-caches", "Gradle 构建缓存",
            "Gradle 依赖与构建缓存。",
            "下次构建会重新下载依赖并重跑任务，比较耗时；不影响源码。",
            "可以清，但下次构建明显变慢。",
            CleanupTier.Caution, CleanupMethod.RecycleContents, Path.Combine(profile, ".gradle", "caches"));

        Add("cargo-cache", "Rust Cargo 下载缓存",
            "cargo 下载的 crate 压缩包缓存（registry/cache）。",
            "下次构建会重新下载；已编译产物不在这里。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(profile, ".cargo", "registry", "cache"));

        Add("vscode-cacheddata", "VS Code 编译缓存",
            "VS Code 的 CachedData（扩展与内置脚本的编译缓存）。",
            "会重建；首次启动略慢，不丢设置与插件。",
            "可放心定期清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "Code", "CachedData"));

        Add("vscode-cache", "VS Code 普通缓存",
            "VS Code 的 Cache 目录。",
            "会重建，不影响设置与插件。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "Code", "Cache"));

        Add("vscode-logs", "VS Code 日志",
            "VS Code 的运行日志。",
            "只影响历史排查记录。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "Code", "logs"));

        Add("scoop-cache", "Scoop 安装包缓存",
            "Scoop 下载的安装包缓存。",
            "重新安装时会重新下载。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(profile, "scoop", "cache"));

        // ---------------------------------------------------------------- 通讯与其它
        Add("discord-cache", "Discord 缓存",
            "Discord 的图片/表情等媒体缓存。",
            "会重新下载；不删聊天记录（那些在服务器端）。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "discord", "Cache"));

        Add("slack-cache", "Slack 缓存",
            "Slack 的媒体与资源缓存。",
            "会重新下载，不影响账号与消息。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "Slack", "Cache"));

        Add("teams-cache", "Teams 缓存",
            "Teams 的媒体缓存。",
            "会重新下载。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(roaming, "Microsoft", "Teams", "Cache"));

        Add("wer-user", "Windows 错误报告（队列）",
            "程序崩溃时收集的诊断报告，准备上报给微软。",
            "除非要排查某个崩溃，否则没有保留价值。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"));

        Add("wer-archive", "Windows 错误报告（归档）",
            "已归档的错误报告。",
            "同上，仅用于排查历史崩溃。",
            "可放心清理。",
            CleanupTier.Safe, CleanupMethod.RecycleContents, Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"));

        Add("windows-fontcache", "字体缓存",
            "系统字体索引缓存（被字体缓存服务持有）。",
            "会重建，但文件通常被服务占用；强删可能导致字体显示异常。",
            "不建议清理。",
            CleanupTier.HandsOff, CleanupMethod.ReportOnly, Path.Combine(local, "FontCache"));

        // ---------------------------------------------------------------- 需要管理员
        Add("win-temp", "系统临时文件（Windows\\Temp）",
            "系统服务与安装程序用的临时目录。",
            "被占用的会被跳过；其中一部分需要管理员权限才能清理。",
            "需要管理员；清理前建议先重启一次以减少占用。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "Temp"));

        Add("win-update", "Windows 更新缓存",
            "已下载并安装过的更新安装包。",
            "删除不会卸载更新，也不影响系统；清理需要管理员权限。",
            "需管理员；可清，通常很可观。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "SoftwareDistribution", "Download"));

        Add("win-cbs-logs", "Windows 组件日志（CBS）",
            "组件安装/修复过程的日志。",
            "只影响历史排错；被服务占用时清不掉。",
            "需管理员；可清。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "Logs", "CBS"));

        Add("memory-dmp", "内核内存转储 MEMORY.DMP",
            "系统蓝屏时写下的完整内核内存快照。",
            "几百 MB 到数 GB；除非在分析蓝屏，否则没有价值。",
            "需管理员；建议用系统「磁盘清理」更规范。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "MEMORY.DMP"));

        Add("minidump", "小型转储 Minidump",
            "蓝屏时写下的小型转储文件。",
            "同上，仅用于排错。",
            "需管理员；可清。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "Minidump"));

        Add("delivery-optimization", "传递优化缓存",
            "Windows 在局域网/互联网间分发更新的 P2P 缓存。",
            "会重新下载；可能影响其它机器从你这台取更新。",
            "需管理员；可清。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization"));

        Add("windows-old", "旧版 Windows（Windows.old）",
            "系统大版本升级前保留的旧系统目录，用于回滚。",
            "删除后无法回退到升级前的版本。若升级已稳定运行一段时间（如 10 天以上），可以删。",
            "需管理员且不可逆；确认不再需要回滚后再删。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, "C:\\Windows.old");

        // WinSxS must not be walked: it is hard-linked, so its directory size is
        // meaningless, and walking 114k files took the whole scan past 30 seconds.
        // The real reclaimable amount comes from DISM /AnalyzeComponentStore.
        t.Add(new CleanupTarget("dism-component", "组件存储清理（DISM）",
            "WinSxS 中已被更新取代的旧组件备份。目录大小没有参考价值（大量硬链接），真实可回收量要用 DISM 分析。",
            "用官方 DISM /StartComponentCleanup 移除被取代的组件；清理后无法卸载已安装的更新。系统稳定性不受影响，但这一步不可逆。",
            "需管理员的官方命令；收益常达数 GB（本机 WinSxS 有 12.6 GB，实际可回收量需分析），建议一年做一次。",
            CleanupTier.AdminOnly, CleanupMethod.ReportOnly, Path.Combine(win, "WinSxS"),
            null, "dism-cleanup", 0, GroupOf(CleanupTier.AdminOnly), true));

        Add("prefetch", "Prefetch 预读文件",
            "系统记录的程序启动特征，用来加速下次启动。",
            "删掉不会坏系统，但下次启动会变慢——它本来就是性能优化数据。",
            "不建议清理；留着对速度更好。",
            CleanupTier.HandsOff, CleanupMethod.ReportOnly, Path.Combine(win, "Prefetch"));

        Add("webcache", "WebCache（含上网历史）",
            "WinINet 的浏览历史与 Cookie 数据库（WebCacheV01.dat）。",
            "会清掉上网历史，且通常被系统进程占用。想清历史请用浏览器的隐私设置，而不是删这个数据库。",
            "不建议清理。",
            CleanupTier.HandsOff, CleanupMethod.ReportOnly, Path.Combine(local, "Microsoft", "Windows", "WebCache"));

        Add("recycle-bin", "回收站",
            "你删除的文件暂存在这里。",
            "清空后无法还原；里面可能还有你想找回的东西。",
            "自己确认后再清；只在你明确点击那一行时执行。",
            CleanupTier.Caution, CleanupMethod.EmptyRecycleBin, "");

        return t;
    }

    /// <summary>Report-only entries still get measured, so the user can see their size.</summary>
    public static bool Measurable(CleanupTarget target) =>
        target.Method is CleanupMethod.ReportOnly or CleanupMethod.EmptyRecycleBin
            ? target.Method == CleanupMethod.ReportOnly
            : true;

    private static string GroupOf(CleanupTier tier) => tier switch
    {
        CleanupTier.Safe => "放心清理（系统或软件会自动重建）",
        CleanupTier.Caution => "谨慎清理（会重建或需重新下载）",
        CleanupTier.AdminOnly => "需要管理员权限（只报告）",
        _ => "不建议清理（只报告原因）",
    };

    /// <summary>An absolute path that is allowed to be cleaned: inside the user profile.</summary>
    public static bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        var profile = Path.GetFullPath(UserProfile).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuse dangerous roots outright, even if they somehow pass IsAllowedPath.
    /// The Windows check is a PREFIX test: matching the segment "Windows" anywhere
    /// wrongly rejected %LOCALAPPDATA%\Microsoft\Windows\Explorer (the thumbnail cache).
    /// </summary>
    public static bool IsForbiddenTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); } catch { return true; }
        if (full.Length <= 3) return true;                                  // "C:\" and similar
        foreach (var special in new[] { UserProfile, LocalAppData, RoamingAppData, ProgramData })
        {
            var s = Path.GetFullPath(special).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, s, StringComparison.OrdinalIgnoreCase)) return true;
        }

        var windows = WindowsDir.TrimEnd(Path.DirectorySeparatorChar);
        if (full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var segment in new[] { "node_modules", ".git", "System32" })
            if (full.Contains(Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool MayExecute(CleanupTarget target) =>
        target.Tier != CleanupTier.AdminOnly && target.Tier != CleanupTier.HandsOff
        && target.Method != CleanupMethod.ReportOnly;

    /// <summary>
    /// Admin-only targets the ELEVATED helper is allowed to clean. The list is fixed
    /// here and the elevated process is told a target ID (never a path), so elevation
    /// cannot be turned into an arbitrary delete.
    /// </summary>
    private static readonly HashSet<string> ElevatedAllowed = new(StringComparer.Ordinal)
    {
        // Windows Update cache, CBS logs, kernel/user dumps, delivery optimization, and
        // the component store (which runs the official DISM command, never a hand delete).
        // Windows.old is deliberately NOT here: removing it is irreversible.
        "win-update", "win-cbs-logs", "memory-dmp", "minidump", "delivery-optimization", "dism-component",
    };

    public static bool MayExecuteElevated(CleanupTarget target) =>
        target.Tier == CleanupTier.AdminOnly && ElevatedAllowed.Contains(target.Id);

    /// <summary>Admin-only rows whose cleanup is an official command (DISM etc.).</summary>
    public static bool IsOfficialAdminCommand(CleanupTarget target) =>
        target.Tier == CleanupTier.AdminOnly && !string.IsNullOrEmpty(target.Command);

    /// <summary>Every target that a "clean all safe items" run may touch, in one place.</summary>
    public static IReadOnlyList<CleanupTarget> SafeBatch(int tempAgeDays = DefaultTempAgeDays) =>
        Build(tempAgeDays).Where(t => t.SafeRegularly).ToList();
}
