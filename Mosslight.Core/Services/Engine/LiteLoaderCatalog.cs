using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  LiteLoader：版本列表 + 自己拼版本 JSON
//
//  ## 为什么不像 Forge 那样跑安装器
//
//  LiteLoader 的安装器是**纯 Swing GUI**：主类 `com.mumfrey.liteloader.installer.Installer`，
//  `main(String[] args)` 压根不看 args，直接 `new InstallerPanel(...)` 然后死等用户点按钮
//  （官方源码 Mumfrey/LiteLoaderInstaller）。在启动器里弹一个要用户自己点的窗口，
//  就等于"一键装好"变成"你自己去点"。
//
//  而且**它也不需要安装器**：LiteLoader 是"launchwrapper + 一个 tweakClass"的加载器，
//  安装器干的事就是往版本 JSON 里写 `mainClass` / `minecraftArguments --tweakClass …`
//  再加几条库。我们照抄这份 JSON 就行 —— PCL 上游（VB 版 `ModDownloadLib.vb`）也是这么装的：
//  `arguments.game = ["--tweakClass", tweakClass]`、`mainClass = net.minecraft.launchwrapper.Launch`、
//  `inheritsFrom` + `jar` 指原版，库 = versions.json 里那几条 + 追加一条 liteloader 自己的 jar。
//
//  ## versions.json 的形状（实测 `https://dl.liteloader.com/versions/versions.json`）
//
//      { "meta": {...},
//        "versions": {
//          "1.8": {
//            "repo": {"stream":"RELEASE","type":"ivy","url":"http://dl.liteloader.com/versions/"},
//            "artefacts": {                       // ← 正式版（有它就代表这个 MC 版本发过正式版）
//              "com.mumfrey:liteloader": {
//                "6e9028…":  {"tweakClass":"com.mumfrey.liteloader.launch.LiteLoaderTweaker",
//                             "libraries":[{"name":"net.minecraft:launchwrapper:1.11"}, …],
//                             "stream":"RELEASE","file":"liteloader-1.8.jar",
//                             "version":"1.8","md5":"6e9028…","timestamp":"1463585254"},
//                "latest":   { …同上… }
//              }
//            },
//            "snapshots": {...},                  // ← 只有测试版（1.12.2 只有这一边）
//            "dev": {...}
//          }, …
//        } }
//
//  ## 三个坑
//
//  1. **只有 `artefacts`（正式版）能用**。`snapshots` 那批（1.8.9 / 1.9 / 1.10 / 1.11 /
//     1.12 / 1.12.1 / 1.12.2）的库列表里带着 `org.spongepowered:mixin:0.7.4-SNAPSHOT`，
//     而 spongepowered 那个快照仓库现在 404 —— 装了也起不来。所以列表里只列正式版。
//  2. **jar 的真实路径是 `{repo.url}com/mumfrey/liteloader/{version}/liteloader-{version}.jar`**
//     —— 用 LiteLoader **自己那个版本号**，两段用同一个值。
//     ⚠️ 数据里的 `file` 字段**不能拿去拼路径**：1.7.10 那一份写着
//        `file: "liteloader-1.7.10.jar"`，可服务器上那个目录里只有
//        `liteloader-1.7.10_04.jar`（实测 `…/1.7.10_04/liteloader-1.7.10.jar` 是 **404**）。
//        `file` 只在 1.8 / 1.9.4 / 1.10.2 这几个"版本号刚好等于游戏版本"的地方巧合地对得上。
//     ⚠️ 也别拿"这个路径能 200"去验证别的文件名：
//        `dl.liteloader.com` 对**别的目录**里的任意文件名都会 200（实测），
//        所以只能按上面那条规则拼，拼完再验的那一步说明不了什么。
//  3. **1.8+ 的安装器源（jenkins.liteloader.com）已经死了**（301 → HTTPS 超时），
//     redist 目录只覆盖到 1.7.x。反正我们不跑安装器，这条只是"别去找它"的备注。
// ============================================================================

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
                //    数据里给的 md5 不是 sha1；size 写 0 又会被 `GetValue<long>()`
                //    读成 "Int32 转 Int64 失败"（真踩到过：整条安装直接失败）。
                //    没有就是没有，别塞个占位值进去。
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
