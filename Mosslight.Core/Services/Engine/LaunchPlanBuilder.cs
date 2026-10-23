using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// 启动命令拼装：纯函数，不读全局配置，方便单测和逐项 diff。
// 只负责生成 java 参数列表和工作目录；起进程、收日志、解 natives 由调用方负责。

/// <summary>启动一个版本所需的全部输入（都是纯数据，方便测试）。</summary>
public sealed record LaunchPlanRequest
{
    public required string MinecraftDir { get; init; }

    public required string VersionId { get; init; }

    public string JavaPath { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// 版本目录可以显式指定 —— 目录名和 JSON 里的 id 不一定一致，扫描器知道真实路径，让它传进来比
    /// 在这里猜可靠。
    /// </summary>
    public string? VersionDir { get; init; }

    public int MinMemoryMb { get; init; } = 512;

    public int MaxMemoryMb { get; init; } = 2048;

    public int Width { get; init; } = 854;

    public int Height { get; init; } = 480;

    /// <summary>留空则按离线规则算（见 LaunchRules.OfflineUuid）。</summary>
    public string Uuid { get; init; } = string.Empty;

    public string AccessToken { get; init; } = "0";

    /// <summary>离线用 legacy。</summary>
    public string UserType { get; init; } = "legacy";

    /// <summary>
    /// 第三方 Yggdrasil 的服务器根地址（`https://littleskin.cn/api/yggdrasil`）。
    /// </summary>
    public string AuthServer { get; init; } = string.Empty;

    public string LauncherName { get; init; } = AppInfo.AppName;

    public string LauncherVersion { get; init; } = AppInfo.AppVersion;

    /// <summary>系统语言（"zh_CN" 这种）。</summary>
    public string Locale { get; init; } = string.Empty;

    /// <summary>留空则用 JSON 里的 type。</summary>
    public string VersionType { get; init; } = string.Empty;

    /// <summary>这个版本额外要加的 JVM 参数（版本设置里填的）。</summary>
    public string ExtraJvmArgs { get; init; } = string.Empty;

    /// <summary>加载器的显示名（`Fabric` / `Forge`…）。</summary>
    public string LoaderLabel { get; init; } = string.Empty;

    /// <summary>这个版本是不是"版本隔离"的（决定去哪个 `mods/` 数模组）。</summary>
    public bool? Isolated { get; init; }

    /// <summary>额外要加的游戏参数（版本设置里的"游戏参数"，追加在 JSON 自带参数后面）。</summary>
    public string ExtraGameArgs { get; init; } = string.Empty;

    /// <summary>自动进入服务器："ip" 或 "ip:port"。</summary>
    public string ServerAddress { get; init; } = string.Empty;

    /// <summary>已经合并好的版本 JSON；null = 自己去读 + 合并。</summary>
    public VersionJson? Version { get; init; }
}

/// <summary>拼好的启动计划。</summary>
public sealed class LaunchPlan
{
    /// <summary>完整的参数列表（**含第 0 个是 java.exe 本身**）。</summary>
    public List<string> Args { get; init; } = new();

    public required string WorkingDirectory { get; init; }

    public string Java { get; init; } = string.Empty;

    public string NativesDir { get; init; } = string.Empty;

    /// <summary>natives 目录不存在，启动前要先解压。</summary>
    public bool NeedsNatives { get; init; }

    public List<string> Classpath { get; init; } = new();

    /// <summary>启动过程中的告警（缺客户端 jar 之类）。</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>本地缺少的库（JSON 说了该有、但文件不在）—— 不拦启动，只提示。</summary>
    public List<string> MissingLibraries { get; init; } = new();

    public string MinecraftDir { get; init; } = string.Empty;

    public string VersionId { get; init; } = string.Empty;

    public required VersionJson VersionJson { get; init; }

    /// <summary>
    /// `${version_type}` 最终用的那个值 —— 它就是**游戏主界面那行字里斜杠后面那段** （启动器品牌
    /// 就塞在这儿）。
    /// </summary>
    public string VersionType { get; init; } = string.Empty;

    /// <summary>客户端 jar 的完整路径（安装 / 校验时要用）。</summary>
    public string ClientJar { get; init; } = string.Empty;

