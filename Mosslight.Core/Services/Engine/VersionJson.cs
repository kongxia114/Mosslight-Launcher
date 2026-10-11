using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  版本 JSON：读取 + inheritsFrom 合并
//
//  移植自 1.0.0 的 core/launch.py 中段。这一层只认 JSON，不碰磁盘上的 jar。
//
//  为什么值得单独一个文件：`inheritsFrom` 的合并规则看着简单，但有三处
//  「简化了就错」的地方（库列表顺序、客户端 jar 用哪一份、老版本的重复库），
//  集中在这里比散在启动流程里好查。
// ============================================================================

/// <summary>
/// 读进来的版本 JSON（**已合并继承链**）。
///
/// ⚠️ 是 `record` 而不是 `class`：合并之后要补一处 `Jar`（见 <see cref="VersionJsonLoader.Load"/>
///    里 OptiFine 那段），用 `with` 比手写一个拷贝构造函数安全 —— 以后加字段不会漏拷。
/// </summary>
public sealed record VersionJson
{
    public string Id { get; init; } = string.Empty;

    public string MainClass { get; init; } = "net.minecraft.client.main.Main";

    public string Type { get; init; } = "release";

    /// <summary>`javaVersion.majorVersion`；老版本 JSON 没有这个字段 → 0。</summary>
    public int JavaMajor { get; init; }

    /// <summary>
    /// 客户端 jar 用哪个版本目录的。
    ///
    /// 取值顺序：子版本显式写的 `jar` → 父版本已经定好的 → 父版本的 id。
    /// 最坏情况下等于自己的 id（自己目录里那个 `<id>.jar`）。
    /// </summary>
    public string Jar { get; init; } = string.Empty;

    /// <summary>合并后的 JVM 参数（原始 JSON 节点，占位符还没替换）。</summary>
    public JsonArray JvmArguments { get; init; } = new();

    /// <summary>合并后的游戏参数（原始 JSON 节点）。</summary>
    public JsonArray GameArguments { get; init; } = new();

    /// <summary>老格式（1.12 及以前）：一整条参数字符串，不是数组。</summary>
    public string? MinecraftArguments { get; init; }

    public JsonArray Libraries { get; init; } = new();

    public string? AssetIndexId { get; init; }

    public string? Assets { get; init; }

    /// <summary>`downloads.client` 的 url / sha1 / size（安装时要用）。</summary>
    public JsonObject? Downloads { get; init; }

    /// <summary>log4j 配置（`logging.client`）。</summary>
    public string? LoggingArgument { get; init; }

    public string? LoggingFileId { get; init; }

    public string? ReleaseTime { get; init; }

    /// <summary>原始（未合并的）JSON —— 安装器要按它下载，不能用合并后的。</summary>
    public JsonObject Raw { get; init; } = new();

    /// <summary>`assetIndex` 那个对象（安装时要用它的 url / sha1）。</summary>
    public JsonObject? AssetIndexNode { get; init; }

    /// <summary>
    /// `logging` 那个对象（合并后）。
    ///
    /// ⚠️ 单独留一份而不是从 <see cref="Raw"/> 里挖：Raw 只有**这一层**的 JSON，
    ///    三层的继承链（加载器 → 中间版本 → 原版）里 logging 常常在最底下那层，
    ///    从 Raw 挖会漏掉，于是 log4j 的配置参数就不加了。
    /// </summary>
    public JsonObject? LoggingNode { get; init; }

    /// <summary>
    /// 这一版（合并后）有没有一个**非空**的 `arguments` 对象。
    ///
    /// ⚠️ 合并时判的是"有没有整个 arguments 对象"，不是"jvm/game 数组空不空" ——
    ///    Python 那边写的是 `if child_args and parent_args:`，
    ///    空字典 `{}` 是假值。用数组判会在 `"arguments": {}` 这种 JSON 上分叉。
    /// </summary>
    public bool HasArguments { get; init; }

    public string? InheritsFrom { get; init; }
}

