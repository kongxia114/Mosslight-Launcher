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
//  ## 卡在哪（2026-10 用 javap 挖出来的实据）
//
//  那个 37 MB 的 jar 里，加载器插件是
//  `net.labymod.core.loader.vanilla.launchwrapper.LabyModLoaderPlugin`，
//  它 `implements net.laby.launcher.plugin.LabyLoaderPlugin`、`init(PluginContext)` ——
//  也就是说它要的是 **LabyMod 自家启动器的插件宿主**（`net.laby.launcher.*`），
//  而不是原版 launchwrapper 的 `--tweakClass`（jar 里连 `ITweaker` 都没有引用，
//  MANIFEST 也没有 `Main-Class` / `Premain-Class`）。
//
//  所以"把版本 JSON 落盘 + 把 jar 塞 classpath"装出来的东西**跑起来等于原版**。
//  要么等 PCL2 那份 `ModDownloadLib`（它里面才有真正的注入配方），要么拿一份
//  PCL 装好的 LabyMod 实例当参照 —— 在那之前这里**只列版本、不假装能装**。
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