    /// <summary>给日志用的一行命令（**路径缩短 + classpath 折叠**）。</summary>
    public string Describe()
    {
        var parts = new List<string>(Args.Count);
        var skipNext = false;

        for (var i = 0; i < Args.Count; i++)
        {
            var arg = Args[i];
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (arg == "-cp" && i + 1 < Args.Count)
            {
                parts.Add("-cp");
                var count = Args[i + 1].Split(PlatformInfo.ClasspathSeparator).Length;
                parts.Add($"<{count} 个文件>");
                skipNext = true;
                continue;
            }

            var text = arg;
            if (MinecraftDir.Length > 0)
            {
                var prefix = MinecraftDir;
                text = text.Replace(prefix + "\\", string.Empty, StringComparison.OrdinalIgnoreCase)
                           .Replace(prefix + "/", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            if (text.Length > 200)
            {
                text = text[..200] + "…";
            }
            parts.Add(text);
        }

        return string.Join(" ", parts);
    }
}

/// <summary>把请求和版本 JSON 拼成完整的启动计划。</summary>
public static class LaunchPlanBuilder
{
    private const string QuickPlaySince = "2023-05-04";

    // `${...}` 占位符的记号（字母数字下划线）。
    private static readonly System.Text.RegularExpressions.Regex PlaceholderRe =
        new(@"\$\{[A-Za-z_][A-Za-z0-9_]*\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool SupportsQuickPlay(string? releaseTime)
    {
        var text = releaseTime ?? string.Empty;
        if (text.Length < 10)
        {
            return false;
        }
        var value = text[..10];
        // 先确认它真的像个日期：乱填的字符串按字典序会排到 "2023-…" 后面
        // （'u' > '2'），那样会给出错的选择
        if (value.Length != 10 || value[4] != '-' || value[7] != '-')
        {
            return false;
        }
        return string.CompareOrdinal(value, QuickPlaySince) >= 0;
    }

    /// <summary>库文件在本地的路径。</summary>
    private static string? ResolveLibrary(string mcDir, JsonObject lib)
    {
        if (lib["downloads"] is JsonObject downloads &&
            downloads["artifact"] is JsonObject artifact &&
            artifact["path"] is JsonValue pathNode &&
            pathNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var rel = pathNode.GetValue<string>();
            if (rel.Length > 0)
            {
                return Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        var name = lib["name"] is JsonValue nameNode &&
                   nameNode.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? nameNode.GetValue<string>()
            : null;
        var (rel2, _) = LaunchRules.MavenToPath(name);
        return rel2 is null
            ? null
            : Path.Combine(mcDir, "libraries", rel2.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>把请求和版本 JSON 拼成完整的启动计划。</summary>
    public static string AuthlibInjectorPath => Path.Combine(
        AppContext.BaseDirectory, "Libraries", "authlib-injector.jar");

    /// <summary>
    /// 注入参数（纯函数，自检直接比对）：`-javaagent:{jar}={authServer}` + 三个
    /// `-Dminecraft.api.*.host`。
    /// </summary>
    public static List<string> BuildAuthlibInjectorArgs(string authServer, string? jarPath = null)
    {
        var jar = jarPath ?? AuthlibInjectorPath;
        var server = authServer.TrimEnd('/');
        return new List<string>
        {
            $"-javaagent:{jar}={server}",
            $"-Dminecraft.api.auth.host={server}",
            $"-Dminecraft.api.session.host={server}",
            $"-Dminecraft.api.profile.host={server}",
        };
    }

    /// <summary>启动参数里的 UUID 形状：小写、无连字符。</summary>
    public static string UuidForLaunch(string uuid)
        => uuid.Replace("-", string.Empty).ToLowerInvariant();

    public static LaunchPlan Build(LaunchPlanRequest req)
    {
        var mcDir = req.MinecraftDir;
        var versionId = req.VersionId;
        var versionDir = req.VersionDir ?? VersionJsonLoader.VersionDir(mcDir, versionId);
        var vj = req.Version ?? VersionJsonLoader.Load(mcDir, versionId);

        var warnings = new List<string>();
        var missingLibs = new List<string>();

        // classpath
        var classpath = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in vj.Libraries)
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            if (!LaunchRules.RulesAllow(lib["rules"]))
            {
                continue;
            }

            var path = ResolveLibrary(mcDir, lib);
            if (path is not null && File.Exists(path))
            {
                // 老版本的 JSON 常把同一个库写两遍（一遍带 downloads、一遍只有 serverreq），
                // 而且它们**没有 inheritsFrom**，走不到合并那步的去重 —— 这里按真实路径兜一次。
                // ⚠️ 用 OrdinalIgnoreCase 的集合，等价于 Python 那边的 `.lower()`：
                // Windows 上大小写不敏感，`Foo.jar` 和 `foo.jar` 是同一个文件。
                if (seenPaths.Add(path))
                {
                    classpath.Add(path);
                }
                continue;
            }

            // 文件不在本地。要分清两种情况：
            // 1. JSON 明确写了下载地址 → 它本该在，缺了要报出来（多半是安装不完整）
            // 2. 没有下载地址 → 很多库（尤其 LWJGL 2 的 lwjgl-platform / jinput-platform）
            // **本来就只有 natives、没有主 jar**，不该塞进 classpath，也不算缺
            var hasUrl = lib["downloads"] is JsonObject d &&
                         d["artifact"] is JsonObject a &&
                         a["url"] is JsonValue urlNode &&
                         urlNode.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                         urlNode.GetValue<string>().Length > 0;
            if (hasUrl)
            {
                missingLibs.Add(lib["name"] is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? n.GetValue<string>()
                    : "?");
            }
        }

        // ⚠️ 客户端 jar 优先用实例自己的 `<实例名>.jar`，其次才看 JSON 的 jar 字段；NeoForge 1.20.5+ 用父版本 jar 会触发模块导出冲突。
        var jarId = string.IsNullOrEmpty(vj.Jar) ? versionId : vj.Jar;
        var clientJar = Path.Combine(versionDir, versionId + ".jar");
        if (!File.Exists(clientJar))
        {
            clientJar = Path.Combine(mcDir, "versions", jarId, jarId + ".jar");
        }
        if (File.Exists(clientJar))
        {
            if (seenPaths.Add(clientJar))
            {
                classpath.Add(clientJar);
            }
        }
        else
        {
            warnings.Add(Localization.Localizer.Tr("找不到客户端 jar：{path}", ("path", clientJar)));
            missingLibs.Add($"{jarId}.jar");
        }

        var sep = PlatformInfo.ClasspathSeparator;
        var classpathStr = string.Join(sep, classpath);

        // natives
        var nativesDir = Path.Combine(versionDir, $"{versionId}-natives");
        var needsNatives = NativesExtractor.NeedsExtract(vj, versionDir, versionId);

        // 占位符
        var assetsIndex = vj.AssetIndexId ?? string.Empty;

        // ⚠️ `${version_type}` = 游戏**主界面那行字**里唯一由启动器控制的部分：
        // `"Minecraft " + 版本名 + (type == "release" ? "" : "/" + type)`（1.20.4 实测）。
        // 用户填过「自定义信息」就用他的；没填就给一份品牌默认值（`Mosslight/Fabric`），
        // 这就是"启动器名字进游戏"。填 `release` 可以关掉它（游戏那边按原样不显示）。
        var versionType = (req.VersionType ?? string.Empty).Trim();
        if (versionType.Length == 0)
        {
            var modDirs = Branding.ModDirs(mcDir, versionDir, req.Isolated);
            versionType = Branding.LaunchBrand(
                req.LoaderLabel,
                Branding.CountMods(modDirs.ToArray()),
                req.LauncherName);
        }
        if (versionType.Length == 0)
        {
            versionType = vj.Type;
        }

        var replacements = new (string Key, string Value)[]
        {
            ("${natives_directory}", nativesDir),
            ("${classpath}", classpathStr),
            ("${classpath_separator}", sep),
            ("${library_directory}", Path.Combine(mcDir, "libraries")),
            ("${launcher_name}", req.LauncherName),
            ("${launcher_version}", req.LauncherVersion),
            ("${auth_player_name}", req.Username),
            // ⚠️ 官方启动器传的是**不带连字符**的 UUID；带连字符有的服务端会当另一个人
            ("${auth_uuid}", UuidForLaunch(
                req.Uuid.Length > 0 ? req.Uuid : LaunchRules.OfflineUuid(req.Username))),
            ("${auth_access_token}", req.AccessToken),
            ("${clientid}", string.Empty),
            ("${auth_xuid}", string.Empty),
            ("${user_type}", req.UserType),
            ("${version_name}", versionId),
            ("${version_type}", versionType),
            // ⚠️ 必须是版本目录，不是 .minecraft —— 版本隔离的版本靠它把存档写进自己目录
            ("${game_directory}", versionDir),
            ("${assets_root}", Path.Combine(mcDir, "assets")),
            ("${assets_index_name}", assetsIndex),
            ("${resolution_width}", req.Width.ToString()),
            ("${resolution_height}", req.Height.ToString()),
            ("${user_properties}", "{}"),
            ("${game_assets}", Path.Combine(mcDir, "assets", "virtual", "legacy")),
            ("${auth_session}", req.AccessToken.Length > 0 ? req.AccessToken : "0"),
            // 快速进入：我们用不到，给空串（正常也不会被加进来，
            // 因为对应的 feature 在 FeatureSet.Default 里是 False）
            ("${quickPlayPath}", string.Empty),
            ("${quickPlayRealms}", string.Empty),
            ("${quickPlayMultiplayer}", string.Empty),
            ("${quickPlaySingleplayer}", string.Empty),
            // 老版本（1.7/1.8）用到的
            ("${arch}", PlatformInfo.ArchBits),
            ("${primary_jar}", clientJar),
        };

        var replacementMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in replacements)
        {
            replacementMap[key] = value;
        }

        // ⚠️ 占位符必须扫描整个 `${...}` 记号查表替换，不能逐个 key 做 String.Replace：`${classpath}` 是 `${classpath_separator}` 的前缀；查不到的记号原样保留。
        string Resolve(string value)
            => PlaceholderRe.Replace(value, match =>
                replacementMap.TryGetValue(match.Value, out var replacement) ? replacement : match.Value);

        // 一条参数 → 零条或多条
        IEnumerable<string> ResolveArg(JsonNode? arg)
        {
            switch (arg)
            {
                case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                    return new[] { Resolve(value.GetValue<string>()) };

                case JsonObject obj:
                {
                    if (!LaunchRules.RulesAllow(obj["rules"]))
                    {
                        return Array.Empty<string>();
                    }
                    var raw = obj["value"];
                    var values = raw switch
                    {
                        JsonArray array => array
                            .Where(x => x is JsonValue v &&
                                        v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            .Select(x => x!.GetValue<string>())
                            .ToList(),
                        JsonValue single when single.GetValueKind() == System.Text.Json.JsonValueKind.String
                            => new List<string> { single.GetValue<string>() },
                        _ => new List<string>(),
                    };
                    return values.Select(Resolve).ToList();
                }

                default:
                    return Array.Empty<string>();
            }
        }

        // JVM 参数
        var jvmArgs = new List<string> { $"-Xms{req.MinMemoryMb}m", $"-Xmx{req.MaxMemoryMb}m" };

        // 输出编码：不设的话 Java 18+ 打出来的中文日志是乱码
        jvmArgs.Add("-Dstdout.encoding=UTF-8");
        jvmArgs.Add("-Dstderr.encoding=UTF-8");

        // 版本自己加的额外参数（版本设置对话框里填的）。
        // 位置很讲究：**必须在 -cp 之前** —— 写到 -cp <classpath> 后面，
        // 第三方账户：把认证服务器注入 JVM。authlib-injector 负责改写 authlib 的
        // 认证地址，`-Dminecraft.api.*.host` 是给认这几个属性的客户端/模组看的。
        if (!string.IsNullOrWhiteSpace(req.AuthServer))
        {
            jvmArgs.AddRange(BuildAuthlibInjectorArgs(req.AuthServer));
        }

        // JVM 会把第一个参数当成主类名，直接报"找不到主类"。
        // 放在内存参数后面则是故意的：用户真想覆盖 -Xmx 时，后写的那个生效。
        if (!string.IsNullOrWhiteSpace(req.ExtraJvmArgs))
        {
            jvmArgs.AddRange(LaunchRules.SplitArgs(req.ExtraJvmArgs));
        }

        if (vj.JvmArguments.Count > 0)
        {
            foreach (var arg in vj.JvmArguments)
            {
                jvmArgs.AddRange(ResolveArg(arg));
            }
        }
        else
        {
            jvmArgs.AddRange(new[]
            {
                $"-Djava.library.path={nativesDir}",
                $"-Djna.tmpdir={nativesDir}",
                $"-Dorg.lwjgl.system.SharedLibraryExtractPath={nativesDir}",
                $"-Dio.netty.native.workdir={nativesDir}",
                $"-Dminecraft.launcher.brand={req.LauncherName}",
                $"-Dminecraft.launcher.version={req.LauncherVersion}",
                "-cp", classpathStr,
            });
        }

        // 让游戏的默认语言跟随系统
        // 游戏第一次启动时会按 Java 的 locale 挑语言，把系统的传进去最保险。
        // （之后玩家在游戏里改过语言，那个选择记在 options.txt 里，这里不影响。）
        if (!string.IsNullOrEmpty(req.Locale))
        {
            var parts = req.Locale.Split('_', 2);
            var lang = parts[0];
            var region = parts.Length > 1 ? parts[1] : string.Empty;
            foreach (var (flag, value) in new[]
                     {
                         ("-Duser.language=", lang.ToLowerInvariant()),
                         ("-Duser.country=", region.ToUpperInvariant()),
                     })
            {
                if (value.Length > 0 && !jvmArgs.Any(a => a.StartsWith(flag, StringComparison.Ordinal)))
                {
                    jvmArgs.Add(flag + value);
                }
            }
        }

        // ⚠️ 签名校验 / 补丁差异 / Log4Shell 三个 JVM 参数不在版本 JSON 里，必须由启动器补。
        foreach (var extra in new[]
                 {
                     "-Dfml.ignoreInvalidMinecraftCertificates=true",
                     "-Dfml.ignorePatchDiscrepancies=true",
                     "-Dlog4j2.formatMsgNoLookups=true",
                 })
        {
            var key = extra.Split('=', 2)[0] + "=";
            if (!jvmArgs.Any(a => a.StartsWith(key, StringComparison.Ordinal)))
            {
                jvmArgs.Add(extra);
            }
        }

        // 游戏参数
        var gameArgs = new List<string> { vj.MainClass };

        if (vj.GameArguments.Count > 0)
        {
            foreach (var arg in vj.GameArguments)
            {
                gameArgs.AddRange(ResolveArg(arg));
            }
        }
        else if (!string.IsNullOrEmpty(vj.MinecraftArguments))
        {
            // 老格式：一整个字符串
            gameArgs.AddRange(LaunchRules.SplitArgs(Resolve(vj.MinecraftArguments)));
        }

        // 版本设置里追加的游戏参数。放**最后**：MC 的选项解析对同一个选项
        // 取后面的值，所以用户填的 --width/--height 能盖掉 JSON 里那份。
        if (!string.IsNullOrWhiteSpace(req.ExtraGameArgs))
        {
            gameArgs.AddRange(LaunchRules.SplitArgs(req.ExtraGameArgs));
        }

        // 自动进入服务器
        var server = (req.ServerAddress ?? string.Empty).Trim();
        if (server.Length > 0)
        {
            if (SupportsQuickPlay(vj.ReleaseTime))
            {
                // 1.20 起 --server/--port 没了，改走 QuickPlay
                gameArgs.Add("--quickPlayMultiplayer");
                gameArgs.Add(server);
            }
            else
            {
                var colon = server.IndexOf(':');
                var host = colon >= 0 ? server[..colon] : server;
                var port = colon >= 0 ? server[(colon + 1)..] : string.Empty;
                gameArgs.Add("--server");
                gameArgs.Add(host);
                gameArgs.Add("--port");
                gameArgs.Add(port.Length > 0 ? port : "25565");
            }
        }

        // log4j 配置（有就用，没有不影响启动）
        if (!string.IsNullOrEmpty(vj.LoggingArgument))
        {
            var fileId = vj.LoggingFileId ?? string.Empty;
            foreach (var candidate in new[]
                     {
                         Path.Combine(mcDir, "assets", "log_configs", fileId),
                         Path.Combine(mcDir, "assets", "log4j_configs", fileId),
                     })
            {
                if (File.Exists(candidate))
                {
                    jvmArgs.Add(Resolve(vj.LoggingArgument.Replace("${path}", candidate, StringComparison.Ordinal)));
                    break;
                }
            }
        }

        var args = new List<string> { req.JavaPath };
        args.AddRange(jvmArgs);
        args.AddRange(gameArgs);

        return new LaunchPlan
        {
            Args = args,
            WorkingDirectory = versionDir,
            Java = req.JavaPath,
            NativesDir = nativesDir,
            NeedsNatives = needsNatives,
            Classpath = classpath,
            MissingLibraries = missingLibs,
            Warnings = warnings,
            MinecraftDir = mcDir,
            VersionId = versionId,
            VersionJson = vj,
            VersionType = versionType,
            ClientJar = clientJar,
        };
    }
}
