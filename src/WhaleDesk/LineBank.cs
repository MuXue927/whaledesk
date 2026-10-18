namespace WhaleDesk;

/// <summary>
/// The pet's own lines, used for interactions DSH knows nothing about (drag, snap,
/// tray, idle). Lines coming from DSH (/api/pet/state bubble) always win, so her
/// "personality" stays consistent with the in-app pet; this bank only fills the gaps.
/// </summary>
internal sealed class LineBank
{
    private readonly Dictionary<string, long> _lastUsed = new();
    private readonly Random _random = new();

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["pet"] = new[]
        {
            "唔……再摸一下下",
            "嘿嘿，今天也要加油哦",
            "别摸啦，痒痒的",
            "收到摸摸，能量 +1",
        },
        ["feed"] = new[]
        {
            "小鱼干！最喜欢了",
            "嚼嚼……谢谢老板",
            "有吃的就有力气干活",
        },
        ["drag"] = new[]
        {
            "诶诶诶——放我下来！",
            "我要飞起来啦～",
            "轻点轻点，裙子要皱了",
        },
        ["snap"] = new[]
        {
            "就这儿了，视角刚好",
            "贴边站好，不挡你写代码",
            "这个角落归我啦",
        },
        ["idle"] = new[]
        {
            "有点无聊，你在忙什么？",
            "要不要我帮你盯着 CPU？",
            "C 盘的缓存又胖了……",
            "我在，随时叫我",
        },
        ["resume"] = new[]
        {
            "我回来啦",
            "继续盯着呢",
        },
        // ---- P5 interactions ----
        ["combo1"] = new[]
        {
            "三连击！你今天心情不错嘛",
            "点这么多下，是有什么好事？",
        },
        ["combo2"] = new[]
        {
            "好啦好啦，我知道你很喜欢我！",
            "再点下去我要冒星星了……",
        },
        ["dizzy"] = new[]
        {
            "@_@ 转晕了…让我缓一下",
            "别点啦别点啦，头晕！",
        },
        ["longpress"] = new[]
        {
            "你…就这样一直摸着我不说话？",
            "被摸久了也会不好意思的……",
        },
        ["overfed"] = new[]
        {
            "嗝——吃不下了，真的吃不下了",
            "小鱼干先存着，等下再吃好不好",
        },
        ["corner"] = new[]
        {
            "这个角落视野真好，我就在这儿扎根了",
            "贴边站好，谁也别想让我挪窝",
        },
        ["latenight"] = new[]
        {
            "两点多了，你也该睡了……我陪你再写一会儿",
            "夜深了，记得喝水，别硬撑",
        },
        ["hourly"] = new[]
        {
            "整点报时：又过了一小时，起来动动？",
            "当当——又是一个整点，进度如何？",
        },
        ["comeback"] = new[]
        {
            "你回来啦！我等了好久",
            "好久没见，还以为你把我忘了",
        },
        ["pomodoro-start"] = new[]
        {
            "番茄钟开始，这 25 分钟交给我看着",
            "专注模式启动，加油！",
        },
        ["pomodoro-break"] = new[]
        {
            "专注结束，去接杯水吧",
            "休息 5 分钟，回来继续",
        },
        ["pomodoro-done"] = new[]
        {
            "一个番茄完成！休息结束，要再来一轮吗",
            "收工啦，今天也很努力",
        },
        ["lowmemory-on"] = new[]
        {
            "我进入省电模式了，安静陪你",
            "内存有点紧，我少吃一点",
        },
        ["lowmemory-off"] = new[]
        {
            "恢复满血状态！",
            "省电模式关掉，我又精神了",
        },
    };

    /// <summary>Pick a line, no more often than once per cooldown in the same category.</summary>
    public string? Pick(string category, long cooldownMs = 4000)
    {
        if (!Lines.TryGetValue(category, out var pool) || pool.Length == 0) return null;
        var now = Environment.TickCount64;
        if (_lastUsed.TryGetValue(category, out var last) && now - last < cooldownMs) return null;
        _lastUsed[category] = now;
        return pool[_random.Next(pool.Length)];
    }

    public static bool Has(string category) => Lines.ContainsKey(category);
}