/// <summary>版本 JSON 的读取与合并。</summary>
public static class VersionJsonLoader
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>版本 JSON 的路径：`&lt;mc_dir&gt;/versions/&lt;id&gt;/&lt;id&gt;.json`。</summary>
    public static string JsonPath(string mcDir, string versionId)
        => Path.Combine(mcDir, "versions", versionId, versionId + ".json");

    /// <summary>版本目录：`&lt;mc_dir&gt;/versions/&lt;id&gt;`。</summary>
    public static string VersionDir(string mcDir, string versionId)
        => Path.Combine(mcDir, "versions", versionId);

    /// <summary>读版本 JSON（**不**处理继承链，扫描列表时用这个）。读不出来返回 null。</summary>
    public static JsonObject? LoadRaw(string mcDir, string versionId)
    {
        var path = JsonPath(mcDir, versionId);
        try
        {
            // 读字节而不是文本：**BOM 交给 JsonIO 处理**。
            // ⚠️ 不能直接 `JsonNode.Parse(bytes)` —— 它内部的 Utf8JsonReader
            //    **不认识 BOM**，带 BOM 的版本 JSON（记事本改一下就成这样）
            //    会第一句就报「0xEF is an invalid start of a value」。
            //    1.0.0 那边用的是 `encoding="utf-8-sig"`，防的就是这件事。
            return JsonIO.ParseBytes(File.ReadAllBytes(path), DocOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读版本 JSON 并把 inheritsFrom 链合并掉。
    ///
    /// 链太深或者某一环的 JSON 不存在都抛 <see cref="VersionJsonException"/> ——
    /// 这两个都是"真出问题了"，不该静默吞掉（扫描器那边有它自己的容错路径）。
    /// </summary>
    public static VersionJson Load(string mcDir, string versionId)
    {
        var cache = new Dictionary<string, VersionJson>(StringComparer.Ordinal);

        VersionJson LoadOne(string vid, int depth)
        {
            if (cache.TryGetValue(vid, out var cached))
            {
                return cached;
            }
            if (depth > LaunchRules.MaxInheritDepth)
            {
                throw new VersionJsonException(Localization.Localizer.Tr(
                    "版本继承链过深（超过 {n} 层）：{vid}",
                    ("n", LaunchRules.MaxInheritDepth), ("vid", vid)));
            }

            var raw = LoadRaw(mcDir, vid)
                ?? throw new VersionJsonException(Localization.Localizer.Tr(
                    "版本 JSON 不存在：{path}", ("path", JsonPath(mcDir, vid))));

            var parentId = raw["inheritsFrom"] is JsonValue parentNode &&
                           parentNode.GetValueKind() == JsonValueKind.String
                ? parentNode.GetValue<string>()
                : null;

            VersionJson merged;
            if (!string.IsNullOrEmpty(parentId))
            {
                merged = Merge(LoadOne(parentId, depth + 1), raw, vid);
            }
            else
            {
                // 没有父版本，但 JSON 自己可能就写了重复项（老版本常见），照样去重
                merged = Merge(null, raw, vid);
            }

            // ⚠️ 继承链合出来的 `jar` 只看了 **JSON 字段**，漏了"自己目录里就躺着一个
            // `<id>.jar`"这种情况 —— 而官方启动器的规矩是**自己目录里那个优先**。
            //
            //   OptiFine 就是踩这个：它的 JSON 是 `inheritsFrom: 1.20.1`、**没有** jar
            //   字段，但自己目录里有一个 23 MB 的 `<id>.jar`（打过补丁的客户端）。
            //   照 Merge 那条回退会退回父版本的原版 jar —— 等于 OptiFine 白装了
            //   （进游戏一看没有光影），而且**一声不响**。
            //
            // 只在 JSON **没有**明确写 `jar` 时才补这一刀：显式写了就听它的
            // （Forge 1.16- 那种 `"jar": "<自己的目录名>"` 的写法不能被顶掉）。
            var rawHasJar = raw["jar"] is JsonValue jarNode &&
                            jarNode.GetValueKind() == JsonValueKind.String &&
                            !string.IsNullOrEmpty(jarNode.GetValue<string>());
            if (!rawHasJar && File.Exists(Path.Combine(VersionDir(mcDir, vid), vid + ".jar")))
            {
                merged = merged with { Jar = vid };
            }

            cache[vid] = merged;
            return merged;
        }

        return LoadOne(versionId, 0);
    }

    /// <summary>只读 + 合并，读不出来返回 null（扫描器 / 界面用这个，不抛异常）。</summary>
    public static VersionJson? TryLoad(string mcDir, string versionId)
    {
        try
        {
            return Load(mcDir, versionId);
        }
        catch (VersionJsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 合并两个版本的库列表，同名以后出现的为准。
    ///
    /// 顺序也保持：父版本在前，子版本覆盖的同名库**留在原位置**（官方启动器就是这么做的，
    /// Forge 的 `-DignoreList` 依赖 classpath 顺序）。
    /// </summary>
    private static JsonArray SortedLibraries(JsonArray? parent, JsonArray? child)
    {
        var merged = new JsonArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var source in new[] { parent, child })
        {
            if (source is null)
            {
                continue;
            }
            foreach (var node in source)
            {
                if (node is not JsonObject lib)
                {
                    continue;
                }
                var name = lib["name"] is JsonValue nameNode &&
                           nameNode.GetValueKind() == JsonValueKind.String
                    ? nameNode.GetValue<string>()
                    : string.Empty;

                if (name.Length > 0 && index.TryGetValue(name, out var at))
                {
                    merged[at] = lib.DeepClone();
                }
                else
                {
                    index[name] = merged.Count;
                    merged.Add(lib.DeepClone());
                }
            }
        }
        return merged;
    }

    private static JsonArray Concat(JsonArray? first, JsonArray? second)
    {
        var result = new JsonArray();
        foreach (var source in new[] { first, second })
        {
            if (source is null)
            {
                continue;
            }
            foreach (var node in source)
            {
                result.Add(node?.DeepClone());
            }
        }
        return result;
    }

    /// <summary>
    /// 取一个字符串字段；对象本身是 null、或者那个键不是字符串，都返回 null。
    ///
    /// ⚠️ 参数是 `JsonObject?` 而**不是** `JsonObject`：调用点有不少是
    ///    "这个键可能整段都不存在"（`assetIndex` / `logging` / 继承来的父版本…）。
    ///    写成非空参数的话，那些地方只能靠 `!` 硬压住编译器 —— 而 `!` 只是
    ///    让它别报警告，**运行时该 NRE 还是 NRE**。这里收成可空，调用点就安全了。
    /// </summary>
    private static string? Str(JsonObject? obj, string key)
        => obj is not null && obj[key] is JsonValue value
           && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static JsonArray? Arr(JsonObject obj, string key) => obj[key] as JsonArray;

    /// <summary>
    /// 把子版本合到父版本上（处理 inheritsFrom）。
    /// 子版本的字段优先；libraries 合并去重；arguments 两边拼起来。
    /// </summary>
    private static VersionJson Merge(VersionJson? parent, JsonObject child, string fallbackId)
    {
        var childArgs = child["arguments"] as JsonObject;
        // Python 的 `if child_args and parent_args:` —— 空字典算"没有"
        var childHasArgs = childArgs is { Count: > 0 };
        var parentHasArgs = parent?.HasArguments ?? false;

        JsonArray jvmArgs, gameArgs;
        bool hasArguments;
        if (childHasArgs && parentHasArgs)
        {
            jvmArgs = Concat(parent!.JvmArguments, Arr(childArgs!, "jvm"));
            gameArgs = Concat(parent.GameArguments, Arr(childArgs!, "game"));
            hasArguments = true;
        }
        else if (childHasArgs)
        {
            // 父版本没有 arguments → 整份用子版本的（**不是**拼接）
            jvmArgs = Concat(null, Arr(childArgs!, "jvm"));
            gameArgs = Concat(null, Arr(childArgs!, "game"));
            hasArguments = true;
        }
        else if (parentHasArgs)
        {
            jvmArgs = Concat(parent!.JvmArguments, null);
            gameArgs = Concat(parent.GameArguments, null);
            hasArguments = true;
        }
        else
        {
            jvmArgs = new JsonArray();
            gameArgs = new JsonArray();
            hasArguments = false;
        }

        // downloads：父的当前提，子版本覆盖同名键
        var downloads = new JsonObject();
        if (parent?.Downloads is JsonObject parentDownloads)
        {
            foreach (var (key, value) in parentDownloads)
            {
                downloads[key] = value?.DeepClone();
            }
        }
        if (child["downloads"] is JsonObject childDownloads)
        {
            foreach (var (key, value) in childDownloads)
            {
                downloads[key] = value?.DeepClone();
            }
        }

        var childJar = Str(child, "jar");
        var parentId = parent?.Id ?? fallbackId;
        var jar = !string.IsNullOrEmpty(childJar)
            ? childJar
            : (!string.IsNullOrEmpty(parent?.Jar) ? parent!.Jar : parentId);

        var assetIndex = child["assetIndex"] as JsonObject ?? parent?.AssetIndexNode;
        // ⚠️ logging 取**合并后**的那一份，不是从父版本的 Raw 挖 ——
        //    三层继承链里 logging 常常在最底下那层，挖 Raw 会漏
        var logging = child["logging"] as JsonObject ?? parent?.LoggingNode;
        // ⚠️ 真正要的参数在 `logging.client` 里，**不是** `logging` 本身。
        //    少读这一层的话 log_arg 永远是 null，于是 `-Dlog4j.configurationFile=…`
        //    根本不会加 —— 游戏照常能起，只是 1.7~1.17 的日志格式退回默认，
        //    一声不响。金标准里就是靠"命令行少一项"抓出来的。
        var loggingClient = logging?["client"] as JsonObject;

        return new VersionJson
        {
            Id = Str(child, "id") ?? parent?.Id ?? fallbackId,
            MainClass = Str(child, "mainClass") ?? parent?.MainClass ?? "net.minecraft.client.main.Main",
            Type = Str(child, "type") ?? parent?.Type ?? "release",
            JavaMajor = ReadJavaMajor(child) is var cm && cm > 0
                ? cm
                : (parent?.JavaMajor ?? 0),
            Jar = jar!,
            JvmArguments = jvmArgs,
            GameArguments = gameArgs,
            HasArguments = hasArguments,
            MinecraftArguments = Str(child, "minecraftArguments") ?? parent?.MinecraftArguments,
            Libraries = SortedLibraries(parent?.Libraries, Arr(child, "libraries")),
            // ⚠️ 这里**不能**写 `assetIndex!`：版本 JSON 没有 assetIndex 段时它是 null，
            //    而 `!` 只是让编译器闭嘴 —— 运行时照样 NRE（真被自检的
            //    "造一个最小可启动版本"那节抓出来过）。Str 收可空参数就没事了。
            AssetIndexId = Str(assetIndex, "id") ?? parent?.AssetIndexId,
            Assets = Str(child, "assets") ?? parent?.Assets,
            Downloads = downloads,
            LoggingArgument = loggingClient is null ? null : Str(loggingClient, "argument"),
            LoggingFileId = loggingClient?["file"] is JsonObject file ? Str(file, "id") : null,
            LoggingNode = logging,
            ReleaseTime = Str(child, "releaseTime") ?? parent?.ReleaseTime,
            Raw = (JsonObject)child.DeepClone(),
            AssetIndexNode = assetIndex,
            InheritsFrom = Str(child, "inheritsFrom"),
        };
    }

    private static int ReadJavaMajor(JsonObject obj)
    {
        if (obj["javaVersion"] is not JsonObject javaVersion)
        {
            return 0;
        }
        if (javaVersion["majorVersion"] is not JsonValue value)
        {
            return 0;
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<int>(),
            JsonValueKind.String => int.TryParse(value.GetValue<string>(), out var parsed) ? parsed : 0,
            _ => 0,
        };
    }
}

/// <summary>版本 JSON 有问题（不存在 / 继承链太深 / 格式坏了）。</summary>
public sealed class VersionJsonException : Exception
{
    public VersionJsonException(string message) : base(message)
    {
    }
}
