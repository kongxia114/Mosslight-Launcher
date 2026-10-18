using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

//     ⚠️ 数据里的 `file` 字段**不能拿去拼路径**：1.7.10 那一份写着
//     ⚠️ 也别拿"这个路径能 200"去验证别的文件名：
// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-liteloadercatalog-cs

/// <summary>LiteLoader 的版本列表 + 版本 JSON 拼装（纯数据，不联网的部分都在这儿）。</summary>
public static class LiteLoaderCatalog
{
    /// <summary>版本清单（**一个文件管所有 MC 版本**，跟别家"按 MC 版本查"不一样）。</summary>
    public const string VersionsUrl = "https://dl.liteloader.com/versions/versions.json";

    /// <summary>
    /// 库根地址（标准 maven 布局，只是根不在 maven central 上）。
    /// 真实文件路径 = `{RepoBase}com/mumfrey/liteloader/{version}/{file}`。
    /// </summary>
    public const string RepoBase = "https://dl.liteloader.com/versions/";

    /// <summary>launchwrapper 的主类 —— LiteLoader 版本 JSON 的 mainClass 就是它。</summary>
    public const string LaunchMainClass = "net.minecraft.launchwrapper.Launch";

    /// <summary>Mojang 的库根（launchwrapper 在它上面）。</summary>
    private const string MinecraftLibraries = "https://libraries.minecraft.net/";

    /// <summary>Maven Central（asm-all / jopt-simple 在它上面）。</summary>
    private const string MavenCentral = "https://repo1.maven.org/maven2/";

    /// <summary>
    /// 从 versions.json 里挑出**指定版本**那一节（安装时用：安装不能信界面那份数据）。
    ///
    /// 只认 `artefacts`（正式版）—— 跟 <see cref="ParseVersions"/> 同一个口径；
    /// 列表里没有的版本这里也不会给，两边不会出现"界面上没有、却能装"的怪状态。
    /// </summary>
    public static JsonObject? PickArtefact(JsonNode? data, string mcVersion, string loaderVersion)
    {
        if (data is not JsonObject root
            || root["versions"] is not JsonObject versions
            || versions[(mcVersion ?? string.Empty).Trim()] is not JsonObject entry
            || entry["artefacts"] is not JsonObject artefacts
            || artefacts["com.mumfrey:liteloader"] is not JsonObject liteloader)
        {
            return null;
        }

        foreach (var (_, node) in liteloader)
        {
            if (node is JsonObject artefact
                && string.Equals(Str(artefact, "version"), (loaderVersion ?? string.Empty).Trim(),
                    StringComparison.Ordinal))
            {
                return artefact;
            }
        }
        return null;
    }

