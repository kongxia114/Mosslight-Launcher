using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  LabyMod 4：版本列表（能读），安装（**还没接，见文件末尾的说明**）
//
//  ## 为什么只做了列表
//
//  和别家不一样：LabyMod **没有安装器、也没有可用的 profile JSON**，
//  它给的是两样东西：
//
//    1. 一个 manifest（production / snapshot 两个频道），列出「LabyMod 版本号 +
//       这个版本支持哪些游戏版本 + 每个游戏版本对应的一份"版本 JSON"地址」；
//    2. 每个游戏版本一份**几乎就是原版的版本 JSON**（`mainClass` 是
//       `net.minecraft.launchwrapper.Launch`、67/25 条普通库、`downloads.client`、
//       `assetIndex`、参数里多一句 `--dummyMinecraftClass net.minecraft.client.main.Main`），
//       **不含任何 LabyMod 自己的库、也没有 tweakClass**。
//
//  真正的 LabyMod 客户端是另一个 37 MB 的 jar
//  （`/api/v1/download/labymod4/{channel}/{commitReference}.jar`），以及 6 个资源包
//  （`common` / `fonts` / `i18n` / `shader` / `vanilla-theme` / `fancy-theme`，
//  实测 `common` 里只有 `assets/labymod/**`，**没有 class**）。
//
//  ## 怎么装（照 PCL2-CE 的 `ModDownloadLib.cs` 抄的，不是猜的）
//
//  那份源码 2026-10 从 GitHub API 拿到（`ModDownloadLib.cs`，213 KB / 4617 行，
//  `hasLabyMod` 那一段在 4490 行附近），它的步骤见
//  <see cref="LabyModCatalog.BuildVersionJson"/> 的注释：合并这份版本 JSON →
//  剔掉跟 LabyMod 隔离库重名的 LWJGL → 追加 LabyMod 自己的 108 条库
//  （**里面有他们 fork 的 `net.minecraft:launchwrapper:4.1.3`**，`mainClass`
//  能解析全靠它）→ 追加 37 MB 客户端 jar → 写 `labymod_data`。
//
//  也就是说 LabyMod **能自动装**，不需要它的启动器；之前"卡在启动注入"的结论
//  是因为没拿到这份源码（当时找的是 `.vb` 文件名，仓库里其实是 `.cs`）。
// ============================================================================

/// <summary>LabyMod 的版本列表（manifest 解析 + 地址）。</summary>
public static class LabyModCatalog
{
    public const string ProductionManifest = "https://releases.r2.labymod.net/api/v1/manifest/production/latest.json";

    public const string SnapshotManifest = "https://releases.r2.labymod.net/api/v1/manifest/snapshot/latest.json";

    /// <summary>频道名（manifest 里的 `channel` 段和下载地址都用它）。</summary>
    public const string ProductionChannel = "production";

    public const string SnapshotChannel = "snapshot";

    /// <summary>LabyMod 客户端本体（37 MB）的地址 —— 现在还没用上，留着给"接安装"那一步。</summary>
    public static string ClientJarUrl(string channel, string commitReference)
        => $"https://releases.r2.labymod.net/api/v1/download/labymod4/{channel}/{commitReference}.jar";

    /// <summary>LabyMod 自己的库清单（**一整个频道一份**，108 条左右）。</summary>
    public static string LibrariesUrl(string channel)
        => $"https://releases.r2.labymod.net/api/v1/libraries/{channel}.json";

    /// <summary>库根：`libraries/{channel}.json` 里那些 url 都以它开头。</summary>
    public const string LibraryBase = "https://releases.r2.labymod.net/libraries/";

    /// <summary>资源包地址（`common` 那 6 个）。</summary>
    public static string AssetUrl(string channel, string commitReference, string assetName, string sha1)
        => $"https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{channel}/{commitReference}/{assetName}/{sha1}.jar";

