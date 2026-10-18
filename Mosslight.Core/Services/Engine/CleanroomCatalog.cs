using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mosslight.Core.Services.Engine;

// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-cleanroomcatalog-cs

/// <summary>Cleanroom 的版本列表 + 版本 JSON 提取。</summary>
public static class CleanroomCatalog
{
    private const string Maven = "https://maven.cleanroommc.com";
    private const string GroupPath = "com/cleanroommc/cleanroom";

    /// <summary>Cleanroom 只支持这一个游戏版本（`install_profile.json` 里的 `minecraft` 字段）。</summary>
    public const string OnlyMinecraftVersion = "1.12.2";

    /// <summary>它要求的 Java 大版本（0.5.0 起是 25；更早的是 21）。</summary>
    public const int RequiredJavaMajor = 25;

    /// <summary>0.5.0 之前的 Cleanroom 要 Java 21（PCL2 CE 的启动代码里也是这个分档）。</summary>
    public const int LegacyRequiredJavaMajor = 21;

    public static string MetadataUrl => $"{Maven}/{GroupPath}/maven-metadata.xml";

    public static string InstallerUrl(string version)
        => $"{Maven}/{GroupPath}/{version}/cleanroom-{version}-installer.jar";

    public static string UniversalUrl(string version)
        => $"{Maven}/{GroupPath}/{version}/cleanroom-{version}-universal.jar";

    /// <summary>这一版要哪个 Java（0.5.0 是分界线）。</summary>
    public static int RequiredJavaMajorFor(string version)
        => LoaderCatalog.CompareVersionKeys(
               LoaderCatalog.VersionKey(version), LoaderCatalog.VersionKey("0.5.0")) >= 0
            ? RequiredJavaMajor
            : LegacyRequiredJavaMajor;

    /// <summary>给界面/日志用的一句提醒。</summary>
    public static string JavaNoteFor(string version)
        => $"Cleanroom {version} 要 Java {RequiredJavaMajorFor(version)} 才跑得起来";

    /// <summary>这个游戏版本能不能装 Cleanroom（只有 1.12.2 行）。</summary>
    public static bool Supports(string? mcVersion)
        => string.Equals((mcVersion ?? string.Empty).Trim(), OnlyMinecraftVersion, StringComparison.Ordinal);

    /// <summary>
    /// `maven-metadata.xml` → 版本行（新的在前）。
    ///
    /// ⚠️ 只认 `<version>` 那一段；`<latest>` / `<release>` 也在同一份文件里，
    ///    按"最后一个 &lt;version&gt;"猜最新是不可靠的（顺序不保证），所以这里
    ///    全部收下来再自己按数值段排。
    /// </summary>
    public static List<LoaderVersion> ParseVersions(string xml)
    {
        var rows = new List<LoaderVersion>();
        foreach (Match m in Regex.Matches(xml ?? string.Empty, "<version>([^<]+)</version>"))
        {
            var version = m.Groups[1].Value.Trim();
            if (version.Length == 0)
            {
                continue;
            }
            // `0.7.8-alpha` 这种带 alpha / beta / rc 的是**预发布**，界面上要标出来
            var stable = !Regex.IsMatch(version, "-(alpha|beta|rc|pre|snapshot)",
                RegexOptions.IgnoreCase);
            rows.Add(new LoaderVersion(Version: version, Stable: stable));
        }

        rows.Sort((x, y) => LoaderCatalog.CompareVersionKeys(
            LoaderCatalog.VersionKey(y.Version), LoaderCatalog.VersionKey(x.Version)));
        return rows;
    }

    /// <summary>
    /// 把安装器里那份 `version.json` 抽出来并修好。
    ///
    /// <paramref name="installerJar"/> 是**已经下到本地**的安装器路径 ——
    /// 这层不做网络（自检可以直接喂一个造出来的 zip）。
    /// </summary>
    public static JsonObject ReadVersionJson(string installerJar)
    {
        using var zip = ZipFile.OpenRead(installerJar);
        var entry = zip.GetEntry("version.json")
                    ?? throw new LoaderSetupException("Cleanroom 安装器里没有 version.json（是不是下载到了半截文件？）");
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();

        if (JsonNode.Parse(text) is not JsonObject vj)
        {
            throw new LoaderSetupException("Cleanroom 的 version.json 解析不出来");
        }
        return vj;
    }

    /// <summary>
    /// 补上空的库地址（坑 1）+ 把 `id` 换成我们要落盘的名字。
    ///
    /// ⚠️ `id` 必须改：目录名 / 文件名 / `--version` 三者得一致，
    ///    否则启动时按目录找 jar、按 id 传 `--version`，两边对不上。
    /// </summary>
    /// <returns>补了几条（自检拿它断言"真的修过东西"）。</returns>
    public static int PrepareVersionJson(JsonObject vj, string loaderVersion, string versionId)
    {
        vj["id"] = versionId;

        var fixedCount = 0;
        foreach (var node in vj["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }

            // 库自己那个 url（maven 简写形式）空着 → 按 maven 根补
            if (lib["url"] is JsonValue urlNode
                && urlNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                && urlNode.GetValue<string>().Length == 0)
            {
                lib["url"] = Maven + "/";
                fixedCount++;
            }

            // downloads.artifact.url 空着 → 按 maven 路径补
            if (lib["downloads"] is JsonObject downloads
                && downloads["artifact"] is JsonObject artifact
                && artifact["url"] is JsonValue artifactUrl
                && artifactUrl.GetValueKind() == System.Text.Json.JsonValueKind.String
                && artifactUrl.GetValue<string>().Length == 0
                && artifact["path"] is JsonValue pathNode
                && pathNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                artifact["url"] = $"{Maven}/{pathNode.GetValue<string>()}";
                fixedCount++;
            }
        }

        // 万一上面哪条没命中（格式变了），这条兜底：Cleanroom 自己的 universal jar 一定在 maven 上
        var universal = new JsonObject
        {
            ["url"] = UniversalUrl(loaderVersion),
        };
        foreach (var node in vj["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib
                || lib["name"] is not JsonValue nameNode
                || nameNode.GetValue<string>() is not { } name
                || !name.StartsWith("com.cleanroommc:cleanroom:", StringComparison.Ordinal))
            {
                continue;
            }
            if (lib["downloads"] is not JsonObject downloads)
            {
                downloads = new JsonObject();
                lib["downloads"] = downloads;
            }
            if (downloads["artifact"] is not JsonObject artifact)
            {
                artifact = (JsonObject)universal.DeepClone();
                downloads["artifact"] = artifact;
                fixedCount++;
            }
            else if (artifact["url"] is null
                     || (artifact["url"] is JsonValue u
                         && u.GetValueKind() == System.Text.Json.JsonValueKind.String
                         && u.GetValue<string>().Length == 0))
            {
                artifact["url"] = UniversalUrl(loaderVersion);
                fixedCount++;
            }
            // 连 path 都没有的话补上（maven 默认布局）
            artifact["path"] ??= $"{GroupPath}/{loaderVersion}/cleanroom-{loaderVersion}-universal.jar";
        }

        return fixedCount;
    }
}