    /// <summary>
    /// `versions.json` + 一个 MC 版本 → 这个游戏版本能装的 LiteLoader（**只列正式版**）。
    ///
    /// 版本号用 JSON 里的 `version`（`1.7.10_04` 这种，是 LiteLoader 自己的编号），
    /// 文件名用 `file`（`liteloader-1.7.10.jar`）—— 两者**不是一回事**，都要留着。
    /// </summary>
    public static List<LoaderVersion> ParseVersions(JsonNode? data, string mcVersion)
    {
        var rows = new List<LoaderVersion>();
        if (data is not JsonObject root
            || root["versions"] is not JsonObject versions
            || versions[(mcVersion ?? string.Empty).Trim()] is not JsonObject entry
            || entry["artefacts"] is not JsonObject artefacts
            || artefacts["com.mumfrey:liteloader"] is not JsonObject liteloader)
        {
            return rows;
        }

        // "latest" 跟某个 md5 键指向**同一份**文件，按版本号去重
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, node) in liteloader)
        {
            if (node is not JsonObject artefact)
            {
                continue;
            }
            var version = Str(artefact, "version");
            if (string.IsNullOrEmpty(version) || !seen.Add(version))
            {
                continue;
            }

            var file = Str(artefact, "file");
            if (string.IsNullOrEmpty(file))
            {
                file = $"liteloader-{version}.jar";
            }

            rows.Add(new LoaderVersion(
                Version: version,
                Modified: TimestampText(Str(artefact, "timestamp")),
                // 正式版那边 stream 都是 RELEASE；万一有别的就当测试版显示
                Stable: string.Equals(Str(artefact, "stream"), "RELEASE", StringComparison.OrdinalIgnoreCase),
                FileName: file,
                InstallerHash: Str(artefact, "md5")));
        }

        // 版本号形如 `1.7.10_04`：主版本在前、编译号在后 —— 按数值段倒序
        rows.Sort((x, y) =>
            LoaderCatalog.CompareVersionKeys(
                LoaderCatalog.VersionKey(y.Version), LoaderCatalog.VersionKey(x.Version)));
        return rows;
    }

    /// <summary>
    /// LiteLoader 那个 jar 在库目录里的相对路径
    /// （`com/mumfrey/liteloader/{version}/liteloader-{version}.jar`）。
    ///
    /// ⚠️ 文件名里的版本号跟目录里那个**是同一个值**，别用数据里的 `file` 字段
    ///    （1.7.10 那份写的是 `liteloader-1.7.10.jar`，而服务器上叫
    ///    `liteloader-1.7.10_04.jar` —— 照 `file` 拼是 404）。
    /// </summary>
    public static string JarRelativePath(string loaderVersion)
    {
        var version = (loaderVersion ?? string.Empty).Trim();
        return $"com/mumfrey/liteloader/{version}/liteloader-{version}.jar";
    }

    /// <summary>
    /// 造一份 LiteLoader 的版本 JSON（`inheritsFrom` 原版，只补加载器要的那几样）。
    ///
    /// ⚠️ `minecraftArguments` 是**整串覆盖**的（`VersionJson.Merge` 那边子版本优先，
    ///    不是拼接）：所以这里必须把原版那一串原样带上，再在末尾加 `--tweakClass`。
    ///    少了原版那串，游戏会以"没登录 / 没窗口"之类的怪状态起来 —— 报错离原因很远。
    /// ⚠️ 库列表用**数据里自带的那几条**（launchwrapper / asm-all / jopt-simple 的版本
    ///    每个游戏版本都不一样，自己维护一张表迟早对不上），再加 LiteLoader 自己的 jar。
    /// ⚠️ LiteLoader 的 jar 走 `downloads.artifact.path` 指到我们自己定的路径：
    ///    它的 maven 坐标里 artifact 名跟文件名对不上（`liteloader-1.7.10_04.jar` 并不存在），
    ///    靠 `name` 推路径会推错文件。
    /// </summary>
    public static JsonObject BuildVersionJson(
        string mcVersion, string versionId, JsonObject artefact, string parentMinecraftArguments)
    {
        var mc = (mcVersion ?? string.Empty).Trim();
        var loaderVersion = Str(artefact, "version");
        _ = Str(artefact, "file");   // 留着只为提醒：这个字段不能拿去拼路径（见类注释）

        var libraries = new JsonArray();
        foreach (var node in artefact["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib || lib["name"] is not JsonValue nameNode
                || nameNode.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }
            // 设计说明见 docs/design-notes.md#mosslight-core-services-engine-liteloadercatalog-cs
            var url = (lib["url"]?.GetValue<string>() ?? string.Empty).Trim();
            libraries.Add(new JsonObject
            {
                ["name"] = name,
                ["url"] = url.Length > 0 ? url : DefaultLibraryRoot(name),
            });
        }

        var relative = JarRelativePath(loaderVersion);
        libraries.Add(new JsonObject
        {
            ["name"] = $"com.mumfrey:liteloader:{loaderVersion}",
            ["url"] = RepoBase,
            ["downloads"] = new JsonObject
            {
                // ⚠️ 这里**只写 path 和 url**，不写 sha1 / size：
                // 设计说明见 docs/design-notes.md#mosslight-core-services-engine-liteloadercatalog-cs
                ["artifact"] = new JsonObject
                {
                    ["path"] = relative,
                    ["url"] = RepoBase + relative,
                },
            },
        });

        var arguments = parentMinecraftArguments.Trim();
        if (arguments.Length > 0)
        {
            arguments += " ";
        }
        // ⚠️ tweakClass 必须用数据里那一份（它是加载器自己的注入点，写死也没错但不该写死）
        var tweakClass = Str(artefact, "tweakClass");
        arguments += "--tweakClass " +
                     (tweakClass.Length > 0 ? tweakClass : "com.mumfrey.liteloader.launch.LiteLoaderTweaker");

        return new JsonObject
        {
            ["id"] = versionId,
            ["inheritsFrom"] = mc,
            ["type"] = "release",
            ["mainClass"] = LaunchMainClass,
            ["minecraftArguments"] = arguments,
            // ⚠️ 显式给 `jar`：LiteLoader 走的是**原版**客户端 jar（它只是往里注入），
            //    不写的话合并时会退回"自己的 id"，于是去找
            //    versions/<实例名>/<实例名>.jar —— 那个文件不存在。
            ["jar"] = mc,
            ["libraries"] = libraries,
            // 只是给人看的（PCL 也会在版本信息里写这句）
            ["_comment"] = $"LiteLoader {loaderVersion} for {mc}",
        };
    }

    /// <summary>按 maven 坐标猜主仓库（数据里没写 url 时用）。</summary>
    private static string DefaultLibraryRoot(string name)
        => name.StartsWith("net.minecraft:", StringComparison.Ordinal)
           || name.StartsWith("com.mojang:", StringComparison.Ordinal)
            ? MinecraftLibraries
            : MavenCentral;

    /// <summary>unix 秒 → `2026-09-15 11:23`（拿不到就空串，不猜）。</summary>
    private static string TimestampText(string raw)
    {
        if (!long.TryParse(raw, out var seconds) || seconds <= 0)
        {
            return string.Empty;
        }
        return DateTimeOffset.FromUnixTimeSeconds(seconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm");
    }

    private static string Str(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : string.Empty;
}