    /// <summary>
    /// 一份 manifest → **这个游戏版本能用的** LabyMod 版本行。
    ///
    /// manifest 的形状（实测）：
    ///   `{ "labyModVersion": "47.3", "commitReference": "1bf7a24b", "sha1": …, "size": …,
    ///      "assets": {"common": "<sha1>", …},
    ///      "minecraftVersions": [{"tag": "26.3", "version": "26.3", "index": 4, "type": "release",
    ///                             "runtime": {"name": "java-runtime-epsilon", "version": 25},
    ///                             "customManifestUrl": "…/production/26.3/1bf7a24b.json"}, …] }`
    ///
    /// ⚠️ 挑版本要看 `version`（真游戏版本）**不是** `tag`（显示名）：
    ///    1.8 那一条是 `tag: "1.8"` / `version: "1.8.9"`，按 tag 比会对不上 API 给的版本号。
    /// ⚠️ `type` 也有假的（26.3 是 `release`，但有的频道里是 `snapshot`），
    ///    这里按**频道**决定"稳定 / 测试"，不按条目里的 `type`。
    /// </summary>
    public static List<LoaderVersion> ParseManifest(JsonNode? data, string channel, string mcVersion)
    {
        var rows = new List<LoaderVersion>();
        if (data is not JsonObject root)
        {
            return rows;
        }

        var labyVersion = Str(root, "labyModVersion");
        var commit = Str(root, "commitReference");
        if (labyVersion.Length == 0 || commit.Length == 0)
        {
            return rows;
        }

        var wanted = (mcVersion ?? string.Empty).Trim();
        if (wanted.Length == 0 || root["minecraftVersions"] is not JsonArray list)
        {
            return rows;
        }

        foreach (var node in list)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }
            if (!string.Equals(Str(entry, "version"), wanted, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new LoaderVersion(
                Version: labyVersion,
                Modified: ReleaseTimeText(root),
                Stable: string.Equals(channel, ProductionChannel, StringComparison.Ordinal),
                // 这两个字段眼下是"留给安装那一步"的：频道 + commit
                Type: channel,
                Patch: commit,
                FileName: Str(entry, "customManifestUrl")));
        }

        return rows;
    }

    /// <summary>这个 MC 版本在 manifest 里有没有（用来决定"无可用版本"还是"能装"）。</summary>
    public static bool SupportsMcVersion(JsonNode? data, string mcVersion)
            => ParseManifest(data, ProductionChannel, mcVersion).Count > 0;

    /// <summary>
    /// 拼出最终要落盘的那份 LabyMod 版本 JSON（**逐条照 PCL2-CE 的
    /// `ModDownloadLib.cs` 抄的**，2026-10 从 GitHub API 拿到的真源码）。
    ///
    /// PCL 的步骤（原文在 `ModDownloadLib.cs` 的 `hasLabyMod` 那一段）：
    ///
    ///   1. 把每个游戏版本那份 manifest JSON 合进来（它本身就是"原版 + 参数里多一句
    ///      `--dummyMinecraftClass`"）；
    ///   2. 拉两份全局清单：
    ///      · `api/v1/libraries/{channel}.json` —— 108 条 LabyMod 自己的库
    ///        （**里面有 `net.minecraft:launchwrapper:4.1.3`，那是他们 fork 过的
    ///        launchwrapper**，`mainClass` 能解析全靠它）；
    ///      · `api/v1/manifest/{channel}/latest.json` —— 拿 `commitReference` / `sha1` / `size`；
    ///   3. **把原版里跟 LabyMod 隔离库重名的 LWJGL 剔掉**：`isolated_libraries` 里
    ///      按 `versions` 命中当前游戏版本的那些（名字是 `lwjgl-opengl` 这种
    ///      **artifact 名**），原版清单里同名的库不能留在 classpath 上 ——
    ///      留着就是两套 LWJGL 打架。PCL 用一个正则从坐标里抠 artifact 名，
    ///      这里等价地取 `group:artifact:version[:classifier]` 的第二段；
    ///   4. 追加 108 条 LabyMod 库（url 已经是完整地址，`path` 取 url 去掉库根）；
    ///   5. 追加 `net.labymod:LabyMod:4` → 那个 37 MB 的客户端 jar；
    ///   6. 写 `labymod_data`（`channelType` / `commitReference` / `version` / `versionType`）——
    ///      LabyMod 自己靠它认"我是哪个频道、哪个 commit"；
    ///   7. `id` 换成我们的版本名（PCL 最后也 `outputJson["id"] = outputName`）。
    /// </summary>
    /// <param name="versionJson">某个游戏版本的 manifest JSON（`customManifestUrl` 那份）。</param>
    /// <param name="librariesJson">`api/v1/libraries/{channel}.json`。</param>
    /// <param name="latestJson">`api/v1/manifest/{channel}/latest.json`。</param>
    /// <param name="versionId">落盘用的版本名。</param>
    public static JsonObject BuildVersionJson(
        JsonObject versionJson, JsonObject librariesJson, JsonObject latestJson,
        string channel, string versionId)
    {
        var output = (JsonObject)versionJson.DeepClone();
        var mcVersion = Str(output, "_minecraftVersion");

        // 3) 原版里跟 LabyMod 隔离库重名的 LWJGL 要剔掉
        var isolated = IsolatedArtifactNames(librariesJson, mcVersion);
        var merged = new JsonArray();
        foreach (var node in output["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            var artifact = ArtifactName(Str(lib, "name"));
            if (artifact.Length > 0 && isolated.Contains(artifact))
            {
                continue;   // 交给 LabyMod 自己的隔离加载器去用
            }
            merged.Add(lib.DeepClone());
        }

        // 4) LabyMod 自己的 108 条库
        foreach (var node in librariesJson["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            var url = Str(lib, "url");
            if (url.Length == 0)
            {
                continue;
            }
            merged.Add(new JsonObject
            {
                ["name"] = Str(lib, "name"),
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        // ⚠️ `path` 取 url 去掉库根之后那一段（PCL 就是这么切的）：
                        //    自己的 maven 布局，跟 group 坐标拼出来的路径**不一定一致**
                        //    （`net.labymod.signed.*` 那些就是）
                        ["path"] = url.StartsWith(LibraryBase, StringComparison.Ordinal)
                            ? url[LibraryBase.Length..]
                            : url,
                        ["sha1"] = Str(lib, "sha1"),
                        ["size"] = LongValue(lib, "size"),
                        ["url"] = url,
                    },
                },
            });
        }

        // 5) 客户端本体
        var commit = Str(latestJson, "commitReference");
        merged.Add(new JsonObject
        {
            ["name"] = "net.labymod:LabyMod:4",
            ["downloads"] = new JsonObject
            {
                ["artifact"] = new JsonObject
                {
                    ["path"] = "net/labymod/LabyMod/4/LabyMod-4.jar",
                    ["sha1"] = Str(latestJson, "sha1"),
                    ["size"] = LongValue(latestJson, "size"),
                    ["url"] = ClientJarUrl(channel, commit),
                },
            },
        });

        output["libraries"] = merged;
        output["id"] = versionId;
        // 6) LabyMod 自己要读的元数据
        output["labymod_data"] = new JsonObject
        {
            ["channelType"] = channel,
            ["commitReference"] = commit,
            ["version"] = Str(latestJson, "labyModVersion"),
            ["versionType"] = "release",
        };
        output.Remove("releaseTime");
        output.Remove("time");
        return output;
    }

    /// <summary>
    /// `isolated_libraries` 里**这个游戏版本要用**的那些库名（artifact 名，如 `lwjgl-opengl`）。
    /// </summary>
    public static HashSet<string> IsolatedArtifactNames(JsonObject librariesJson, string mcVersion)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in librariesJson["isolated_libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            var versions = lib["versions"] as JsonArray;
            var applies = versions?.Any(v => string.Equals(v?.GetValue<string>(), mcVersion, StringComparison.Ordinal)) ?? false;
            if (applies)
            {
                names.Add(Str(lib, "name"));
            }
        }
        return names;
    }

    /// <summary>`org.lwjgl:lwjgl-opengl:3.4.3:natives-windows` → `lwjgl-opengl`。</summary>
    public static string ArtifactName(string coordinate)
    {
        var parts = (coordinate ?? string.Empty).Split(':');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static long LongValue(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return 0;
        }
        try
        {
            return value.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? value.GetValue<long>()
                : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>manifest 顶上的 `releaseTime`（毫秒时间戳）→ `2026-09-15 11:23`。</summary>
    private static string ReleaseTimeText(JsonObject root)
    {
        if (root["releaseTime"] is not JsonValue value)
        {
            return string.Empty;
        }

        long millis;
        try
        {
            millis = value.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => value.GetValue<long>(),
                System.Text.Json.JsonValueKind.String => long.TryParse(value.GetValue<string>(), out var parsed)
                    ? parsed
                    : 0,
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return string.Empty;
        }

        if (millis <= 0)
        {
            return string.Empty;
        }
        // 有的接口给秒、有的给毫秒：小于 1e11 的按秒算（2001 年以前的毫秒不可能）
        var stamp = millis < 100_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds(millis)
            : DateTimeOffset.FromUnixTimeMilliseconds(millis);
        return stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static string Str(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : string.Empty;
}
