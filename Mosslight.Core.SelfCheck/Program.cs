using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Animation;
using Mosslight.Core.Appearance;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;
using Mosslight.Core.Theme;

namespace Mosslight.Core.SelfCheck;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>自检自己用的 HttpClient（`--net` 那几节里发 HEAD 用）；⚠️ 跟引擎里那些**刻意不共用**：引擎那边的超时/UA 是给真实下载调的，自检要的是"快速失败"，混在一起改一边会影响另一边。</summary>
    private static readonly HttpClient Http = CreateSelfCheckClient();

    private static HttpClient CreateSelfCheckClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadManager.UserAgent);
        return client;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var goldenPath = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "tests", "theme_golden.json"));

        Section("主题色金标准比对（C# vs Python 的 core/theme.py）");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine($"  ⚠️ 找不到金标准文件：{goldenPath}");
            Console.WriteLine("     先生成：python D:\\deepseek\\.dhml-tests\\gen_theme_golden.py");
            return 2;
        }
        CheckPalettes(goldenPath);

        Section("调色板完整性");
        CheckPaletteShape();

        Section("颜色函数的边界");
        CheckColorFunctionEdges(goldenPath);

        Section("配置的类型纠正（手改过的配置文件）");
        CheckSettingsCoercion();

        Section("多语言");
        CheckLocalization();
        CheckUpdates();
        CheckLittleSkinAuth();

        Section("入场动画预设");
        CheckAnimationPresets();

        Section("缓动曲线金标准（C# vs Python 的 ui/widgets/pcl_ease.py）");
        CheckEasingCurves();

        Section("入场动画轨迹引擎（C# vs Python 的 ui/widgets/slide_in.py）");
        CheckSlideInTrajectory();

        Section("日志窗口的后端（级别判定 / 续行 / 噪音 / log4j XML 还原）");
        CheckLogging();

        Section("服务桩（原型数据）");
        CheckStubs();

        Section("启动引擎金标准（C# vs Python 的 core/launch.py + versions.py）");
        CheckLaunchEngine(goldenPath);

        Section("加载器目录金标准（C# vs Python 的 core/loaders.py）");
        CheckLoaderCatalog(goldenPath);

        Section("加载器：后补的几家（Legacy Fabric / Cleanroom / LiteLoader）");
        CheckLoaderCatalogExtras();

        // 安装页的"谁能跟谁一起装"（PCL 的 Load*GetError 那张表）
        Section("安装页的兼容性规则（与 PCL 对齐）");
        CheckLoaderCompat();

        Section("加载器实例自己的 jar（NeoForge 1.20.5+ 的模块冲突）");
        CheckEnsureInstanceJar();

        Section("下载源策略（自动 / 只用官方 / 只用 BMCLAPI / 双流）");
        CheckDownloadSourcePolicy();

        Section("游戏语言（首次启动写进 options.txt）");
        CheckGameLocales();

        Section("版本要哪个 Java（带 inheritsFrom 的那种）");
        CheckRequiredJavaForInheriting();

        Section("装完找目录（安装器自己起的名字）");
        CheckInstalledVersionLookup();

        Section("安装器两份清单（install_profile.json + version.json）");
        CheckInstallerLibraryTasks();

        Section("安装器日志（Logs\\installer-*.log）");
        CheckInstallerLog();

        Section("安装包：只下载安装器（不安装）");
        CheckInstallerDownloadOnly();

        Section("版本设置 + 安装器金标准（C# vs Python 的 version_settings.py / loader_setup.py）");
        CheckVersionSettingsAndInstaller(goldenPath);

        Section("补全缺失的库 + mod 目录反查（C# vs Python 的 repair.py / mods.py）");
        CheckRepairAndModsDir(goldenPath);

        Section("整合包（C# vs Python 的 modpack.py）");
        CheckModpack(goldenPath);

        // 上面那节验的是**解析**（纯函数）。这一节把**服务层**真跑一遍：
        // 建实例目录、展开覆盖层、写版本 JSON —— 全离线（用一个没有 files、
        // 也没有加载器的包，所以一步网络都不用走）。
        Section("整合包服务（离线端到端）");
        CheckModpackService();

        Section("整合包实例能不能启动（实例 → 加载器 → 原版 的 inheritsFrom 链）");
        CheckModpackInstanceLaunch();

        Section("本地整合包压缩包的格式识别（拖入安装）");
        CheckPackArchive();

        Section("详情页的版本分组（游戏版本 × 加载器 × 正式/预览）");
        CheckModVersionGroups();

        Section("下载进度广播（下载窗口靠这个取数）");
        CheckDownloadMonitor();

        Section("滑到底续取的判断（详情页分组 / 搜索结果共用）");
        CheckScrollLoader();

        Section("收藏夹（`favorites.json` 的读写）");
        CheckFavorites();

        Section("资源字典 key（代码里 FindResource 的 key 必须真的存在）");
        CheckResourceKeys();

        Section("玩家头像（皮肤抠图 / 挑默认皮肤）");
        CheckPlayerAvatars();

        Section("参数拆分与内存建议");
        CheckArgSplittingAndMemory();

        // ⚠️ 这一节要起一个真的 java 进程（约 1 秒）。前面那些全是纯计算 /
        // 读写临时文件，"拼出来的命令行对不对"验过了，但
        // **"到底有没有真的把进程拉起来、日志收不收得上来"** 没验过 ——
        // 而那正是 LaunchResult.Ok 对用户唯一的含义。
        Section("真启动一次（进程起来 + 日志收得上 + 退出事件）");
        CheckRealProcessLaunch();

        // 联网冒烟**默认不跑**：自检是 CI / 离线的常客，不该因为没网就红。
        // 传 `--net` 才跑（见 Readme 的"怎么验证下载引擎"）。
        if (args.Contains("--net"))
        {
            Section("联网冒烟（--net）：对着真实的 Mojang 清单验一遍");
            CheckNetworkSmoke().GetAwaiter().GetResult();
        }

        // ⚠️ **真跑一次 Forge 安装器**单独一个开关：它会下 ~9 MB 安装器、
        // 再把安装器自己的库下下来，比 `--net` 那一堆只发 HEAD 的重得多。
        // 单独开关是为了让 `--net` 保持"几十秒能跑完"。
        if (args.Contains("--net-forge"))
        {
            Section("真跑一次 Forge 安装器（--net-forge）");
            CheckRealForgeInstallAsync().GetAwaiter().GetResult();
        }

        // ⚠️ 后补的那两家（LiteLoader / Cleanroom）**真装一遍**也要单独开关：
        // LiteLoader 只下 3 个库（~1 MB），但 Cleanroom 那个安装器是 15 MB。
        // 单独开关，`--net` 才能保持"几十秒跑完"。
        if (args.Contains("--net-loaders"))
        {
            Section("真装一次 LiteLoader / 真取一次 Cleanroom（--net-loaders）");
            CheckRealJsonBuildLoadersAsync().GetAwaiter().GetResult();
        }

        // LabyMod：真拉一遍清单、拼出 JSON、规划一遍（不下载那几个 GB）
        if (args.Contains("--net-labymod"))
        {
            Section("LabyMod：真取清单并拼出可启动的版本 JSON（--net-labymod）");
            CheckRealLabyModPlanAsync().GetAwaiter().GetResult();
        }

        Console.WriteLine();
        Console.WriteLine($"  {_passed} 项通过，{_failed} 项不通过");
        if (_failed > 0)
        {
            Console.WriteLine();
            foreach (var line in Failures)
            {
                Console.WriteLine("  ✗ " + line);
            }
            return 1;
        }
        Console.WriteLine("  全部通过");
        return 0;
    }

    // 小工具

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  ✓ {name}");
        }
        else
        {
            _failed++;
            var line = detail is null ? name : $"{name} —— {detail}";
            Failures.Add(line);
            Console.WriteLine($"  ✗ {line}");
        }
    }

    private static void Equal(string name, string expected, string actual)
        => Check(name, expected == actual, $"期望 {expected}，实际 {actual}");

    // 主题色

    private sealed record GoldenCase(string Mode, string Accent, Dictionary<string, string> Palette);

    private static void CheckPalettes(string goldenPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var root = doc.RootElement;

        var cases = new List<GoldenCase>();
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var palette = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in item.GetProperty("palette").EnumerateObject())
            {
                palette[prop.Name] = prop.Value.GetString()!;
            }
            cases.Add(new GoldenCase(
                item.GetProperty("mode").GetString()!,
                item.GetProperty("accent").GetString()!,
                palette));
        }

        var mismatch = 0;
        var checkedTokens = 0;
        foreach (var testCase in cases)
        {
            var mode = testCase.Mode == "light" ? PaletteMode.Light : PaletteMode.Dark;
            var actual = ThemeService.Palette(mode, testCase.Accent);
            var label = $"{testCase.Mode}/{ (testCase.Accent.Length == 0 ? "自带强调色" : testCase.Accent) }";

            foreach (var (token, expected) in testCase.Palette)
            {
                checkedTokens++;
                if (!actual.TryGetValue(token, out var got))
                {
                    mismatch++;
                    if (mismatch <= 10) Check($"{label} · {token}", false, "C# 里没有这个 token");
                    continue;
                }
                if (got != expected)
                {
                    mismatch++;
                    if (mismatch <= 10)
                    {
                        Check($"{label} · {token}", false, $"期望 {expected}，实际 {got}");
                    }
                }
            }
        }
        Check($"{cases.Count} 个用例 / {checkedTokens} 个 token 全部一致",
            mismatch == 0, mismatch == 0 ? null : $"{mismatch} 个不一致（上面列了前 10 个）");
    }

    private static void CheckPaletteShape()
    {
        var dark = ThemeService.Palette(PaletteMode.Dark);
        var light = ThemeService.Palette(PaletteMode.Light);
        Check("DARK 有 48 个 token", dark.Count == 48, $"实际 {dark.Count}");
        Check("LIGHT 有 48 个 token", light.Count == 48, $"实际 {light.Count}");
        Check("两套主题的键完全一样",
            dark.Keys.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(light.Keys.OrderBy(x => x, StringComparer.Ordinal)));
        Check("强调色一族是 14 个键", ThemeService.AccentTokens.Count == 14,
            $"实际 {ThemeService.AccentTokens.Count}");
        Check("每个强调色键都在调色板里",
            ThemeService.AccentTokens.All(dark.ContainsKey));

        // 自定义强调色只应该动那 14 个键，别的一个都不能碰
        var custom = ThemeService.Palette(PaletteMode.Dark, "#3b82f6");
        var touched = custom.Where(kv => dark[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        Check("自定义强调色只覆盖强调色一族",
            touched.All(ThemeService.IsAccentToken),
            "被误改的：" + string.Join(", ", touched.Where(k => !ThemeService.IsAccentToken(k))));

        // 非法值必须被忽略（喂进去会算炸，界面就没样式了）
        foreach (var bad in new[] { "red", "#12345", "#gggggg", "3b82f6", "" })
        {
            var palette = ThemeService.Palette(PaletteMode.Dark, bad);
            Check($"非法强调色 {bad switch { "" => "(空)", _ => bad }} 被忽略",
                palette["accent"] == dark["accent"], $"实际 {palette["accent"]}");
        }
    }

    private static void CheckColorFunctionEdges(string goldenPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var extra = doc.RootElement.GetProperty("extra");

        foreach (var prop in extra.GetProperty("readable_on").EnumerateObject())
        {
            Equal($"readable_on({prop.Name})", prop.Value.GetString()!, ColorMath.ReadableOn(prop.Name));
        }

        foreach (var prop in extra.GetProperty("blend").EnumerateObject())
        {
            var parts = prop.Name.Split('|');
            Equal($"blend({prop.Name})", prop.Value.GetString()!,
                ColorMath.Blend(parts[0], parts[1], double.Parse(parts[2])));
        }

        foreach (var prop in extra.GetProperty("shift").EnumerateObject())
        {
            var parts = prop.Name.Split('|');
            Equal($"shift({prop.Name})", prop.Value.GetString()!,
                ColorMath.Shift(parts[0], double.Parse(parts[1])));
        }

        Check("IsHex 认 #rrggbb", ColorMath.IsHex("#5EC269"));
        Check("IsHex 拒 #rrggbbaa", !ColorMath.IsHex("#5ec26980"));
        Check("IsHex 拒 null", !ColorMath.IsHex(null));

        var (r, g, b) = ColorMath.ToRgb("#ffffff");
        Check("ToRgb(#ffffff) = 1,1,1", Math.Abs(r - 1) < 1e-9 && Math.Abs(g - 1) < 1e-9 && Math.Abs(b - 1) < 1e-9);
        Equal("ToHex(1,1,1)", "#ffffff", ColorMath.ToHex(1, 1, 1));
        Equal("ToHex 超范围会钳住", "#ffffff", ColorMath.ToHex(2, 2, 2));
        Equal("ToHex 负值会钳住", "#000000", ColorMath.ToHex(-1, -1, -1));
    }

    // 配置

    private static void CheckSettingsCoercion()
    {
        // Python 的 as_bool 认这些写法；照抄的时候最怕只认 "true"/"false"
        foreach (var (text, expected) in new (string, bool)[]
                 {
                     ("true", true), ("yes", true), ("on", true), ("1", true),
                     ("false", false), ("no", false), ("off", false), ("0", false),
                     ("TRUE", true), (" False ", false),
                 })
        {
            var node = System.Text.Json.Nodes.JsonValue.Create(text);
            var got = AppSettings.AsBool(node, fallback: !expected);
            Check($"AsBool(\"{text}\") = {expected}", got == expected, $"实际 {got}");
        }

        // 认不出来的字符串要退回默认，而不是当成 true
        Check("AsBool(\"随便\") 退回默认",
            AppSettings.AsBool(System.Text.Json.Nodes.JsonValue.Create("随便"), false) == false);

        Equal("AsInt(\"4096\")", "4096",
            AppSettings.AsInt(System.Text.Json.Nodes.JsonValue.Create("4096"), 0).ToString());
        Check("AsInt(\"abc\") 退回默认",
            AppSettings.AsInt(System.Text.Json.Nodes.JsonValue.Create("abc"), 7) == 7);

        Equal("AsColor 小写化", "#3b82f6",
            AppSettings.AsColor(System.Text.Json.Nodes.JsonValue.Create("#3B82F6"), ""));
        Equal("AsColor 非法退回默认", "",
            AppSettings.AsColor(System.Text.Json.Nodes.JsonValue.Create("red"), ""));

        // 范围纠正：文件里手写成 999 的透明度不该把界面画没
        var isolatedDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck-defaults");
        var settings = LoadIsolatedSettings(isolatedDir);
        // ⚠️ 这一条是"别碰用户真配置"的看门狗（见 LoadIsolatedSettings 的注释）
        Equal("自检用的 settings 落在临时目录的 Mosslight 下",
            Path.Combine(isolatedDir, AppSettings.DataDirName, "config.json"),
            settings.FilePath);
        Equal("配置目录 = 启动器目录下的 Mosslight",
            Path.Combine(@"D:\launcher", AppSettings.DataDirName),
            AppSettings.ResolveConfigDir(@"D:\launcher"));
        Equal("程序目录能写 → 用程序目录下的 Mosslight",
            Path.Combine(@"D:\launcher", AppSettings.DataDirName),
            AppSettings.PickConfigDir(@"D:\launcher", _ => true, @"C:\Users\u\AppData\Roaming\Mosslight"));
        Equal("程序目录写不了 → 退到 AppData 的 Mosslight",
            @"C:\Users\u\AppData\Roaming\Mosslight",
            AppSettings.PickConfigDir(@"C:\Program Files\Mosslight",
                _ => false, @"C:\Users\u\AppData\Roaming\Mosslight"));
        Check("临时目录判定为可写（IsWritable）",
            AppSettings.IsWritable(Path.Combine(Path.GetTempPath(), "mosslight-writable-probe")));
        Check("不存在的只读路径判定为不可写",
            !AppSettings.IsWritable(@"Z:\mosslight-definitely-missing\Mosslight"));
        settings.Set("card_opacity", 999);
        Check("card_opacity 被钳到 100", settings.CardOpacity == 100, $"实际 {settings.CardOpacity}");
        settings.Set("bg_dim", -50);
        Check("bg_dim 被钳到 0", settings.BackgroundDim == 0, $"实际 {settings.BackgroundDim}");
        settings.Set("download_threads", 999);
        Check("download_threads 被钳到 32", settings.DownloadThreads == 32, $"实际 {settings.DownloadThreads}");
        settings.Set("bg_mode", "乱写的");
        Check("bg_mode 认不出来回 center", settings.BackgroundMode == BackgroundModes.Center,
            $"实际 {settings.BackgroundMode}");
        settings.Set("theme", "dark");
        Check("theme 认 dark", settings.Theme == ThemeMode.Dark);
        settings.Set("theme", "乱写的");
        Check("theme 认不出来回 system", settings.Theme == ThemeMode.System);

        settings.Set("multi_thread", false);
        settings.Set("download_threads", 3);
        Check("multi_thread 关 → 用默认 8 线程（不是 1）", settings.EffectiveThreads == 8,
            $"实际 {settings.EffectiveThreads}");
        settings.Set("multi_thread", true);
        Check("multi_thread 开 → 用自己设的 3", settings.EffectiveThreads == 3,
            $"实际 {settings.EffectiveThreads}");
    }

    /// <summary>自检里要一份 AppSettings 时走这里 —— 它保证是**隔离的**；⚠️ 自检里凡是要造 settings 的地方都从这儿拿，别直接 `AppSettings.Load(App.BaseDir)`。</summary>
    private static AppSettings LoadIsolatedSettings(string dir)
    {
        Directory.CreateDirectory(dir);
        return AppSettings.Load(dir);
    }

    // 多语言

    /// <summary>工程支持的五种语言，顺序就是下拉框里的顺序。</summary>
    private static readonly string[] ExpectedLanguages =
        { "zh_CN", "zh_TW", "en_US", "ja_JP", "ru_RU" };

    // LittleSkin OAuth

    /// <summary>纯离线的解析断言：网络请求那几条没法在自检里真跑。</summary>
    private static void CheckLittleSkinAuth()
    {
        Section("LittleSkin OAuth 2.0：授权地址 / token / 角色 / 错误");

        var options = new Mosslight.Core.Services.Auth.LittleSkinOptions
        {
            ClientId = "client-abc",
            ClientSecret = "secret",
            RedirectUri = "http://localhost:25566/callback",
            LogDir = Path.Combine(Path.GetTempPath(), "mosslight-auth-probe"),
        };
        var service = new Mosslight.Core.Services.Auth.LittleSkinAuthService(options);
        var url = service.GetAuthorizeUrl("xyz");

        Check("授权地址：域名对", url.StartsWith("https://littleskin.cn/oauth/authorize?", StringComparison.Ordinal));
        Check("授权地址：client_id", url.Contains("client_id=client-abc"));
        Check("授权地址：response_type=code", url.Contains("response_type=code"));
        Check("授权地址：默认 scope 是 User.Read（LittleSkin 对 Yggdrasil.Minecraft.API 回 invalid_scope）",
            url.Contains("scope=User.Read"));
        Check("授权地址：scope 里没有多余空格或未编码字符",
            !url.Contains("scope=User.Read ") && !url.Contains("scope=User.Read%20"));
        Check("授权地址：redirect_uri 编码过",
            url.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A25566%2Fcallback"));
        Check("授权地址：state 原样带上", url.Contains("state=xyz"));

        var tokenJson = """
        {"access_token":"at-123","token_type":"Bearer","expires_in":3600,
         "refresh_token":"rt-456","scope":"Yggdrasil.Minecraft.API"}
        """;
        var parsed = Mosslight.Core.Services.Auth.LittleSkinAuthService.ParseToken(tokenJson);
        Check("token：解析成功", parsed.Ok);
        Equal("token：access_token", "at-123", parsed.AccessToken);
        Equal("token：refresh_token", "rt-456", parsed.RefreshToken);
        Check("token：有效期算得出来（约 1 小时后）",
            parsed.ExpiresAt > DateTimeOffset.Now.AddMinutes(50));

        var badToken = Mosslight.Core.Services.Auth.LittleSkinAuthService.ParseToken(
            "{\"error\":\"invalid_grant\",\"message\":\"授权码已过期\"}");
        Check("token：缺 access_token → 失败并带原因",
            !badToken.Ok && badToken.Error == "授权码已过期");

        var profiles = Mosslight.Core.Services.Auth.LittleSkinAuthService.ParseProfiles(
            "[{\"id\":\"a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6\",\"name\":\"Steve\"}]");
        Check("角色：数组解析出 1 个", profiles.Count == 1, $"实际 {profiles.Count}");
        Equal("角色：名字", "Steve", profiles[0].Name);
        Equal("角色：uuid 带连字符",
            "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", profiles[0].Uuid);

        var single = Mosslight.Core.Services.Auth.LittleSkinAuthService.ParseProfiles(
            "{\"id\":\"00112233445566778899aabbccddeeff\",\"name\":\"Alex\"}");
        Check("角色：单个对象也认", single.Count == 1, $"实际 {single.Count}");

        var broken = Mosslight.Core.Services.Auth.LittleSkinAuthService.ParseProfiles("{ 不是 json");
        Check("角色：坏 JSON → 空列表（不抛）", broken.Count == 0);

        var accountDir = Path.Combine(Path.GetTempPath(), "mosslight-accounts-probe");
        if (Directory.Exists(accountDir)) { Directory.Delete(accountDir, recursive: true); }
        var store = new Mosslight.Core.Services.RealAccountService(accountDir);
        store.AddThirdParty("Steve", "00112233-4455-6677-8899-aabbccddeeff",
            "at-1", "rt-1", "littleskin");
        var reopened = new Mosslight.Core.Services.RealAccountService(accountDir);
        var saved = reopened.All.FirstOrDefault();
        Check("第三方账户：写进 accounts.json 再读回来",
            saved is not null && saved.Kind == Mosslight.Core.Services.AccountKind.ThirdParty,
            saved?.Name ?? "(none)");
        Equal("第三方账户：uuid 保留", "00112233-4455-6677-8899-aabbccddeeff", saved?.Uuid ?? "");
        Equal("第三方账户：access_token 保留", "at-1", saved?.AccessToken ?? "");
        Equal("第三方账户：auth_server 保留", "littleskin", saved?.AuthServer ?? "");
        var again = reopened.AddThirdParty("Steve", "00112233-4455-6677-8899-aabbccddeeff",
            "at-2", "rt-2", "littleskin");
        Check("第三方账户：重登不新增行，只刷 token",
            reopened.All.Count == 1 && again.AccessToken == "at-2", $"行数 {reopened.All.Count}");
        Directory.Delete(accountDir, recursive: true);

        // 通用凭据接口
        Check("凭据：环境变量名拼得对",
            Mosslight.Core.Configuration.BuildSecrets.EnvName("littleskin.client_id")
                == "MOSSLIGHT_LITTLESKIN_CLIENT_ID");
        Check("凭据：没配的键返回空串（不抛）",
            Mosslight.Core.Configuration.BuildSecrets.Get("no.such.key") == string.Empty
            && !Mosslight.Core.Configuration.BuildSecrets.Has("no.such.key"));

        Environment.SetEnvironmentVariable("MOSSLIGHT_SELFCHECK_PROBE", "hello");
        Equal("凭据：环境变量能兜底", "hello",
            Mosslight.Core.Configuration.BuildSecrets.Get("selfcheck.probe"));
        Check("凭据：Has 认环境变量", Mosslight.Core.Configuration.BuildSecrets.Has("selfcheck.probe"));
        Environment.SetEnvironmentVariable("MOSSLIGHT_SELFCHECK_PROBE", null);
        Check("凭据：环境变量清掉就没了",
            !Mosslight.Core.Configuration.BuildSecrets.Has("selfcheck.probe"));

        Check("凭据：LittleSkinSecrets 与 BuildSecrets 一致",
            Mosslight.Core.Services.Auth.LittleSkinSecrets.IsConfigured
            == (Mosslight.Core.Configuration.BuildSecrets.Has("littleskin.client_id")
                && Mosslight.Core.Configuration.BuildSecrets.Has("littleskin.client_secret")));

        Check("凭据：默认回调是 127.0.0.1:25566",
            Mosslight.Core.Services.Auth.LittleSkinSecrets.RedirectUri == "http://127.0.0.1:25566/callback");
        var secretOptions = Mosslight.Core.Services.Auth.LittleSkinSecrets.CreateOptions();
        Check("凭据：CreateOptions 带回调和日志目录",
            secretOptions.RedirectUri == Mosslight.Core.Services.Auth.LittleSkinSecrets.RedirectUri
            && secretOptions.LogDir.Length > 0);
        Check("凭据：IsConfigured 与两个值的空/非空一致",
            Mosslight.Core.Services.Auth.LittleSkinSecrets.IsConfigured
            == (Mosslight.Core.Services.Auth.LittleSkinSecrets.ClientId.Length > 0
                && Mosslight.Core.Services.Auth.LittleSkinSecrets.ClientSecret.Length > 0));

        // authlib 自动下载 / 皮肤头像
        Check("authlib：下载路径是动态的（跟着程序目录）",
            Mosslight.Core.Services.Auth.AuthlibInjectorInstaller.TargetPath
                .EndsWith(Path.Combine("Libraries", "authlib-injector.jar"), StringComparison.OrdinalIgnoreCase)
            && Mosslight.Core.Services.Auth.AuthlibInjectorInstaller.TargetPath
                .StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
        Check("authlib：下载地址是 release 资产",
            Mosslight.Core.Services.Auth.AuthlibInjectorInstaller.DownloadUrl
                .Contains("/releases/download/", StringComparison.Ordinal));

        const string textureB64 = "eyJ0ZXh0dXJlcyI6eyJTS0lOIjp7InVybCI6Imh0dHA6Ly90ZXh0dXJlcy5taW5lY3JhZnQubmV0L3RleHR1cmUvYWJjMTIzIn19fQ==";
        var profileJson = "{\"id\":\"x\",\"name\":\"Steve\",\"properties\":[{\"name\":\"textures\",\"value\":\"" + textureB64 + "\"}]}";
        Equal("皮肤：从 textures 里挖出地址",
            "http://textures.minecraft.net/texture/abc123",
            Mosslight.Core.Services.Auth.SkinAvatarService.ParseSkinUrl(profileJson) ?? "");
        Check("皮肤：坏 JSON → null",
            Mosslight.Core.Services.Auth.SkinAvatarService.ParseSkinUrl("{不是 json") is null);
        Check("皮肤：落在 skins/<名字>.png",
            Mosslight.Core.Services.Auth.SkinAvatarService.SkinPathFor(@"C:\cfg", "Steve")
                .EndsWith(Path.Combine("skins", "Steve.png"), StringComparison.OrdinalIgnoreCase));

        // 启动参数里的第三方注入
        var injectArgs = Mosslight.Core.Services.Engine.LaunchPlanBuilder.BuildAuthlibInjectorArgs(
            "https://littleskin.cn/api/yggdrasil", @"D:\app\Libraries\authlib-injector.jar");
        Equal("authlib：4 条注入参数", "4", injectArgs.Count.ToString());
        Equal("authlib：javaagent 带服务器",
            @"-javaagent:D:\app\Libraries\authlib-injector.jar=https://littleskin.cn/api/yggdrasil",
            injectArgs[0]);
        Check("authlib：三个 -Dminecraft.api.*.host",
            injectArgs.Contains("-Dminecraft.api.auth.host=https://littleskin.cn/api/yggdrasil")
            && injectArgs.Contains("-Dminecraft.api.session.host=https://littleskin.cn/api/yggdrasil")
            && injectArgs.Contains("-Dminecraft.api.profile.host=https://littleskin.cn/api/yggdrasil"));
        Check("authlib：jar 路径是动态的（跟着程序目录）",
            Mosslight.Core.Services.Engine.LaunchPlanBuilder.AuthlibInjectorPath
                .EndsWith(Path.Combine("Libraries", "authlib-injector.jar"), StringComparison.OrdinalIgnoreCase));

        Equal("启动 uuid：去掉连字符并小写",
            "0123456789abcdef0123456789abcdef",
            Mosslight.Core.Services.Engine.LaunchPlanBuilder.UuidForLaunch(
                "01234567-89AB-CDEF-0123-456789ABCDEF"));

        var soon = new Mosslight.Core.Services.Account("Steve", Mosslight.Core.Services.AccountKind.ThirdParty,
            "u", null, DateTimeOffset.Now, "at", "rt", "https://littleskin.cn/api/yggdrasil", "ct",
            DateTimeOffset.Now.AddHours(1));
        Check("续期：1 小时后过期 → 需要续", Mosslight.Core.Services.Auth.ThirdPartyTokens.NeedsRefresh(soon));
        Check("续期：48 小时后过期 → 不续",
            !Mosslight.Core.Services.Auth.ThirdPartyTokens.NeedsRefresh(
                soon with { TokenExpiresAt = DateTimeOffset.Now.AddHours(48) }));
        Check("续期：不知道过期时间 → 不续",
            !Mosslight.Core.Services.Auth.ThirdPartyTokens.NeedsRefresh(soon with { TokenExpiresAt = null }));
        Check("续期：离线账户永不续",
            !Mosslight.Core.Services.Auth.ThirdPartyTokens.NeedsRefresh(
                soon with { Kind = Mosslight.Core.Services.AccountKind.Offline }));

        var authJson = """
        {"accessToken":"at-9","clientToken":"ct-9","selectedProfile":{"id":"0123456789abcdef0123456789abcdef","name":"Steve"},
         "availableProfiles":[{"id":"0123456789abcdef0123456789abcdef","name":"Steve"},
                              {"id":"fedcba9876543210fedcba9876543210","name":"Alex"}]}
        """;
        var auth = Mosslight.Core.Services.Auth.CustomYggdrasilService.ParseAuth(authJson);
        Check("Yggdrasil：密码登录解析成功", auth.Ok);
        Equal("Yggdrasil：accessToken", "at-9", auth.AccessToken);
        Equal("Yggdrasil：clientToken", "ct-9", auth.ClientToken);
        Equal("Yggdrasil：selectedProfile", "Steve", auth.SelectedProfile?.Name ?? "");
        Check("Yggdrasil：两个角色都在", auth.AvailableProfiles.Count == 2, $"实际 {auth.AvailableProfiles.Count}");
        Equal("Yggdrasil：uuid 带连字符",
            "01234567-89ab-cdef-0123-456789abcdef", auth.AvailableProfiles[0].Uuid);

        var authError = Mosslight.Core.Services.Auth.CustomYggdrasilService.ParseAuth(
            "{\"error\":\"ForbiddenOperationException\",\"errorMessage\":\"邮箱或密码错误\"}");
        Check("Yggdrasil：失败时带 errorMessage",
            !authError.Ok && authError.Error == "邮箱或密码错误", authError.Error);

        Equal("错误：优先 error_description",
            "the code is invalid",
            Mosslight.Core.Services.Auth.LittleSkinAuthService.ReadError(
                "{\"error\":\"invalid_grant\",\"error_description\":\"the code is invalid\"}") ?? "");
        Equal("错误：Yggdrasil 的 errorMessage 也认",
            "无效的令牌",
            Mosslight.Core.Services.Auth.LittleSkinAuthService.ReadError(
                "{\"error\":\"ForbiddenOperationException\",\"errorMessage\":\"无效的令牌\"}") ?? "");
        Check("错误：不是 JSON → null",
            Mosslight.Core.Services.Auth.LittleSkinAuthService.ReadError("<html>500</html>") is null);
    }

    // 自动更新

    /// <summary>自动更新那一套：环境变量拦截、版本比较、SHA256、写盘脚本、config.ini。</summary>
    private static void CheckUpdates()
    {
        Section("自动更新：环境变量 / 版本比较 / SHA256 / 替换脚本");

        var oldValue = Environment.GetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar, "TRUE");
            Check("MOSSLIGHT_DISABLE_UPDATE=TRUE（大写）→ 关掉自动更新",
                Mosslight.Core.Updates.UpdateManager.IsDisabledByEnvironment());
            Environment.SetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar, "1");
            Check("MOSSLIGHT_DISABLE_UPDATE=1 → 关掉自动更新",
                Mosslight.Core.Updates.UpdateManager.IsDisabledByEnvironment());
            Environment.SetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar, "0");
            Check("MOSSLIGHT_DISABLE_UPDATE=0 → 不关",
                !Mosslight.Core.Updates.UpdateManager.IsDisabledByEnvironment());
            Environment.SetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar, null);
            Check("没设环境变量 → 不关", !Mosslight.Core.Updates.UpdateManager.IsDisabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Mosslight.Core.Updates.UpdateManager.DisableEnvVar, oldValue);
        }

        Check("v2.10.2 → 2.10.2",
            Mosslight.Core.Updates.UpdateManager.TryParseVersion("v2.10.2", out var v) && v == new Version(2, 10, 2, 0));
        Check("v2.10.2-beta → 2.10.2（后缀丢掉）",
            Mosslight.Core.Updates.UpdateManager.TryParseVersion("v2.10.2-beta", out var pre) && pre == new Version(2, 10, 2, 0));
        Check("乱写的版本号认不出来",
            !Mosslight.Core.Updates.UpdateManager.TryParseVersion("banana", out _));

        Check("临时目录不算安装目录",
            !Mosslight.Core.Updates.UpdateManager.IsSafeInstallDir(Path.GetTempPath(), out _));
        Check("正常目录算安装目录",
            Mosslight.Core.Updates.UpdateManager.IsSafeInstallDir(@"D:\Games\Mosslight", out _));
        Check("盘符根不算安装目录",
            !Mosslight.Core.Updates.UpdateManager.IsSafeInstallDir(@"D:\", out _));

        const string releaseJson = """
        {
          "tag_name": "v9.9.9",
          "html_url": "https://github.com/kongxia114/Mosslight-Launcher-2/releases/tag/v9.9.9",
          "assets": [
            { "name": "Mosslight-2.0-v9.9.9-win64.zip", "size": 1462185,
              "browser_download_url": "https://example.invalid/small.zip",
              "digest": "sha256:f77fea9d2e191985a6ff4b6fc31c361ee7bf3c3797e9f21d6b998992c8a60ec2" }
          ]
        }
        """;

        var newer = Mosslight.Core.Updates.UpdateManager.ParseRelease(releaseJson, new Version(2, 0, 0, 0));
        Check("看到新版本 → Available", newer.Available && newer.Info?.Tag == "v9.9.9");
        Equal("认的是 win64.zip 那个资产",
            "Mosslight-2.0-v9.9.9-win64.zip", newer.Info?.ZipName ?? "");
        Equal("SHA256 从 digest 里剥出十六进制",
            "f77fea9d2e191985a6ff4b6fc31c361ee7bf3c3797e9f21d6b998992c8a60ec2", newer.Info?.Sha256 ?? "");
        Check("同版本 → 不提示更新",
            !Mosslight.Core.Updates.UpdateManager.ParseRelease(releaseJson, new Version(9, 9, 9, 0)).Available);
        const string fullBody = "## 更新内容\n\n- **修好了** `A`\n- 加了个功能\n\n---\n\n## 下载\n\n小的 1.4 MB，大的 55 MB";
        Equal("更新说明：在 --- 处截断", "## 更新内容\n\n- **修好了** `A`\n- 加了个功能",
            Mosslight.Core.Updates.UpdateManager.CleanReleaseNotes(fullBody));
        Equal("更新说明：没有分割线时在 ## 下载 处截断", "## 更新内容\n\n- 只有日志",
            Mosslight.Core.Updates.UpdateManager.CleanReleaseNotes(
                "## 更新内容\n\n- 只有日志\n\n## 下载\n\n这些不该出现"));
        Equal("更新说明：空正文 → 空串", "",
            Mosslight.Core.Updates.UpdateManager.CleanReleaseNotes("   \n\n"));
        var longBody = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"- 第 {i} 行"));
        var cut = Mosslight.Core.Updates.UpdateManager.CleanReleaseNotes(longBody);
        Check($"更新说明：超过 {Mosslight.Core.Updates.UpdateManager.MaxNoteLines} 行会截断",
            cut.EndsWith("…") && cut.Split('\n').Length == Mosslight.Core.Updates.UpdateManager.MaxNoteLines + 1,
            $"实际 {cut.Split('\n').Length} 行");

        var forced = Mosslight.Core.Updates.UpdateManager.ParseRelease(
            releaseJson.Replace("\"v9.9.9\"", "\"v9.9.9-force\""), new Version(2, 0, 0, 0));
        Check("tag 带 -force → IsForced（版本号照样认成 9.9.9）",
            forced.Available && forced.Info?.IsForced == true && forced.Info?.Version == new Version(9, 9, 9, 0));
        Check("普通 tag → 不是强制更新", newer.Info?.IsForced == false);

        Check("刚更新完：记录 1.0.0 < 当前 2.0.0",
            Mosslight.Core.Updates.UpdateManager.IsJustUpdated(new Version(2, 0, 0, 0), "1.0.0.0"));
        Check("记录等于当前 → 不算刚更新",
            !Mosslight.Core.Updates.UpdateManager.IsJustUpdated(new Version(2, 0, 0, 0), "2.0.0.0"));
        Check("记录比当前新（还没替换）→ 不算刚更新",
            !Mosslight.Core.Updates.UpdateManager.IsJustUpdated(new Version(2, 0, 0, 0), "9.9.9.0"));
        Check("没有记录（全新安装）→ 不算刚更新",
            !Mosslight.Core.Updates.UpdateManager.IsJustUpdated(new Version(2, 0, 0, 0), null));

        Check("坏 JSON → 不崩、返回不可用",
            !Mosslight.Core.Updates.UpdateManager.ParseRelease("{ 这不是 json", new Version(1, 0, 0, 0)).Available);

        var probe = Path.Combine(Path.GetTempPath(), "mosslight-sha-probe.bin");
        File.WriteAllText(probe, "mosslight");
        Equal("SHA256 算对了", "f77fea9d2e191985a6ff4b6fc31c361ee7bf3c3797e9f21d6b998992c8a60ec2",
            Mosslight.Core.Updates.UpdateManager.Sha256OfFile(probe));
        File.Delete(probe);

        var script = Mosslight.Core.Updates.UpdateManager.BuildApplyScript("Mosslight启动器.exe");
        Check("替换脚本：是 .bat（不是 PowerShell）",
            script.StartsWith("@echo off", StringComparison.Ordinal) && !script.Contains("Expand-Archive"));
        Check("替换脚本：等主程序退出（timeout 3 秒）", script.Contains("timeout /t 3 /nobreak"));
        Check("替换脚本：TARGET_NAME 由 C# 写死",
            script.Contains("set \"TARGET_NAME=Mosslight启动器.exe\""));
        Check("替换脚本：精确删除（绝不 *.exe 通配符）",
            script.Contains("del /Q \"%~dp0%TARGET_NAME%\"") && !script.Contains("*.exe"));
        Check("替换脚本：过渡 exe 在 Temp 里",
            script.Contains(@"%~dp0Temp\Mosslight_update.exe"));
        Check("替换脚本：move 用完整路径改名",
            script.Contains("move /Y \"%NEW_EXE%\" \"%~dp0%TARGET_NAME%\""));
        Check("替换脚本：失败留 update_error.txt（ASCII 错误码）",
            script.Contains("update_error.txt") && script.Contains("MOVE_FAILED"));
        Check("替换脚本：不 chcp（按 ANSI 落盘，chcp 会把中文名字解析成乱码）",
            !script.Contains("chcp"));
        Check("替换脚本：CRLF 行尾", script.Contains("\r\n"));
        Check("替换脚本：最后删自己", script.Contains("del \"%~f0\""));
        var updater = new Mosslight.Core.Updates.UpdateManager(Path.GetTempPath());
        Check("过渡 exe 的落点是 Temp\\Mosslight_update.exe",
            updater.TransitionalExePath.EndsWith(
                Path.Combine("Temp", "Mosslight_update.exe"), StringComparison.OrdinalIgnoreCase),
            updater.TransitionalExePath);

        var missingZip = Path.Combine(Path.GetTempPath(), "mosslight-no-such-update.zip");
        var extractManager = new Mosslight.Core.Updates.UpdateManager(
            Path.Combine(Path.GetTempPath(), "mosslight-extract-probe"));
        var missing = extractManager.ExtractToStaging(missingZip);
        Check("解压时缺包：报错带绝对路径 + 杀软提示",
            !missing.Ok && missing.Message.Contains("杀毒") && missing.Message.Contains("mosslight-no-such-update.zip"),
            missing.Message);

        var cacheDir = Path.Combine(Path.GetTempPath(), "mosslight-cache-probe");
        var cacheManager = new Mosslight.Core.Updates.UpdateManager(cacheDir);
        var cacheUpdate = Path.Combine(cacheDir, "Temp", "Update");
        var cacheNew = Path.Combine(cacheDir, "Temp", "NewVersionFiles");
        Directory.CreateDirectory(cacheUpdate);
        Directory.CreateDirectory(cacheNew);
        File.WriteAllText(Path.Combine(cacheUpdate, "old.zip"), "x");
        File.WriteAllText(Path.Combine(cacheNew, "Mosslight.exe"), "x");
        Check("清缓存：旧 zip 和解压目录都被删掉",
            cacheManager.TryClearUpdateCache(out _)
            && !Directory.Exists(cacheUpdate) && !Directory.Exists(cacheNew));

        // 待展示缓存（pending_update.json）
        var pendingDir = Path.Combine(Path.GetTempPath(), "mosslight-pending-probe");
        Directory.CreateDirectory(pendingDir);
        var pendingManager = new Mosslight.Core.Updates.UpdateManager(pendingDir);
        var pendingInfo = new Mosslight.Core.Updates.UpdateInfo(
            "v2.11.0", new Version(2, 11, 0, 0), "x.zip", "https://example.invalid/x.zip",
            0, "sha", string.Empty, IsForced: true, Notes: "## 更新内容\n\n- **加粗**\n- 列表");
        Check("写 pending_update.json 成功", pendingManager.TryWritePendingUpdate(pendingInfo, out _));
        var read = Mosslight.Core.Updates.UpdateManager.ReadPendingUpdate(pendingManager.PendingFilePath);
        Equal("pending：版本读得回来", "2.11.0.0", read?.Version ?? "");
        Equal("pending：tag 读得回来", "v2.11.0", read?.Tag ?? "");
        Check("pending：强制标记读得回来", read?.IsForced == true);
        Check("pending：正文里的 markdown 原样保留", read?.Notes.Contains("**加粗**") == true);

        File.WriteAllText(pendingManager.PendingFilePath, "{ 这不是 json");
        Check("pending：坏 JSON → null（不抛异常）",
            Mosslight.Core.Updates.UpdateManager.ReadPendingUpdate(pendingManager.PendingFilePath) is null);
        File.WriteAllText(pendingManager.PendingFilePath, "{\"version\": 123}");
        Check("pending：字段类型不对 → null",
            Mosslight.Core.Updates.UpdateManager.ReadPendingUpdate(pendingManager.PendingFilePath) is null);
        File.WriteAllText(pendingManager.PendingFilePath, string.Empty);
        Check("pending：空文件 → null",
            Mosslight.Core.Updates.UpdateManager.ReadPendingUpdate(pendingManager.PendingFilePath) is null);

        Mosslight.Core.Updates.UpdateManager.DeletePendingUpdate(pendingManager.PendingFilePath);
        Check("pending：删掉之后就不存在了",
            !File.Exists(pendingManager.PendingFilePath) &&
            Mosslight.Core.Updates.UpdateManager.ReadPendingUpdate(pendingManager.PendingFilePath) is null);

        var realExe = Mosslight.Core.Updates.UpdateManager.GetRealExeName();
        Check("真实 exe 名认得出来（不写死 Mosslight.exe）",
            realExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), realExe);

        var markerPath = Path.Combine(Path.GetTempPath(), "mosslight-update_error-probe.json");
        File.WriteAllText(markerPath, "MOVE_FAILED");
        Equal("失败标记读得出来（ASCII 错误码）", "MOVE_FAILED",
            Mosslight.Core.Updates.UpdateManager.ReadErrorMarker(markerPath) ?? "");
        Mosslight.Core.Updates.UpdateManager.DeleteErrorMarker(markerPath);
        Check("失败标记删得掉",
            Mosslight.Core.Updates.UpdateManager.ReadErrorMarker(markerPath) is null);

        var iniDir = Path.Combine(Path.GetTempPath(), "mosslight-ini-probe");
        Directory.CreateDirectory(iniDir);
        var iniPath = Mosslight.Core.Storage.GameConfigFile.PathFor(iniDir);
        if (File.Exists(iniPath)) File.Delete(iniPath);
        Check("config.ini 不存在 → 建一份默认的",
            Mosslight.Core.Storage.GameConfigFile.Ensure(iniDir, out var made, out _) && File.Exists(made));
        Equal("默认 config.ini 里 minecraft_dir 是空的", "",
            Mosslight.Core.Storage.GameConfigFile.ReadMinecraftDir(made));
        File.WriteAllText(iniPath, "[game]\r\nminecraft_dir = D:\\mc\r\n");
        Equal("config.ini 里的 minecraft_dir 读得出来", @"D:\mc",
            Mosslight.Core.Storage.GameConfigFile.ReadMinecraftDir(iniPath));
        File.Delete(iniPath);
    }

    private static void CheckLocalization()
    {
        // ⚠️ 故意初始化到不存在的目录：发布产物没有 Assets\lang，语言必须来自内嵌资源。
        Localizer.Init(Path.Combine(Path.GetTempPath(), "mosslight-selfcheck-no-lang-dir"));
        _ = Localizer.SetLanguageWithSource(Localizer.FallbackLanguage);

        // 内嵌资源本身

        var embedded = Localizer.EmbeddedLanguages();
        Equal("内嵌语言恰好是这 5 种", string.Join(",", ExpectedLanguages), string.Join(",", embedded));
        // LanguageNames 是下拉框显示名的那张表；内嵌资源名是从 csproj 的文件来的，
        // 两边对不上就意味着"有个语言没显示名"或者"表里有个语言读不到"
        Equal("内嵌语言跟 LanguageNames 的表一致",
            string.Join(",", Localizer.LanguageNames.Keys.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(",", embedded.OrderBy(x => x, StringComparer.Ordinal)));

        // ⚠️ 五份内嵌语言文件的键集合必须逐一比对；只看中文正常会漏掉其他语言整条退回中文的问题。
        CheckEmbeddedLanguageKeySets();

        // ⚠️ 名单表文案由变量传给 Localizer，脚本扫不到；自检要独立覆盖这类 key。
        CheckCreditTexts();

        // 验证 Localizer 的实际加载路径：资源名、前缀与 RootNamespace、JSON 结构都在这一步暴露。
        foreach (var code in ExpectedLanguages)
        {
            var source = Localizer.SetLanguageWithSource(code);
            Equal($"Localizer 取 {code} 走的是内嵌", nameof(Localizer.LanguageSource.Embedded),
                source.ToString());
            Equal($"切到 {code} 后 CurrentLanguage", code, Localizer.CurrentLanguage);
            Check($"{code} 经 Localizer 读到 >500 条文案", CountKeys(code) > 500, $"{CountKeys(code)} 条");
        }

        // 全部退回中文后，下面几条基于中文的断言才稳定
        Localizer.SetLanguage("zh_CN");

        // 内嵌那份是**真的**（不是名字在、内容是空壳）：
        // 挑一条中文原文，切到英文后必须真的变了
        Localizer.SetLanguage("zh_CN");
        var chinese = Localizer.Tr("启动游戏");
        Localizer.SetLanguage("en_US");
        var english = Localizer.Tr("启动游戏");
        Check("同一个 key 在中英下不一样", chinese != english, $"{chinese} / {english}");
        Check("英文那份不是空壳（没退回中文源文）", english != "启动游戏", english);
        Localizer.SetLanguage("zh_CN");

        // 查不到的 key 必须原样返回中文，绝不能露出 key 本身
        Equal("查不到的 key 原样返回", "这句话没翻译", Localizer.Tr("这句话没翻译"));

        // 占位符
        Localizer.SetLanguage("zh_CN");
        var filled = Localizer.Tr("版本 {name}", ("name", "1.20.5"));
        Check("占位符被替换", filled.Contains("1.20.5"), filled);
        var fallback = Localizer.Tr("没有这条 {text}", ("text", "x"));
        Check("译文/源文占位符对不上时不抛异常", fallback.Length > 0, fallback);

        // 系统语言的归一化
        foreach (var (tag, expected) in new (string, string)[]
                 {
                     ("zh-CN", "zh_CN"), ("zh_TW", "zh_TW"), ("zh-Hant-TW", "zh_TW"),
                     ("en-US", "en_US"), ("ja_JP", "ja_JP"), ("ru-RU", "ru_RU"),
                     ("Chinese (Simplified)_China", "zh_CN"), ("de-DE", "en_US"), ("", ""),
                 })
        {
            Equal($"NormalizeLocale(\"{tag}\")", expected, Localizer.NormalizeLocale(tag));
        }

        Localizer.SetLanguage("不存在的语言");
        Check("切到不存在的语言会退回 zh_CN", Localizer.CurrentLanguage == "zh_CN",
            Localizer.CurrentLanguage);
    }

    /// <summary>把内嵌的某种语言解析出来，只数键的个数（给断言当证据用）。</summary>
    private static int CountKeys(string code)
    {
        var bytes = ReadEmbeddedLanguage(code);
        if (bytes is null) return 0;
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.EnumerateObject().Count();
    }

    /// <summary>按资源名把内嵌的语言 json 取出来；⚠️ 资源名是**照抄 Localizer 的拼法**（MSBuild 的 EmbeddedResource 默认命名：根命名空间 + 相对目录 + 文件名）。</summary>
    private static byte[]? ReadEmbeddedLanguage(string code)
    {
        var asm = typeof(Localizer).Assembly;
        var name = $"Mosslight.Core.Localization.lang.{code}.json";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>内嵌的五个语言文件，键集合必须完全一致（键顺序可以不同 —— 那是给 review 看的，不值得为它报错，所以这里比的是**集合**）；⚠️ 数据源是**内嵌资源**，不是磁盘文件：语言文件已经从 `Mosslight.App\Assets\lang\` 挪到 `Mosslight.Core\Localization\lang\` 并被编进程序集了，发布产物里没有那个目录。</summary>
    private static void CheckEmbeddedLanguageKeySets()
    {
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var name in ExpectedLanguages)
        {
            var bytes = ReadEmbeddedLanguage(name);
            if (bytes is null)
            {
                Check($"内嵌资源里有 {name}.json", false,
                    $"Mosslight.Core.Localization.lang.{name}.json");
                return;
            }

            // ⚠️ 解析前先剥 UTF-8 BOM，否则 JsonDocument.Parse 会抛未捕获异常，整节自检崩掉。
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            using var doc = JsonDocument.Parse(bytes);
            sets[name] = doc.RootElement.EnumerateObject().Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            Check($"内嵌 {name}.json 有内容", sets[name].Count > 500, $"{sets[name].Count} 条");
        }

        var baseline = sets["zh_CN"];
        foreach (var name in ExpectedLanguages.Skip(1))
        {
            var missing = baseline.Except(sets[name]).ToList();
            var extra = sets[name].Except(baseline).ToList();
            Check($"{name} 的键跟 zh_CN 一字不差",
                missing.Count == 0 && extra.Count == 0,
                missing.Count > 0 ? $"缺 {missing.Count} 条（{string.Join(" / ", missing.Take(3))}）"
                : extra.Count > 0 ? $"多 {extra.Count} 条（{string.Join(" / ", extra.Take(3))}）"
                : $"{sets[name].Count} 条");
        }
    }

    /// <summary>「更多」页那几张名单表的文案，每个语言都得有译文；⚠️ 为什么单开一节：`Views/Credits.cs` 里的说明和按钮文案是**变量**传给 `Localizer.Tr` 的（数据表驱动），`tools\check_lang_keys.py` 只扫源码字面量，看不见它们。</summary>
    private static void CheckCreditTexts()
    {
        var texts = new List<string>();
        foreach (var list in new[] { Mosslight.App.Views.Credits.About, Mosslight.App.Views.Credits.Thanks, Mosslight.App.Views.Credits.Links })
        {
            foreach (var entry in list)
            {
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    texts.Add(entry.Description);
                }
                if (!string.IsNullOrEmpty(entry.ButtonLabel))
                {
                    texts.Add(entry.ButtonLabel);
                }
            }
        }

        var distinct = texts.Distinct(StringComparer.Ordinal).ToList();
        Check("「更多」页名单的文案收集到了", distinct.Count > 0, $"{distinct.Count} 条");

        foreach (var language in ExpectedLanguages)
        {
            var missing = distinct.Where(t => !Localizer.HasTranslation(language, t)).ToList();
            Check($"{language}: 「更多」页名单文案都有译文",
                missing.Count == 0,
                missing.Count == 0
                    ? $"{distinct.Count} 条"
                    : $"缺 {missing.Count} 条（{string.Join(" / ", missing.Take(3))}）");
        }
    }

    // 入场动画预设

    /// <summary>动画预设金标准：期望值照抄 Python，不从 Presets 反推。</summary>
    private sealed record PresetCase(
        string Key, AnimationDirection Direction, int DurationMs, int OffsetPx, int IntervalMs,
        AnimationStyle Style = AnimationStyle.Simple, string Ease = "cubic");

    private static void CheckAnimationPresets()
    {
        // ⚠️ 顺序照 PRESET_ORDER，不是 PRESETS 字典顺序（slide_left_back 与 slide_up_elastic 相反）；Key / Name / PresetOrder 不可动。
        // 时长 / 位移 / 错峰等数值按当前手感表为准，金标准只约束轨迹引擎。
        var expected = new PresetCase[]
        {
            new("slide_left",         AnimationDirection.Horizontal, 330, 120,  60),
            new("slide_up",           AnimationDirection.Vertical,   380,  84,  70, Ease: "quint"),
            new("slide_left_bounce",  AnimationDirection.Horizontal, 460, 120,  85,
                AnimationStyle.Spring, "cubic"),
            new("pcl_like",           AnimationDirection.Vertical,   300,  26,  45, Ease: "cubic"),
            new("slide_up_elastic",   AnimationDirection.Vertical,   560,  52,  55, Ease: "elastic"),
            new("slide_left_back",    AnimationDirection.Horizontal, 360, 120,  65, Ease: "back:0.10"),
            new("pcl_recipe",         AnimationDirection.Vertical,   300,  24,  32, AnimationStyle.Pcl, "cubic"),
        };

        Check("一共 7 个预设", AnimationPresets.Presets.Count == 7,
            $"实际 {AnimationPresets.Presets.Count}");
        Equal("PresetOrder 长度", "7", AnimationPresets.PresetOrder.Count.ToString());
        Check("PresetOrder 顺序跟 Python 一致",
            AnimationPresets.PresetOrder.SequenceEqual(expected.Select(c => c.Key), StringComparer.Ordinal),
            string.Join(", ", AnimationPresets.PresetOrder));
        Check("PRESETS 的键跟 PresetOrder 对得上",
            AnimationPresets.Presets.Count == AnimationPresets.PresetOrder.Count
                && AnimationPresets.PresetOrder.All(AnimationPresets.Presets.ContainsKey)
                && AnimationPresets.Presets.Values.All(p => AnimationPresets.PresetOrder.Contains(p.Key)));
        Equal("DefaultPreset", "slide_left", AnimationPresets.DefaultPreset);
        Equal("DefaultSpeed", "normal", AnimationPresets.DefaultSpeed);

        // ⚠️ 逐字段比：界面文案也算进去 —— 文案错一个字，i18n 查表就整条退回中文
        foreach (var c in expected)
        {
            var p = AnimationPresets.Presets[c.Key];
            Equal($"{c.Key} · Key", c.Key, p.Key);
            Equal($"{c.Key} · Direction", c.Direction.ToString(), p.Direction.ToString());
            Equal($"{c.Key} · DurationMs", c.DurationMs.ToString(), p.DurationMs.ToString());
            Equal($"{c.Key} · OffsetPx", c.OffsetPx.ToString(), p.OffsetPx.ToString());
            Equal($"{c.Key} · IntervalMs", c.IntervalMs.ToString(), p.IntervalMs.ToString());
            Equal($"{c.Key} · Style", c.Style.ToString(), p.Style.ToString());
            Equal($"{c.Key} · Ease", c.Ease, p.Ease);
        }

        // 文案单独列：它们是**多语言的 key**（zh_CN.json 里能查到），抄错就等于丢翻译
        Equal("slide_left 名字", "左侧滑入", AnimationPresets.Presets["slide_left"].Name);
        Equal("slide_left 说明", "从左边滑进来，一步到位（默认）",
            AnimationPresets.Presets["slide_left"].Description);
        Equal("pcl_recipe 名字", "PCL 原味（拆两条 + 淡入）",
            AnimationPresets.Presets["pcl_recipe"].Name);
        Check("pcl_recipe 的说明含那句建议",
            AnimationPresets.Presets["pcl_recipe"].Description.Contains("建议先试它"));

        Equal("slide_left_bounce · OvershootPx", "18",
            AnimationPresets.Presets["slide_left_bounce"].OvershootPx.ToString());
        Equal("slide_left_bounce · BounceRatio", "0.6",
            AnimationPresets.Presets["slide_left_bounce"].BounceRatio.ToString());
        // 默认值也别放过：其余 6 条用的都是它
        Equal("slide_left · OvershootPx 默认", "12",
            AnimationPresets.Presets["slide_left"].OvershootPx.ToString());
        Equal("slide_left · BounceRatio 默认", "0.72",
            AnimationPresets.Presets["slide_left"].BounceRatio.ToString());

        // ⚠️ FadeRatio 是调参值：透明度在整条轨迹 × FadeRatio 时到 1，目标是视觉落位前 15~180ms 淡入完成。
        foreach (var (key, ratio) in new (string, double)[]
                 {
                     ("slide_left", 0.75), ("slide_up", 0.65), ("slide_left_bounce", 0.45),
                     ("pcl_like", 0.75), ("slide_up_elastic", 0.21), ("slide_left_back", 0.45),
                     ("pcl_recipe", 0.42),
                 })
        {
            Equal($"{key} · FadeRatio", ratio.ToString("0.##"),
                AnimationPresets.Presets[key].FadeRatio.ToString("0.##"));
        }
        Check("默认 FadeRatio 是 1.0（= 改动之前的行为，金标准那些对照用例靠它）",
            new AnimationPreset("x", "x", "x", AnimationDirection.Horizontal, 100, 10, 10).FadeRatio == 1.0);

        // 逐档验手感不变量：单档顺、七档能区分；判据与 HandProbe 一致，但自检不依赖探针。
        CheckPresetHandfeel();

        // 认不出来不许抛

        Check("Preset(\"乱写的\") 回默认",
            AnimationPresets.Preset("乱写的").Key == AnimationPresets.DefaultPreset);
        Check("Preset(null) 回默认", AnimationPresets.Preset(null).Key == AnimationPresets.DefaultPreset);
        Check("Preset(\"\") 回默认", AnimationPresets.Preset("").Key == AnimationPresets.DefaultPreset);
        // 大小写**不**做宽容：Python 是 `PRESETS.get(key)` 精确查表，大写就是查不到。
        // 这里要是"顺手"加了忽略大小写，两边行为就分叉了
        Check("Preset(\"SLIDE_LEFT\") 回默认",
            AnimationPresets.Preset("SLIDE_LEFT").Key == AnimationPresets.DefaultPreset);

        Check("SpeedFactor(\"乱写的\") == 1.0", AnimationPresets.SpeedFactor("乱写的") == 1.0);
        Check("SpeedFactor(null) == 1.0", AnimationPresets.SpeedFactor(null) == 1.0);

        // 速度表本身
        foreach (var (key, factor) in new (string, double)[]
                 {
                     ("fast", 0.7), ("normal", 1.0), ("slow", 1.5),
                     ("very_slow", 2.2), ("extreme", 3.2),
                 })
        {
            Equal($"Speeds[{key}]", factor.ToString(), AnimationPresets.Speeds[key].ToString());
            Equal($"SpeedFactor({key})", factor.ToString(), AnimationPresets.SpeedFactor(key).ToString());
        }
        Equal("SpeedOrder 顺序", "fast, normal, slow, very_slow, extreme",
            string.Join(", ", AnimationPresets.SpeedOrder));
        Equal("SpeedText(normal)", "标准（1×）", AnimationPresets.SpeedText("normal"));
        Equal("SpeedText(very_slow)", "很慢（2.2×）", AnimationPresets.SpeedText("very_slow"));
        Equal("SpeedText(乱写的) 回默认文案", "标准（1×）", AnimationPresets.SpeedText("乱写的"));
        Equal("DirectionText(h)", "水平（左右）",
            AnimationPresets.DirectionText(AnimationDirection.Horizontal));
        Equal("DirectionText(v)", "垂直（上下）",
            AnimationPresets.DirectionText(AnimationDirection.Vertical));

        // 缩放

        var slideLeft = AnimationPresets.Preset("slide_left");
        // 1.0 必须**原样返回**（不是"算一遍结果碰巧一样"）：调用方靠它跳过整条动画
        Check("Scaled(p, 1.0) 原样返回", ReferenceEquals(slideLeft, AnimationPresets.Scaled(slideLeft, 1.0)));
        Check("Scaled(p, 1.0) 只动时长和错峰",
            AnimationPresets.Scaled(slideLeft, 1.0) == slideLeft);

        var scaled = AnimationPresets.Scaled(slideLeft, 2.2);
        Equal("Scaled(slide_left, 2.2) · DurationMs 330×2.2=726", "726", scaled.DurationMs.ToString());
        Equal("Scaled(slide_left, 2.2) · IntervalMs 60×2.2=132", "132", scaled.IntervalMs.ToString());
        Equal("Scaled(slide_left, 2.2) · OffsetPx 不变", "120", scaled.OffsetPx.ToString());
        Equal("Scaled(slide_left, 2.2) · Ease 不变", "cubic", scaled.Ease);
        Equal("Scaled(slide_left, 2.2) · Key 不变", "slide_left", scaled.Key);
        // 淡入比例也**不该**跟着倍率变：它是比例，Timespan 那边自己会乘上去
        Equal("Scaled(slide_left, 2.2) · FadeRatio 不变", "0.75", scaled.FadeRatio.ToString("0.##"));

        // 下限：倍率小到离谱时，别让动画退化成"瞬移"（40ms 单张 / 8ms 错峰）
        var clamped = AnimationPresets.Scaled(AnimationPresets.Preset("slide_up"), 0.01);
        Equal("Scaled(slide_up, 0.01) · DurationMs 被夹到 40", "40", clamped.DurationMs.ToString());
        Equal("Scaled(slide_up, 0.01) · IntervalMs 被夹到 8", "8", clamped.IntervalMs.ToString());

        // 版本列表的短参数
        // ⚠️ ForVersions 只改 duration / interval / offset：形态和缓动必须还是主表那一套，
        // 改错了"版本列表"就会变成另一种动画，而不是同一种的加快版。
        var versionsPclRecipe = AnimationPresets.ForVersions("pcl_recipe");
        Equal("ForVersions(pcl_recipe) · DurationMs", "255", versionsPclRecipe.DurationMs.ToString());
        Equal("ForVersions(pcl_recipe) · IntervalMs", "26", versionsPclRecipe.IntervalMs.ToString());
        Equal("ForVersions(pcl_recipe) · OffsetPx", "20", versionsPclRecipe.OffsetPx.ToString());
        Equal("ForVersions(pcl_recipe) · Style 跟主表一致", "Pcl", versionsPclRecipe.Style.ToString());
        Equal("ForVersions(pcl_recipe) · Ease 跟主表一致", "cubic", versionsPclRecipe.Ease);
        Equal("ForVersions(pcl_recipe) · FadeRatio 跟主表一致", "0.42",
            versionsPclRecipe.FadeRatio.ToString("0.##"));

        var versionsPclLike = AnimationPresets.ForVersions("pcl_like");
        Equal("ForVersions(pcl_like) · DurationMs", "255", versionsPclLike.DurationMs.ToString());
        Equal("ForVersions(pcl_like) · IntervalMs", "35", versionsPclLike.IntervalMs.ToString());
        Equal("ForVersions(pcl_like) · OffsetPx", "22", versionsPclLike.OffsetPx.ToString());
        Equal("ForVersions(pcl_like) · Ease 跟主表一致", "cubic", versionsPclLike.Ease);

        // 逐条照抄 VERSION_VARIANT，免得只验了被点名的两个
        foreach (var (key, duration, interval, offset) in new (string, int, int, int)[]
                 {
                     ("slide_left", 280, 55, 80), ("slide_up", 340, 65, 60),
                     ("slide_left_bounce", 390, 65, 90), ("pcl_like", 255, 35, 22),
                     ("slide_left_back", 305, 50, 80), ("slide_up_elastic", 440, 48, 75),
                     ("pcl_recipe", 255, 26, 20),
                 })
        {
            var variant = AnimationPresets.ForVersions(key);
            Equal($"ForVersions({key}) · DurationMs", duration.ToString(), variant.DurationMs.ToString());
            Equal($"ForVersions({key}) · IntervalMs", interval.ToString(), variant.IntervalMs.ToString());
            Equal($"ForVersions({key}) · OffsetPx", offset.ToString(), variant.OffsetPx.ToString());
            Equal($"ForVersions({key}) · Key 不变", key, variant.Key);
        }

        // 认不出来的 key 先退回默认预设，再取默认那条的变体 —— 不是直接抛
        var versionsFallback = AnimationPresets.ForVersions("乱写的");
        Equal("ForVersions(乱写的) · Key 回默认", "slide_left", versionsFallback.Key);
        Equal("ForVersions(乱写的) · DurationMs 是默认那条的变体", "280",
            versionsFallback.DurationMs.ToString());
    }


    /// <summary>七档的**手感不变量**：每一档单看要顺，七档合起来要分得开；⚠️ 为什么不跟 HandProbe 探针共用一份实现：探针在实验目录里、**不进仓库**，自检必须能独立跑。</summary>
    private static void CheckPresetHandfeel()
    {
        const double step = 0.25;
        var rows = new List<(string Key, double Total, double Visual, double OnePx, double FourPx, double V0)>();

        foreach (var key in AnimationPresets.PresetOrder)
        {
            var p = AnimationPresets.Preset(key);
            var total = SlideInTrajectory.TotalDurationMs(p);
            var offset = Math.Max(1, p.OffsetPx);
            rows.Add((
                key,
                total,
                FirstAt(p, total, step, d => d <= 2.0),
                LastAt(p, total, step, d => Math.Abs(d) > 1.0),
                LastAt(p, total, step, d => Math.Abs(d) > 4.0),
                (offset - SlideInTrajectory.DisplacementAt(p, 16.0)) / 16.0));
        }

        foreach (var r in rows)
        {
            Check($"{r.Key} · 视觉落位在 80% 时长之内",
                r.Visual / r.Total <= 0.80, $"{r.Visual / r.Total * 100:F0}%");
            // 1px 尾巴：>45% 才算停不稳。**回弹档另有口子** —— 只要视觉落位早于 10%
            // 时长，尾巴本来就是"弹完还在收"，不是"到位慢"
            // （弹性那档实测落位在 7%、1px 尾巴 51%：它在 40ms 就看着到位了，
            // 后面 283ms 全是阻尼摆动 —— 判它"停不稳"是判错了对象）。
            var reboundTail = AnimationRebound.Measure(AnimationPresets.Preset(r.Key)).HasRebound
                              && r.Visual / r.Total <= 0.10;
            Check($"{r.Key} · 1px 尾巴不超过 45% 时长（回弹档落位早于 10% 时不受此限）",
                (r.Total - r.OnePx) / r.Total <= 0.45 || reboundTail,
                $"{(r.Total - r.OnePx) / r.Total * 100:F0}%"
                + (reboundTail ? $"（回弹档，落位在 {r.Visual / r.Total * 100:F0}%）" : string.Empty));
            Check($"{r.Key} · 起步速度 ≥ 位移的 0.85%/ms（起手不发木）",
                r.V0 / Math.Max(1, AnimationPresets.Preset(r.Key).OffsetPx) >= 0.0085,
                $"{r.V0 / Math.Max(1, AnimationPresets.Preset(r.Key).OffsetPx) * 100:F2}%/ms");
        }

        // 回弹：既要有，又要看得见、不夸张
        var reboundKeys = new List<string>();
        foreach (var key in AnimationPresets.PresetOrder)
        {
            var measured = AnimationRebound.Measure(AnimationPresets.Preset(key));
            if (!measured.HasRebound) continue;
            reboundKeys.Add(key);
            Check($"{key} · 过冲不夸张（≤25px）", measured.OvershootPx <= 25.0, $"{measured.OvershootPx:F2}px");
            Check($"{key} · 过冲真的看得见（>0.5px）", measured.OvershootPx > 0.5, $"{measured.OvershootPx:F2}px");
        }
        Check("有可见回弹的至少三档（弹簧 / 弹性 / 过冲）", reboundKeys.Count >= 3,
            string.Join(", ", reboundKeys));

        // 七档是不是一个味道
        var tails = rows.Select(r => (r.Total - r.FourPx) / r.Total).ToList();
        var spread = tails.Max() / Math.Max(1e-9, tails.Min());
        Check("收尾速度拉开 ≥2.5 倍（最后 4px 占时长比 最快/最慢）—— 七档不能一个味道",
            spread >= 2.5, $"{tails.Min() * 100:F0}% ~ {tails.Max() * 100:F0}% = {spread:F2}×");

        const int rowCount = 12;
        var rowTotals = rows.Select(r => r.Total + AnimationPresets.Preset(r.Key).IntervalMs * (rowCount - 1)).ToList();
        Check("整列 12 张铺完 0.6~1.6 秒", rowTotals.All(t => t >= 600 && t <= 1600),
            $"{rowTotals.Min():F0}ms ~ {rowTotals.Max():F0}ms");
        Check("整列铺完时间拉开 ≥1.6 倍", rowTotals.Max() / rowTotals.Min() >= 1.6,
            $"{rowTotals.Min():F0}ms ~ {rowTotals.Max():F0}ms = {rowTotals.Max() / rowTotals.Min():F2}×");
    }

    /// <summary>位移第一次满足条件的那一刻（毫秒）。</summary>
    private static double FirstAt(AnimationPreset p, double total, double step, Func<double, bool> hit)
    {
        for (var t = 0.0; t <= total; t += step)
        {
            if (hit(SlideInTrajectory.DisplacementAt(p, t))) return t;
        }
        return total;
    }

    /// <summary>位移**最后一次**满足条件的那一刻（毫秒）。</summary>
    private static double LastAt(AnimationPreset p, double total, double step, Func<double, bool> hit)
    {
        var last = 0.0;
        for (var t = 0.0; t <= total; t += step)
        {
            if (hit(SlideInTrajectory.DisplacementAt(p, t))) last = t;
        }
        return last;
    }

    // 缓动曲线

    /// <summary>跟 ui/widgets/pcl_ease.py 逐点比对参数化缓动曲线。</summary>
    private static void CheckEasingCurves()
    {
        var goldenPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "tests", "ease_golden.json"));

        if (!File.Exists(goldenPath))
        {
            Check("找得到缓动金标准", false,
                $"缺 {goldenPath}，先生成：python D:\\deepseek\\.dhml-tests\\gen_ease_golden.py");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var root = doc.RootElement;
        var curves = root.GetProperty("curves");
        var tolerance = root.GetProperty("tolerance").GetDouble();
        var count = root.GetProperty("count").GetInt32();

        // 金标准里的 key = 预设表里的 Ease 字符串（"back:0.18" / "elastic" / "spring:0.14"）
        var keys = curves.EnumerateObject().Select(p => p.Name).ToList();

        // ⚠️ 先校验 WPF 的 EaseOut 约定，且必须用内置 CubicEase 实测，不要走 EasingFactory。
        var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var conventionMax = 0.0;
        for (var i = 1; i < count; i++)
        {
            var t = i / 100.0;
            conventionMax = Math.Max(conventionMax, Math.Abs(cubicOut.Ease(t) - (1.0 - Math.Pow(1.0 - t, 3))));
        }
        Check("WPF 约定：EaseOut 时 Ease(t) == 1 - EaseInCore(1-t)（拿内置 OutCubic 实测）",
            conventionMax < 1e-12, $"最大偏差 {conventionMax:E3}");

        // 逐点比对
        var report = new List<string>();
        foreach (var key in keys)
        {
            var curve = curves.GetProperty(key);
            var expected = curve.GetProperty("samples").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var easing = EasingFactory.Create(key);

            var maxError = 0.0;
            var worstT = 0.0;
            for (var i = 0; i < expected.Length; i++)
            {
                var t = i / 100.0;
                var error = Math.Abs(easing.Ease(t) - expected[i]);
                if (error > maxError)
                {
                    maxError = error;
                    worstT = t;
                }
            }

            report.Add($"{key} 最大误差 {maxError:E3}（t={worstT:F2}）");
            Check($"{key} · {expected.Length} 点全在 {tolerance:0.0E+00} 以内", maxError <= tolerance,
                $"最大误差 {maxError:E3} @ t={worstT:F2}，Python 值 {expected[(int)Math.Round(worstT * 100)]:R}");

            // 端点必须精确到位：动画的起止位置靠它，差一点点卡片就"差一像素没归位"
            Check($"{key} · t=0 精确为 0", easing.Ease(0) == 0.0, easing.Ease(0).ToString("R"));
            Check($"{key} · t=1 精确为 1", easing.Ease(1) == 1.0, easing.Ease(1).ToString("R"));
        }

        Console.WriteLine("     " + string.Join("；", report));

        // 三条曲线的峰值都报出来：过冲幅度是"观感像不像"最直接的量化指标
        foreach (var key in keys)
        {
            var expectedPeak = curves.GetProperty(key).GetProperty("peak").GetDouble();
            var actualPeak = SamplePeak(EasingFactory.Create(key), count);
            Console.WriteLine($"     {key,-13} 峰值 C# {actualPeak:F6} / Python {expectedPeak:F6}"
                + $"（过冲 {(actualPeak - 1) * 100:F2}% / {(expectedPeak - 1) * 100:F2}%）");
        }

        // 参数也要对
        //
        // 光比数值还不够：参数错一点点（比如 s 反推的迭代次数不对）可能落在容差边缘，
        // 而"过冲就是 18%"是这一档的验收标准 —— 直接量峰值最直白。
        var back = EasingFactory.Create("back:0.18");
        var backPeak = SamplePeak(back, count);
        var goldenBackPeak = curves.GetProperty("back:0.18").GetProperty("peak").GetDouble();
        Check($"back:0.18 峰值过冲 = 18%（Python 峰值 {goldenBackPeak:F6}）",
            Math.Abs(backPeak - goldenBackPeak) <= tolerance,
            $"C# 峰值 {backPeak:F6}（过冲 {(backPeak - 1) * 100:F2}%）、Python {goldenBackPeak:F6}");
        Check("back:0.18 峰值确实过冲 18%（不依赖 Python 的独立核对）",
            Math.Abs(backPeak - 1.18) <= 1e-4, $"峰值 {backPeak:F6}");

        // s 是二分反推出来的：迭代次数/采样点数抄错都会让它分叉（而曲线整体看着还挺像）
        var backEase = (ParametricBackEase)back;
        var goldenS = curves.GetProperty("back:0.18").GetProperty("s").GetDouble();
        Check("back:0.18 反推的 s 跟 Python 的 s_for_overshoot 一致",
            Math.Abs(backEase.S - goldenS) <= 1e-9, $"C# {backEase.S:R} / Python {goldenS:R}");

        Equal("spring 的 cycles 照抄 SPRING_CYCLES", "1.15",
            ((ParametricDampedSineEase)EasingFactory.Create("spring:0.14")).Cycles.ToString());
        Equal("spring 的 decay 照抄 SPRING_DECAY", "5",
            ((ParametricDampedSineEase)EasingFactory.Create("spring:0.14")).Decay.ToString());
        Equal("spring:0.14 的过冲比例被当作参数传进去", "0.14",
            ((ParametricDampedSineEase)EasingFactory.Create("spring:0.14")).OvershootRatio.ToString());
        Equal("elastic 的 period", "0.3",
            ((ParametricElasticEase)EasingFactory.Create("elastic")).Period.ToString());
        Equal("elastic 的 amplitude", "1",
            ((ParametricElasticEase)EasingFactory.Create("elastic")).Amplitude.ToString());

        // 工厂的映射表
        Check("Create(\"back:0.18\") 是自实现的过冲曲线（不是 WPF 的 BackEase，Amplitude 不是过冲比例）",
            EasingFactory.Create("back:0.18") is ParametricBackEase);
        Check("Create(\"elastic\") 是自实现的弹性曲线（不是 WPF 的 ElasticEase）",
            EasingFactory.Create("elastic") is ParametricElasticEase);
        Check("Create(\"spring:0.14\") 是自实现的阻尼正弦（不是 WPF 的 ElasticEase）",
            EasingFactory.Create("spring:0.14") is ParametricDampedSineEase);

        Check("Create(\"cubic\") → CubicEase/EaseOut",
            EasingFactory.Create("cubic") is CubicEase { EasingMode: EasingMode.EaseOut });
        // ⚠️ 金标准要求 OutQuint 严格等于 1-(1-t)^5；WPF QuinticEase 是另一条多项式，不能留分叉。
        Check("Create(\"quint\") → 自实现的精确 OutQuint（不是 WPF 的 QuinticEase）",
            EasingFactory.Create("quint")
                is ParametricPowerOutEase { Power: 5.0, EasingMode: EasingMode.EaseOut });
        var quint = EasingFactory.Create("quint");
        var quintMaxError = 0.0;
        var wpfQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var wpfQuintMaxError = 0.0;
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100.0;
            quintMaxError = Math.Max(quintMaxError, Math.Abs(quint.Ease(t) - (1.0 - Math.Pow(1.0 - t, 5))));
            wpfQuintMaxError = Math.Max(wpfQuintMaxError, Math.Abs(wpfQuint.Ease(t) - (1.0 - Math.Pow(1.0 - t, 5))));
        }
        Check("quint 跟 Qt 的 OutQuint = 1-(1-t)^5 逐点一致（101 点，1e-12 内）",
            quintMaxError < 1e-12, $"最大偏差 {quintMaxError:E3}");
        // ⚠️ 实测 WPF QuinticEase 就是 1-(1-t)^5；这条断言是哨兵，框架改公式时会红。
        Check("WPF 内置 QuinticEase 实测**就是** 1-(1-t)^5（旧注释说不是，是错的）",
            wpfQuintMaxError < 1e-12,
            $"内置最大偏差 {wpfQuintMaxError:E3}（t=0.5 处 内置 {wpfQuint.Ease(0.5):F8} / "
            + $"精确 {1.0 - Math.Pow(0.5, 5):F8}）");
        Check("Create(\"inout\") → CubicEase/EaseInOut（唯一不是 EaseOut 的）",
            EasingFactory.Create("inout") is CubicEase { EasingMode: EasingMode.EaseInOut });
        // ⚠️ EasingMode 不在 IEasingFunction 上（那是 EasingFunctionBase 的属性），
        // 所以这里按 EasingFunctionBase 断言 —— 工厂返回的确实都是它。
        Check("三条自实现曲线的 EasingMode 都是 EaseOut（反演靠它才对）",
            EasingFactory.Create("back:0.18") is EasingFunctionBase { EasingMode: EasingMode.EaseOut }
                && EasingFactory.Create("elastic") is EasingFunctionBase { EasingMode: EasingMode.EaseOut }
                && EasingFactory.Create("spring:0.14") is EasingFunctionBase { EasingMode: EasingMode.EaseOut });

        // 预设表里在用的每个 ease 都得是认识的曲线名 —— 别再出现"抄了个名字但工厂不认"
        foreach (var preset in AnimationPresets.Presets.Values)
        {
            var created = EasingFactory.Create(preset.Ease);
            Check($"预设 {preset.Key} 的 ease「{preset.Ease}」能造成曲线", created is not null);
        }

        // 认不出来 / 空 → 退回 EaseOut 的 CubicEase，绝不抛
        foreach (var bad in new[] { "乱写的", "", null, "back", "BACK:0.18", "back:", "spring:abc", "cubic:9" })
        {
            var fallback = EasingFactory.Create(bad);
            Check($"Create(\"{bad ?? "(null)"}\") 不抛且是缓动函数", fallback is EasingFunctionBase);
        }
        Check("认不出来的名字退回 EaseOut CubicEase",
            EasingFactory.Create("乱写的") is CubicEase { EasingMode: EasingMode.EaseOut });
        Check("Create(null) 退回 EaseOut CubicEase",
            EasingFactory.Create(null) is CubicEase { EasingMode: EasingMode.EaseOut });
    }

    /// <summary>在 t = 0, 0.01, …, 1 上取曲线峰值（跟金标准的采样点一致，好直接比）。</summary>
    private static double SamplePeak(IEasingFunction easing, int count)
    {
        var peak = double.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var value = easing.Ease(i / 100.0);
            if (value > peak) peak = value;
        }
        return peak;
    }

    // 日志窗口的后端

    /// <summary>日志窗口那一层（LogLine / MinecraftLogReader / LogBus）。</summary>
    private static void CheckLogging()
    {
        // 级别判定：认那四种带定界符的写法
        foreach (var (line, expected) in new (string, LogLevel)[]
                 {
                     // 现代 MC 控制台（log4j2 的 LegacyXMLLayout）
                     ("[12:00:00] [Client thread/INFO]: Loading Minecraft 1.20.1", LogLevel.Info),
                     ("[12:00:00] [Render thread/WARN]: Option 'foo' is deprecated", LogLevel.Warn),
                     ("[12:00:05] [main/ERROR]: Could not load mod 'example'", LogLevel.Error),
                     ("[12:00:05] [main/FATAL]: A fatal error occurred", LogLevel.Fatal),
                     ("[12:00:06] [Worker-Main-1/DEBUG]: Cache hit", LogLevel.Debug),
                     // 1.12 之前那种裸露的 [LEVEL]
                     ("[INFO] something happened", LogLevel.Info),
                     // log4j2 的原始 XML（还没被还原的时候）
                     ("<log4j:Event logger=\"net.minecraft\" level=\"ERROR\" thread=\"main\">", LogLevel.Error),
                     // ⚠️ 不带定界符的裸写法认不出来；放宽成“包含 LEVEL”会把 "no errors found" 误判成错误行。
                     ("12:00:00 ERROR something broke", LogLevel.None),
                 })
        {
            Equal($"DetectLevel(\"{line[..Math.Min(40, line.Length)]}…\")",
                expected.ToString(), LogLine.DetectLevel(line).ToString());
        }

        // ⚠️ 不能简单找子串，否则 "no errors found" 会被误判成错误行。
        Equal("DetectLevel(\"no errors found\") 不认成错误",
            LogLevel.None.ToString(), LogLine.DetectLevel("no errors found").ToString());
        Equal("DetectLevel(\"警告：这东西已过期\") 不认成 WARN（中文不算标记）",
            LogLevel.None.ToString(), LogLine.DetectLevel("警告：这东西已过期").ToString());
        Equal("DetectLevel(null)", LogLevel.None.ToString(), LogLine.DetectLevel(null).ToString());
        Equal("DetectLevel(\"\")", LogLevel.None.ToString(), LogLine.DetectLevel("").ToString());

        // 续行（堆栈）
        Check("缩进行算续行", LogLine.IsContinuation("\tat net.fabricmc.Loader.add(Loader.java:88)"));
        Check("Caused by: 算续行", LogLine.IsContinuation("Caused by: java.lang.NullPointerException"));
        Check("Suppressed: 算续行", LogLine.IsContinuation("Suppressed: java.io.IOException"));
        Check("... 打头算续行", LogLine.IsContinuation("... 12 more"));
        Check("C# 风格的缩进 at 也算续行", LogLine.IsContinuation("   at Foo.Bar()"));
        // ⚠️ 纯空白行**不算**续行，否则空行会把上一条的级别一路继承下去
        Check("空行不算续行", !LogLine.IsContinuation("   "));
        Check("空串不算续行", !LogLine.IsContinuation(""));
        // 启动器自己加的行不缩进、也不以堆栈前缀开头，所以藏了 INFO 也照样看得见
        Check("启动器自己的消息不算续行",
            !LogLine.IsContinuation(LogLine.LauncherPrefix + "游戏已退出（代码 0）"));

        // 噪音
        Check("「Saving chunks for level」是噪音",
            LogLine.IsNoise("[12:00:04] [Server thread/INFO]: Saving chunks for level 'ServerLevel[world]'"));
        Check("「Time elapsed:」是噪音",
            LogLine.IsNoise("[12:00:04] [Server thread/INFO]: Time elapsed: 123 ms"));
        // ⚠️ 启动器自己的话**永远不算噪音** —— 那是用户最需要看的
        Check("带 [启动器] 前缀的行即使是噪音也不滤（启动器优先）",
            !LogLine.IsNoise(LogLine.LauncherPrefix + "Saving chunks for level"));
        Check("正常输出不是噪音",
            !LogLine.IsNoise("[12:00:03] [Render thread/INFO]: OpenGL 4.6.0 NVIDIA 552.22"));
        Check("空的噪音清单 = 什么都不滤", !LogLine.MatchesNoise("anything", Array.Empty<string>()));

        // log4j XML 还原
        var xml = "<log4j:Event logger=\"net.minecraft.client.Minecraft\" timestamp=\"1717200000000\" "
                  + "level=\"INFO\" thread=\"Render thread\">"
                  + "<log4j:Message>Loading Minecraft 1.20.1</log4j:Message>"
                  + "</log4j:Event>";
        var lines = MinecraftLogReader.Render(xml);
        Equal("还原出一条", "1", lines.Count.ToString());
        Check("还原出来的行里带级别 / 线程 / 消息",
            lines[0].Contains("Render thread/INFO", StringComparison.Ordinal)
            && lines[0].Contains("Loading Minecraft 1.20.1", StringComparison.Ordinal),
            lines[0]);
        Check("还原出来的行以 [HH:MM:SS] 开头",
            lines[0].StartsWith("[", StringComparison.Ordinal) && lines[0].IndexOf(']') == 9,
            lines[0]);

        // ⚠️ 必须把输出按行喂给读取器，不能整段丢给 Render：真实 Process.OutputDataReceived 是逐行回调的。
        var throwReader = new MinecraftLogReader();
        var throwLines = new List<string>();
        foreach (var raw in new[]
                 {
                     "<log4j:Event logger=\"a\" timestamp=\"1717200000000\" level=\"ERROR\" thread=\"main\">",
                     "<log4j:Message>boom</log4j:Message>",
                     "<log4j:Throwable><![CDATA[java.lang.NullPointerException: oops",
                     "\tat net.fabricmc.Loader.add(Loader.java:88)",
                     "\tat net.fabricmc.Main.run(Main.java:12)",
                     "]]></log4j:Throwable>",
                     "</log4j:Event>",
                 })
        {
            throwLines.AddRange(throwReader.Feed(raw));
        }
        Equal("带堆栈的事件还原出 4 行（1 头 + 3 堆栈，兜底那行空行被丢掉）",
            "4", throwLines.Count.ToString());
        Check("异常头跟日志头在一起（第一行）",
            throwLines[0].Contains("boom", StringComparison.Ordinal), throwLines[0]);
        Check("堆栈每一行都缩进了一个 tab",
            throwLines.Skip(1).All(l => l.StartsWith('\t')),
            string.Join(" | ", throwLines.Skip(1)));
        Check("堆栈第一行（异常类型那行）也在，而且被缩进了 —— 不缩进的话它会变成一条新日志",
            throwLines[1].Contains("NullPointerException", StringComparison.Ordinal)
            && throwLines[1].StartsWith('\t'),
            throwLines[1]);

        // 认不出来的东西**原样放出去**，不吞内容
        var garbage = MinecraftLogReader.Render("this is not xml at all");
        Check("认不出来的东西原样返回（不吞内容）",
            garbage.Count == 1 && garbage[0] == "this is not xml at all",
            string.Join(" | ", garbage));

        // 时间戳
        Equal("FormatTime(非数字)", "--:--:--", MinecraftLogReader.FormatTime("abc"));
        Equal("FormatTime(null)", "--:--:--", MinecraftLogReader.FormatTime(null));
        Check("FormatTime(正常值) 是 HH:MM:SS",
            MinecraftLogReader.FormatTime("1717200000000").Length == 8,
            MinecraftLogReader.FormatTime("1717200000000"));

        // 有状态的读取器：一条事件跨 3 行
        var reader = new MinecraftLogReader();
        Equal("XML 声明那行被丢掉（是布局加的壳，不是日志）",
            "0", reader.Feed("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Count.ToString());
        Equal("事件第 1 行先攒着（还不完整）", "0",
            reader.Feed("<log4j:Event logger=\"a\" timestamp=\"1717200000000\" level=\"INFO\" thread=\"main\">")
                .Count.ToString());
        Equal("事件第 2 行还是攒着", "0",
            reader.Feed("<log4j:Message>hello</log4j:Message>").Count.ToString());
        var finished = reader.Feed("</log4j:Event>");
        Check("第 3 行（收尾）才吐出可读行",
            finished.Count == 1 && finished[0].Contains("hello", StringComparison.Ordinal),
            string.Join(" | ", finished));
        // 老版本（1.11 及更早）：控制台本来就是可读文本
        var plain = new MinecraftLogReader();
        var plainLines = plain.Feed("[12:00:00] [main/INFO]: plain old text");
        Check("认不出 XML 的行原样放过去",
            plainLines.Count == 1 && plainLines[0] == "[12:00:00] [main/INFO]: plain old text",
            string.Join(" | ", plainLines));

        // 流断了但还有半条 —— 不吞
        var stuck = new MinecraftLogReader();
        stuck.Feed("<log4j:Event logger=\"a\" timestamp=\"1717200000000\" level=\"INFO\">");
        var leftover = stuck.Flush();
        Check("流断了时半条事件被原样吐出来", leftover.Count == 1, $"{leftover.Count} 行");

        // 日志总线
        LogBus.Clear();

        // ⚠️ 事件是**同步**触发的，所以订阅之后立刻写就能收到（不用等）
        var received = new List<LogEntry>();
        void OnAppended(LogEntry e) => received.Add(e);
        LogBus.Appended += OnAppended;
        try
        {
            LogBus.Write("[12:00:00] [main/INFO]: first");
            LogBus.Write("\tat some.Stack.Frame(File.java:1)");        // 续行 → 继承 INFO
            LogBus.Write("[12:00:01] [main/ERROR]: second");
            LogBus.Write("");                                          // 空行 → 不属于任何一级
            LogBus.Write("   ");                                       // 纯空白 → 也不算续行
            LogBus.Write("[12:00:02] [main/INFO]: 普通行");
        }
        finally
        {
            LogBus.Appended -= OnAppended;
        }

        Equal("收到 6 条", "6", received.Count.ToString());
        Equal("第 2 条（堆栈续行）继承了上一条的 INFO",
            LogLevel.Info.ToString(), received[1].Level.ToString());
        Check("第 2 条被标成续行", received[1].IsContinuation);
        Equal("第 3 条是 ERROR", LogLevel.Error.ToString(), received[2].Level.ToString());
        Equal("空行不属于任何一级（永远显示）",
            LogLevel.None.ToString(), received[3].Level.ToString());
        Equal("纯空白行也不算续行", LogLevel.None.ToString(), received[4].Level.ToString());
        Equal("总线里存了 6 条", "6", LogBus.Entries.Count.ToString());

        // [启动器] 前缀
        var launcherEntry = LogBus.WriteLauncher("游戏已退出（代码 0）");
        Check("WriteLauncher 会自动补 [启动器] 前缀",
            launcherEntry.Text.StartsWith(LogLine.LauncherPrefix, StringComparison.Ordinal),
            launcherEntry.Text);
        Check("WriteLauncher 记的条目标了 IsLauncher", launcherEntry.IsLauncher);

        // 导出：**全部**条目，不管窗口上藏了哪几级
        var exported = LogBus.Export();
        Check("导出里前面那几条都在（导出不过滤级别）",
            exported.Contains("first", StringComparison.Ordinal)
            && exported.Contains("second", StringComparison.Ordinal)
            && exported.Contains("普通行", StringComparison.Ordinal),
            exported.Length.ToString());
        Check("导出以换行结尾", exported.EndsWith('\n'));

        // 清空会通知订阅者
        var cleared = false;
        void OnCleared() => cleared = true;
        LogBus.Cleared += OnCleared;
        try
        {
            LogBus.Clear();
        }
        finally
        {
            LogBus.Cleared -= OnCleared;
        }
        Check("Clear 之后总线空了", LogBus.Entries.Count == 0);
        Check("Clear 通知了订阅者", cleared);
        Equal("空总线的导出是空串", "", LogBus.Export());

        // 那一排计数按钮的级别表
        Equal("可过滤的级别正好 5 个（致命/错误/警告/信息/调试）",
            "5", LogLine.Filterable.Count.ToString());
        Equal("顺序从严重到轻",
            "Fatal, Error, Warn, Info, Debug",
            string.Join(", ", LogLine.Filterable.Select(l => l.ToString())));
        Equal("级别名 FATAL", "FATAL", LogLine.Name(LogLevel.Fatal));
        Equal("级别名 ERROR", "ERROR", LogLine.Name(LogLevel.Error));
        Equal("级别名 WARN", "WARN", LogLine.Name(LogLevel.Warn));
        Equal("级别名 INFO", "INFO", LogLine.Name(LogLevel.Info));
        Equal("级别名 DEBUG", "DEBUG", LogLine.Name(LogLevel.Debug));
        Equal("None 没有级别名", "", LogLine.Name(LogLevel.None));

        // 噪音清单本身：**不许为空**（空了就等于这个功能被悄悄摘掉了）
        Check("噪音清单不为空", LogLine.NoisePatterns.Count > 0,
            string.Join(" / ", LogLine.NoisePatterns));
    }

    // 入场动画轨迹引擎

    /// <summary>跟 `ui/widgets/slide_in.py` 的**轨迹**逐点比对。</summary>
    private static void CheckSlideInTrajectory()
    {
        var goldenPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "tests", "slide_anim_golden.json"));

        if (!File.Exists(goldenPath))
        {
            Check("找得到入场动画轨迹金标准", false,
                $"缺 {goldenPath}，先生成：python D:\\deepseek\\实验\\tools\\gen_slide_golden.py");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var root = doc.RootElement;
        var goldenPresets = root.GetProperty("presets");

        foreach (var item in goldenPresets.EnumerateObject())
        {
            var key = item.Name;
            var golden = item.Value;
            var preset = PresetFromGolden(golden.GetProperty("preset"));
            var isSpring = preset.Style == AnimationStyle.Spring;

            // 总时长
            var goldenDuration = golden.GetProperty("duration_ms").GetDouble();
            Equal($"{key} · 轨迹总时长", goldenDuration.ToString("F0"),
                SlideInTrajectory.TotalDurationMs(preset).ToString("F0"));

            // 位移逐点
            var maxOffsetError = 0.0;
            var worstT = 0.0;
            var coordMismatch = 0;
            var opacityError = 0.0;
            var goldenOpacityAlwaysOne = true;
            var sampleCount = 0;
            foreach (var sample in golden.GetProperty("samples").EnumerateArray())
            {
                var t = sample.GetProperty("t").GetDouble();
                var expectedOffset = sample.GetProperty("offset").GetDouble();
                var actualOffset = SlideInTrajectory.DisplacementAt(preset, t);

                if (!isSpring)
                {
                    var error = Math.Abs(actualOffset - expectedOffset);
                    if (error > maxOffsetError) { maxOffsetError = error; worstT = t; }
                }

                // 屏幕上真的挪到哪：水平挪 x、垂直挪 y，而且 Python 那边是 int(round(...))
                // （银行家舍入）—— 所以这边也显式 ToEven，不然卡在 .5 的点上会差 1px
                var expectedCoord = preset.Direction == AnimationDirection.Vertical
                    ? sample.GetProperty("y").GetInt32()
                    : sample.GetProperty("x").GetInt32();
                var actualCoord = (int)Math.Round(
                    SlideInTrajectory.ToCoordinate(preset, actualOffset), MidpointRounding.ToEven);
                if (actualCoord != expectedCoord && !isSpring) coordMismatch++;

                var expectedOpacity = sample.GetProperty("opacity").GetDouble();
                if (Math.Abs(expectedOpacity - 1.0) > 1e-9) goldenOpacityAlwaysOne = false;
                if (preset.Style == AnimationStyle.Pcl)
                {
                    opacityError = Math.Max(opacityError,
                        Math.Abs(SlideInTrajectory.OpacityAt(preset, t, true) - expectedOpacity));
                }
                sampleCount++;
            }

            Check($"{key} · 金标准里有采样点", sampleCount > 5, $"实际 {sampleCount} 个");
            if (!isSpring)
            {
                Check($"{key} · {sampleCount} 个采样点位移全在 1e-9 以内",
                    maxOffsetError < 1e-9,
                    $"最大误差 {maxOffsetError:E3} @t={worstT}ms（Python {SlideInTrajectory.DisplacementAt(preset, worstT):R}）");
                Check($"{key} · 量化后的坐标逐点一致（银行家舍入也对得上）",
                    coordMismatch == 0, $"{coordMismatch} 个点对不上");
            }
            else
            {
                Console.WriteLine($"     （{key} 是弹簧档：整条轨迹有意不同，判据在下面那条 bump 上）");
            }

            if (preset.Style == AnimationStyle.Pcl)
            {
                Check($"{key} · 透明度（淡入）逐点一致", opacityError < 1e-9, $"最大误差 {opacityError:E3}");
            }
            else
            {
                Check($"{key} · 1.0.0 那边确实不淡入（2.0 有意改成淡入）",
                    goldenOpacityAlwaysOne, "金标准里的 opacity 不是恒 1，这条偏差的依据变了");
            }

            // 过冲峰值
            var goldenPeak = golden.GetProperty("peak_overshoot_px").GetDouble();
            var measured = AnimationRebound.Measure(preset);
            if (isSpring)
            {
                Check($"{key} · 过冲正好是 OvershootPx={preset.OvershootPx}（±0.01px）",
                    Math.Abs(measured.OvershootPx - preset.OvershootPx) <= 0.01,
                    $"实测 {measured.OvershootPx:F4}px，Python 那条 bump 的峰值 {goldenPeak:F4}px");
            }
            else
            {
                Check($"{key} · 过冲峰值跟金标准一致（±0.01px）",
                    Math.Abs(measured.OvershootPx - goldenPeak) <= 0.01,
                    $"C# {measured.OvershootPx:F4}px / Python {goldenPeak:F4}px");
            }

            // 弹簧：回弹段公式逐值
            if (golden.TryGetProperty("spring_bump", out var bump))
            {
                var bumpDuration = bump.GetProperty("duration_ms").GetDouble();
                var bumpError = 0.0;
                foreach (var sample in bump.GetProperty("samples").EnumerateArray())
                {
                    var t = sample.GetProperty("t").GetDouble();
                    bumpError = Math.Max(bumpError, Math.Abs(
                        SlideInTrajectory.SpringBump(t / bumpDuration, preset.OvershootPx)
                        - sample.GetProperty("offset").GetDouble()));
                }
                Check($"{key} · 回弹段公式（_spring() 的 on_value）逐值一致",
                    bumpError < 1e-9, $"最大误差 {bumpError:E3}");
            }
        }


        // Bounce：三段
        var bounce = PresetFromGolden(goldenPresets.GetProperty("__bounce_synthetic").GetProperty("preset"));
        var bounceTimeline = SlideInTrajectory.Timeline(bounce);
        Equal("bounce · main = int(420 × 0.72)", "302", bounceTimeline.MainMs.ToString("F0"));
        Equal("bounce · back = 420 - 302", "118", bounceTimeline.ReboundMs.ToString("F0"));
        Equal("bounce · 冲过头那段 = back // 2", "59", bounceTimeline.ReboundOutMs.ToString("F0"));
        Equal("bounce · 收回那段 = back - back // 2", "59", bounceTimeline.ReboundBackMs.ToString("F0"));
        Check("bounce · 段 1 结束处正好滑到位（= 0，不是'差不多 0'）",
            Math.Abs(SlideInTrajectory.DisplacementAt(bounce, 302)) < 1e-9,
            SlideInTrajectory.DisplacementAt(bounce, 302).ToString("R"));
        Check("bounce · 段 2 结束处正好冲过头 -overshoot（-16px）",
            Math.Abs(SlideInTrajectory.DisplacementAt(bounce, 361) + 16.0) < 1e-9,
            SlideInTrajectory.DisplacementAt(bounce, 361).ToString("R"));
        Check("bounce · 终点精确归零",
            SlideInTrajectory.DisplacementAt(bounce, 420) == 0.0,
            SlideInTrajectory.DisplacementAt(bounce, 420).ToString("R"));
        Check("bounce · 轨迹是**三段**（中间有一个回到 0 的拐点和一次过冲折返）",
            SlideInTrajectory.DisplacementAt(bounce, 200) > 0
            && SlideInTrajectory.DisplacementAt(bounce, 340) < 0
            && SlideInTrajectory.DisplacementAt(bounce, 400) < 0,
            $"t=200 {SlideInTrajectory.DisplacementAt(bounce, 200):F3}、"
            + $"t=340 {SlideInTrajectory.DisplacementAt(bounce, 340):F3}、"
            + $"t=400 {SlideInTrajectory.DisplacementAt(bounce, 400):F3}");

        // Spring：真·往返
        // ⚠️ 这一节用的是**预设表里真正在播的那一档**（不是金标准里的参数），
        // 所以数值跟着 AnimationPresets 走：这一轮调参之后是
        // 460ms / 0.6 / overshoot 18px。
        var spring = AnimationPresets.Preset("slide_left_bounce");
        var springTimeline = SlideInTrajectory.Timeline(spring);
        Equal("spring · main = int(460 × 0.6)", "276", springTimeline.MainMs.ToString("F0"));
        Equal("spring · 前段起手就是满偏移", "120", SlideInTrajectory.DisplacementAt(spring, 0).ToString("F0"));
        Check("spring · 滑到位那一刻是 0",
            Math.Abs(SlideInTrajectory.DisplacementAt(spring, springTimeline.MainMs)) < 1e-9,
            SlideInTrajectory.DisplacementAt(spring, springTimeline.MainMs).ToString("R"));

        // 方向反转：把 1ms 采样的一阶差分符号数一遍
        var reversals = 0;
        var previousSlope = 0.0;
        var previousValue = SlideInTrajectory.DisplacementAt(spring, 0);
        var springTotal = (int)Math.Ceiling(SlideInTrajectory.TotalDurationMs(spring));
        for (var ms = 1; ms <= springTotal; ms++)
        {
            var value = SlideInTrajectory.DisplacementAt(spring, ms);
            var slope = value - previousValue;
            if (previousSlope != 0 && slope != 0 && Math.Sign(slope) != Math.Sign(previousSlope)) reversals++;
            if (slope != 0) previousSlope = slope;
            previousValue = value;
        }
        Check("spring · 采样序列出现方向反转（真的来回弹了，不是一条单调曲线）",
            reversals >= 3, $"实测反转 {reversals} 次");
        // 最深处必须是"预设里写的那个过冲"（SpringBump 的 scale 就是照它归一化的）
        var springMeasured = AnimationRebound.Measure(spring);
        Check("spring · 最深那次过冲 ≈ -OvershootPx（不是'看着弹一下'）",
            Math.Abs(springMeasured.OvershootPx - spring.OvershootPx) <= 0.01,
            $"实测 {springMeasured.OvershootPx:F4}px，预设写的 {spring.OvershootPx}px，"
            + $"峰值在 {springMeasured.PeakTimeMs:F0}ms");

        // PCL：两条位移叠加 + 淡入
        var pcl = AnimationPresets.Preset("pcl_recipe");
        var pclTimeline = SlideInTrajectory.Timeline(pcl);
        Equal("pcl · fast_ms = max(60, int(300 × 0.71))", "213", pclTimeline.FastMs.ToString("F0"));
        Equal("pcl · slow_ms = max(fast + 20, 300)", "300", pclTimeline.SlowMs.ToString("F0"));
        Equal("pcl · fade_ms = max(60, min(100, 213))", "100", pclTimeline.FadeMs.ToString("F0"));

        // 手算两条轨道再相加：A 走 31% 位移（OutQuint）、B 走 69%（PCL 旧版 back）
        var a0 = pcl.OffsetPx * SlideInTrajectory.PclFastShare;
        var b0 = pcl.OffsetPx * SlideInTrajectory.PclSlowShare;
        var trackError = 0.0;
        for (var ms = 0; ms <= 300; ms++)
        {
            var trackA = ms >= pclTimeline.FastMs
                ? 0.0
                : a0 * (1.0 - AnimationCurves.PowerOut(ms / pclTimeline.FastMs, 5.0));
            var trackB = ms >= pclTimeline.SlowMs
                ? 0.0
                : b0 * (1.0 - AnimationCurves.PclBackOut(ms / pclTimeline.SlowMs, 2.0));
            trackError = Math.Max(trackError, Math.Abs(trackA + trackB - SlideInTrajectory.DisplacementAt(pcl, ms)));
        }
        Check("pcl · 总位移 == 位移A + 位移B（两条并行轨道相加）", trackError < 1e-12,
            $"最大误差 {trackError:E3}");
        Check("pcl · 两条轨道**各自的收尾时刻不同**（A 在 213ms 就停了，B 一直跑到 300ms）",
            Math.Abs(SlideInTrajectory.DisplacementAt(pcl, 250)
                     - (b0 * (1.0 - AnimationCurves.PclBackOut(250 / 300.0, 2.0)))) < 1e-12,
            $"t=250ms 处 {SlideInTrajectory.DisplacementAt(pcl, 250):F6}");
        var pclFadeDoneMs = pclTimeline.FadeMs + 1.0;
        Check("pcl · 淡入在慢轨走完之前就结束了（淡完时还在动）",
            pclFadeDoneMs < pclTimeline.SlowMs
            && SlideInTrajectory.OpacityAt(pcl, pclFadeDoneMs, true) >= 1.0 - 1e-9
            && SlideInTrajectory.DisplacementAt(pcl, pclFadeDoneMs) > 0.05,
            $"t={pclFadeDoneMs:F0}ms 处 透明度 {SlideInTrajectory.OpacityAt(pcl, pclFadeDoneMs, true):R}、"
            + $"位移 {SlideInTrajectory.DisplacementAt(pcl, pclFadeDoneMs):F3}px"
            + $"（慢轨 {pclTimeline.SlowMs:F0}ms、位移共 {pcl.OffsetPx}px）");

        // 「先快后慢」是这一档的灵魂（PCL 的配方就是 A 快轨 + B 慢轨），
        // 也正好是它跟"单条缓动曲线"那几档的分界。用**等长两半各走多少位移**来量：
        // 前半程必须走掉明显更多（判据 ≥65%）。
        var pclHalf = pclTimeline.SlowMs / 2.0;
        var firstHalf = pcl.OffsetPx - SlideInTrajectory.DisplacementAt(pcl, pclHalf);
        Check("pcl · 前半程走的位移 ≥65%（两条轨道的「先快后慢」）",
            firstHalf >= pcl.OffsetPx * 0.65,
            $"前半程 {firstHalf:F2}px / 共 {pcl.OffsetPx}px = {firstHalf / pcl.OffsetPx * 100:F0}%");
        Check("pcl · 淡入是**从 0 开始**的（不是从 1 开始）",
            SlideInTrajectory.OpacityAt(pcl, 0, true) == 0.0,
            SlideInTrajectory.OpacityAt(pcl, 0, true).ToString("R"));
        Check("pcl · 关掉淡入时透明度恒为 1（不再挂透明动画）",
            SlideInTrajectory.OpacityAt(pcl, 0, false) == 1.0
            && SlideInTrajectory.OpacityAt(pcl, 175, false) == 1.0);

        // 关键帧（WPF 那边真正会播的数据）
        foreach (var candidate in new[] { bounce, spring, pcl })
        {
            var times = SlideInTrajectory.KeyTimes(candidate);
            var monotonic = true;
            for (var i = 1; i < times.Count; i++)
            {
                if (times[i] <= times[i - 1]) monotonic = false;
            }
            Check($"{candidate.Key} · 关键帧时刻严格递增（{times.Count} 个）", monotonic);
            Check($"{candidate.Key} · 第一个关键帧在 0ms", times[0] == 0.0, times[0].ToString("R"));
            Check($"{candidate.Key} · 最后一个关键帧在总时长处",
                Math.Abs(times[^1] - SlideInTrajectory.TotalDurationMs(candidate)) < 1e-9,
                $"{times[^1]:R} vs {SlideInTrajectory.TotalDurationMs(candidate):R}");

            // 关键帧之间是线性插值，折线与解析轨迹的最大误差判据为 0.06px。
            // ⚠️ 误差下界来自速度断点的几何性质，采样再密也消不掉；不要为此改算法。
            var reconstructionError = 0.0;
            for (var ms = 0.0; ms <= SlideInTrajectory.TotalDurationMs(candidate); ms += 0.5)
            {
                var index = 0;
                while (index + 1 < times.Count && times[index + 1] < ms) index++;
                var a = times[index];
                var b = index + 1 < times.Count ? times[index + 1] : a;
                var span = b - a;
                var interpolated = span <= 0
                    ? SlideInTrajectory.DisplacementAt(candidate, a)
                    : SlideInTrajectory.DisplacementAt(candidate, a)
                      + (SlideInTrajectory.DisplacementAt(candidate, b)
                         - SlideInTrajectory.DisplacementAt(candidate, a)) * ((ms - a) / span);
                reconstructionError = Math.Max(reconstructionError,
                    Math.Abs(interpolated - SlideInTrajectory.DisplacementAt(candidate, ms)));
            }
            Check($"{candidate.Key} · 关键帧线性重建的误差 < 0.06px（8ms 网格 + 自适应细分够用）",
                reconstructionError < 0.06, $"实测最大 {reconstructionError:F4}px");
        }

        // 总开关：关掉 = 不建计划
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "anim");
        Directory.CreateDirectory(scratch);
        var animSettings = AppSettings.Load(scratch);
        animSettings.Set("anim_enabled", false);
        Check("总开关关掉时 BuildPlan 返回 null（不是'时长为 0'）",
            PageAnimator.BuildPlan(animSettings) is null);
        animSettings.Set("anim_enabled", true);
        var enabledPlan = PageAnimator.BuildPlan(animSettings);
        Check("总开关打开时 BuildPlan 建得出计划", enabledPlan is not null);
        Check("默认那档（slide_left）走的是单条缓动曲线（最快的一条路）",
            enabledPlan?.Slide is DoubleAnimation && enabledPlan.KeyFramed == false,
            enabledPlan?.Slide.GetType().Name ?? "(null)");

        // 三种"轨迹形态"必须走关键帧，Simple 那档才用单条曲线
        foreach (var (style, expectedKeyFramed) in new (AnimationStyle, bool)[]
                 {
                     (AnimationStyle.Simple, false),
                     (AnimationStyle.Bounce, true),
                     (AnimationStyle.Spring, true),
                     (AnimationStyle.Pcl, true),
                 })
        {
            var probePreset = AnimationPresets.Preset("slide_left") with
            {
                Key = $"probe_{style}",
                Style = style,
                OvershootPx = 20,
            };
            var plan = PageAnimator.BuildPlan(probePreset, fade: true);
            Check($"{style} 形态的位移动画 {(expectedKeyFramed ? "是关键帧" : "是单条缓动曲线")}",
                plan.KeyFramed == expectedKeyFramed,
                $"实际 {plan.Slide.GetType().Name}");
            Check($"{style} 形态的起手位移 = {SlideInTrajectory.ToCoordinate(probePreset, probePreset.OffsetPx)}"
                  + "（水平取负 / 垂直取正）",
                plan.StartOffset == SlideInTrajectory.ToCoordinate(probePreset, probePreset.OffsetPx),
                plan.StartOffset.ToString("R"));
        }

        // 淡入关掉时**一个透明动画都不建**
        var noFade = PageAnimator.BuildPlan(AnimationPresets.Preset("slide_left"), fade: false);
        Check("淡入关掉时不建透明动画", noFade.FadeAnimation is null);

        // 回弹判断（对话框那句话的判据）
        var expectedRebound = new (string Key, bool HasRebound)[]
        {
            ("slide_left", false), ("slide_up", false), ("slide_left_bounce", true),
            ("pcl_like", false), ("slide_up_elastic", true), ("slide_left_back", true),
            ("pcl_recipe", true),
        };
        foreach (var (key, expected) in expectedRebound)
        {
            var measured = AnimationRebound.Measure(AnimationPresets.Preset(key));
            Equal($"{key} · 有没有回弹", expected ? "有回弹" : "无回弹",
                measured.HasRebound ? "有回弹" : "无回弹");
        }

        // 缓动曲线：工厂造出来的对象 == Core 的公式
        // （轨迹引擎直接用 Core 的公式，WPF 用工厂造的对象 —— 两条路必须是同一份数）
        foreach (var preset in AnimationPresets.Presets.Values)
        {
            var easing = EasingFactory.Create(preset.Ease);
            var error = 0.0;
            for (var i = 0; i <= 100; i++)
            {
                var t = i / 100.0;
                error = Math.Max(error, Math.Abs(easing.Ease(t) - AnimationCurves.Evaluate(preset.Ease, t)));
            }
            Check($"{preset.Key} · 工厂造出来的「{preset.Ease}」跟 Core 的公式逐点一致",
                error < 1e-12, $"最大 {error:E3}");
        }

        foreach (var (ease, reference, label) in new (string, Func<double, double>, string)[]
                 {
                     ("pcl_back:2.0", t => AnimationCurves.PclBackOut(t, 2.0), "pcl_back_out(power=2)"),
                     ("pcl_elastic:2.0", t => AnimationCurves.PclElasticOut(t, 2.0), "pcl_elastic_out(power=2)"),
                     ("power:5", t => AnimationCurves.PowerOut(t, 5.0), "power_out(power=5) = Qt OutQuint"),
                 })
        {
            var easing = EasingFactory.Create(ease);
            var error = 0.0;
            for (var i = 0; i <= 100; i++)
            {
                error = Math.Max(error, Math.Abs(easing.Ease(i / 100.0) - reference(i / 100.0)));
            }
            Check($"新增曲线「{ease}」（{label}）逐点一致", error < 1e-12, $"最大 {error:E3}");
        }

        // 认不出来 / 参数写坏了一律退回 OutCubic
        // ⚠️ 这两条是**跟着 Python 改的**：`_ease_curve("back:")` 在那边返回 OutCubic
        // （头名变成带冒号的整串，一个分支都匹配不上），"BACK:0.18" 同理（精确匹配）。
        // 改之前 C# 这边会把它们当成 back 用默认参数处理 —— 两边行为是分叉的。
        Check("「back:」参数写坏 → 退回 OutCubic（跟 Python 一致）",
            EasingFactory.Create("back:") is CubicEase { EasingMode: EasingMode.EaseOut },
            EasingFactory.Create("back:").GetType().Name);
        Check("「BACK:0.18」大小写不宽容 → 退回 OutCubic（跟 Python 一致）",
            EasingFactory.Create("BACK:0.18") is CubicEase { EasingMode: EasingMode.EaseOut },
            EasingFactory.Create("BACK:0.18").GetType().Name);
        Check("「back」不带参数 = 过冲 0.10（跟 Python 的 named(\"back_out\", 0.10) 一致）",
            EasingFactory.Create("back") is ParametricBackEase { OvershootRatio: 0.10 });
        Check("「spring」不带参数 = 过冲 0.12（跟 Python 的 named(\"spring\") 默认值一致）",
            EasingFactory.Create("spring") is ParametricDampedSineEase { OvershootRatio: 0.12 });
    }

    /// <summary>金标准里那段 `preset` → Core 的 AnimationPreset（字段一一对应）。</summary>
    private static AnimationPreset PresetFromGolden(JsonElement preset) => new(
        Key: preset.GetProperty("key").GetString() ?? "?",
        Name: preset.GetProperty("key").GetString() ?? "?",
        Description: string.Empty,
        Direction: preset.GetProperty("direction").GetString() == "v"
            ? AnimationDirection.Vertical
            : AnimationDirection.Horizontal,
        DurationMs: preset.GetProperty("duration").GetInt32(),
        OffsetPx: preset.GetProperty("offset").GetInt32(),
        IntervalMs: preset.GetProperty("interval").GetInt32(),
        Style: preset.GetProperty("style").GetString() switch
        {
            "bounce" => AnimationStyle.Bounce,
            "spring" => AnimationStyle.Spring,
            "pcl" => AnimationStyle.Pcl,
            _ => AnimationStyle.Simple,
        },
        OvershootPx: preset.GetProperty("overshoot").GetInt32(),
        BounceRatio: preset.GetProperty("bounce_ratio").GetDouble(),
        Ease: preset.GetProperty("ease").GetString() ?? "cubic");

    // 服务桩

    private static void CheckStubs()
    {
        var services = LauncherServices.Prototype();
        Check("原型服务全都标了 IsPrototype", services.AnyPrototype);

        var versions = services.Games.Installed;
        Check("假数据里有版本", versions.Count > 0, $"{versions.Count} 个");
        Check("假数据里有 1.20.5（跟截图对齐）", versions.Any(v => v.Id == "1.20.5"));

        var javas = services.Java.Detected;
        Check("假数据里有 10 个 Java（跟截图那句提示对齐）", javas.Count == 10, $"{javas.Count} 个");
        Check("有一个是默认的", javas.Count(j => j.IsDefault) == 1);
        Equal("1.20.5 挑到 Java 21", "21",
            services.Java.PickFor(versions.First(v => v.Id == "1.20.5"))?.Version ?? "null");
        Equal("1.12.2 挑到 Java 8", "8",
            services.Java.PickFor(versions.First(v => v.Id == "1.12.2-forge"))?.Version ?? "null");
        // 1.20.4 和 1.20.5 相邻但要求不同，正是最容易被"只看次版本号"写错的地方
        foreach (var (mc, want) in new (string, string)[]
                 {
                     ("1.16.5", "8"), ("1.17", "17"), ("1.20.4", "17"),
                     ("1.20.5", "21"), ("1.21.4", "21"), ("1.7.10", "8"),
                 })
        {
            Equal($"Minecraft {mc} 要 Java {want}", want, StubJavaService.WantedMajor(mc));
        }

        Check("一开始没有档案（启动页要显示「未选择档案」）", services.Accounts.Current is null);
        var account = services.Accounts.AddOffline("BaBaLe");
        Check("新建离线档案后成为当前档案",
            services.Accounts.Current?.Name == "BaBaLe" && account.Kind == AccountKind.Offline);

        // 启动必须**明确失败**并说明原因，不能假装成功
        var result = services.Launch
            .LaunchAsync(new LaunchRequest(versions[0], account, javas[0], 2048, "D:\\mc"))
            .GetAwaiter().GetResult();
        Check("假启动器明确返回失败", !result.Ok, result.Message);
        Check("失败信息里说清是原型", result.Message.Contains("原型"), result.Message);
    }

    // 启动引擎金标准

    /// <summary>启动计划金标准的一个 case；⚠️ 用命名 record，别用嵌套元组。</summary>
    private sealed record PlanCase(
        string Golden,
        string VersionId,
        string Loader = "",
        bool? Isolated = null,
        string VersionType = "",
        string Server = "",
        string ExtraJvm = "",
        string ExtraGame = "",
        string Locale = "zh_CN");

    private static void CheckLaunchEngine(string themeGoldenPath)
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(themeGoldenPath))!;
        var goldenPath = Path.Combine(testsDir, "launch_golden.json");
        var mcDir = Path.Combine(testsDir, "fixtures", "mc");

        if (!File.Exists(goldenPath) || !Directory.Exists(mcDir))
        {
            Check("启动引擎金标准文件在", false,
                $"找不到 {goldenPath} —— 先生成：python tools\\gen_launch_golden.py");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var golden = doc.RootElement;

        // 纯函数
        foreach (var (name, want) in golden.GetProperty("offline_uuid").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"离线 UUID（{Describe(name)}）", want, LaunchRules.OfflineUuid(name));
        }

        foreach (var (name, want) in golden.GetProperty("maven").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var expectedPath = want[0].ValueKind == JsonValueKind.Null ? null : want[0].GetString();
            var expectedDir = want[1].ValueKind == JsonValueKind.Null ? null : want[1].GetString();
            var (path, dir) = LaunchRules.MavenToPath(name);
            Equal($"Maven 路径（{name}）", expectedPath ?? "<null>", path ?? "<null>");
            Equal($"Maven 目录（{name}）", expectedDir ?? "<null>", dir ?? "<null>");
        }

        // rules：**最后一条匹配生效** + features 必须显式判断
        foreach (var (case_, want) in golden.GetProperty("rules").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            JsonNode? rules = case_ switch
            {
                "empty" => new JsonArray(),
                "windows" => ParseNode("""[{"action":"allow","os":{"name":"windows"}}]"""),
                "last_wins" => ParseNode("""[{"action":"allow"},{"action":"disallow","os":{"name":"windows"}}]"""),
                "osx" => ParseNode("""[{"action":"allow","os":{"name":"osx"}}]"""),
                "feature_on" => ParseNode("""[{"action":"allow","features":{"has_custom_resolution":true}}]"""),
                "feature_off" => ParseNode("""[{"action":"allow","features":{"is_demo_user":true}}]"""),
                _ => null,
            };
            Check($"rules.{case_} → {want}", LaunchRules.RulesAllow(rules) == want,
                $"期望 {want}，实际 {!want}");
        }

        // 继承链合并
        var fabric = VersionJsonLoader.Load(mcDir, "1.20.1-fabric");
        var merged = golden.GetProperty("merged_fabric");
        Equal("合并后 id 用子版本的", merged.GetProperty("id").GetString()!, fabric.Id);
        Equal("合并后 mainClass 用子版本的", merged.GetProperty("mainClass").GetString()!, fabric.MainClass);
        // ⚠️ 子版本**没写** jar 字段 → 必须回退到父版本（这里父版本 id 就是 jar 的目录名）。
        // 这就是"Fabric 报 couldn't locate the game"那个坑。
        Equal("Fabric 没写 jar → 用父版本的", merged.GetProperty("jar").GetString()!, fabric.Jar);
        Equal("合并后 type 继承父版本", merged.GetProperty("type").GetString()!, fabric.Type);
        Equal("合并后 javaVersion 继承父版本",
            merged.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32().ToString(),
            fabric.JavaMajor.ToString());
        Equal("合并后 assetIndex 继承父版本",
            merged.GetProperty("assetIndex").GetString()!, fabric.AssetIndexId ?? "<null>");
        Equal("合并后库列表顺序（父在前、子在后）",
            string.Join("|", merged.GetProperty("library_names").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", fabric.Libraries.OfType<JsonObject>()
                .Select(l => l["name"]?.GetValue<string>() ?? "")));
        Equal("合并后 jvm 参数两边拼接",
            merged.GetProperty("jvm_arg_count").GetInt32().ToString(),
            fabric.JvmArguments.Count.ToString());

        // ⚠️ 去重键必须是 `group:artifact:classifier`（不含版本），否则 ASM 9.6 / 9.11 会同时进 classpath 导致崩溃；classifier 不同的 natives 都要保留。
        var dedupeDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "libdedupe");
        var dedupeParent = Path.Combine(dedupeDir, "versions", "1.21.6");
        var dedupeChild = Path.Combine(dedupeDir, "versions", "1.21.6-fabric");
        try
        {
            Directory.CreateDirectory(dedupeParent);
            Directory.CreateDirectory(dedupeChild);
            File.WriteAllText(Path.Combine(dedupeParent, "1.21.6.json"),
                """{"id":"1.21.6","mainClass":"net.minecraft.client.main.Main","libraries":["""
                + """{"name":"org.ow2.asm:asm:9.6","downloads":{"artifact":{"path":"org/ow2/asm/asm/9.6/asm-9.6.jar","url":"https://libraries.minecraft.net/org/ow2/asm/asm/9.6/asm-9.6.jar"}}},"""
                + """{"name":"org.lwjgl:lwjgl:3.3.3:natives-windows"}]}""");
            File.WriteAllText(Path.Combine(dedupeChild, "1.21.6-fabric.json"),
                """{"id":"1.21.6-fabric","inheritsFrom":"1.21.6","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":["""
                + """{"name":"org.ow2.asm:asm:9.11","url":"https://maven.fabricmc.net/"},"""
                + """{"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-arm64"}]}""");

            var deduped = VersionJsonLoader.Load(dedupeDir, "1.21.6-fabric");
            var dedupedNames = deduped.Libraries.OfType<JsonObject>()
                .Select(l => l["name"]?.GetValue<string>() ?? "")
                .ToList();
            Equal("同 artifact 不同版本只留子版本那条（位置不变）",
                "org.ow2.asm:asm:9.11|org.lwjgl:lwjgl:3.3.3:natives-windows|"
                + "org.lwjgl:lwjgl:3.3.3:natives-windows-arm64",
                string.Join("|", dedupedNames));
            Check("父版本的 asm 9.6 被顶掉了", !dedupedNames.Contains("org.ow2.asm:asm:9.6"));
        }
        finally
        {
            try
            {
                Directory.Delete(dedupeDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无所谓
            }
        }

        // ⚠️ OptiFine：JSON 没写 jar，但自己目录里躺着 <id>.jar → **自己目录那个优先**。
        // 退回父版本的原版 jar 等于 OptiFine 白装了，而且一声不响。
        Equal("OptiFine 自己目录里有 jar → 用它",
            golden.GetProperty("merged_optifine_jar").GetString()!,
            VersionJsonLoader.Load(mcDir, "1.20.1-OptiFine_HD_U_I6").Jar);
        // ⚠️ 反过来：JSON **显式写了** jar（Forge 1.16- 的写法）就不能被顶掉
        Equal("Forge 显式写的 jar 不能被顶掉",
            golden.GetProperty("merged_forge_jar").GetString()!,
            VersionJsonLoader.Load(mcDir, "1.16.5-forge-36.2.34").Jar);

        // 启动计划
        // 每个 case 对应 gen_launch_golden.py 里 plan_for(...) 的一种参数组合
        var planCases = new PlanCase[]
        {
            new("plan_vanilla", "1.20.1"),
            new("plan_fabric", "1.20.1-fabric", Loader: "Fabric", Isolated: true),
            new("plan_custom_info", "1.20.1", VersionType: "MyLauncher/Test"),
            new("plan_server", "1.20.1", Server: "mc.example.com:25566"),
            new("plan_extra", "1.20.1", ExtraJvm: "-XX:+UseG1GC", ExtraGame: "--width 1920"),
            new("plan_locale", "1.20.1"),
        };

        foreach (var c in planCases)
        {
            var case_ = c.Golden;
            var planGolden = golden.GetProperty(case_);
            var versionId = c.VersionId;

            var plan = LaunchPlanBuilder.Build(new LaunchPlanRequest
            {
                MinecraftDir = mcDir,
                VersionId = versionId,
                VersionDir = Path.Combine(mcDir, "versions", versionId),
                JavaPath = @"C:\java\bin\javaw.exe",
                Username = "BaBaLe",
                MinMemoryMb = 512,
                MaxMemoryMb = 4096,
                Width = 1280,
                Height = 720,
                LauncherName = "Mosslight",
                LauncherVersion = "v2.0.0-alpha",
                Locale = c.Locale,
                VersionType = c.VersionType,
                ServerAddress = c.Server,
                ExtraJvmArgs = c.ExtraJvm,
                ExtraGameArgs = c.ExtraGame,
                LoaderLabel = c.Loader,
                Isolated = c.Isolated,
            });

            Equal($"{case_} 的 version_type 取值",
                planGolden.GetProperty("version_type").GetString()!, plan.VersionType);

            Equal($"{case_} 的工作目录 = 版本目录",
                planGolden.GetProperty("cwd_is_version_dir").GetBoolean().ToString(),
                (Path.GetFullPath(plan.WorkingDirectory)
                 == Path.GetFullPath(Path.Combine(mcDir, "versions", versionId))).ToString());

            Equal($"{case_} 的 natives 目录",
                planGolden.GetProperty("natives_dir").GetString()!,
                Normalize(plan.NativesDir, mcDir));

            Equal($"{case_} 要不要解压 natives",
                planGolden.GetProperty("needs_natives").GetBoolean().ToString(),
                plan.NeedsNatives.ToString());

            Equal($"{case_} 的 classpath",
                string.Join("|", planGolden.GetProperty("classpath").EnumerateArray()
                    .Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal)),
                string.Join("|", plan.Classpath.Select(p => Normalize(p, mcDir))
                    .OrderBy(x => x, StringComparer.Ordinal)));

            // 命令行逐项比 —— 这是整份金标准里最值钱的一条
            var wantArgs = planGolden.GetProperty("args").EnumerateArray()
                .Select(x => x.GetString()!).ToList();
            var gotArgs = plan.Args.Select(a => Normalize(a, mcDir)).ToList();

            if (wantArgs.Count != gotArgs.Count)
            {
                // 个数不一样时也要说清**差在哪儿** —— 只报两个数字的话，
                // 得自己去翻 python 输出对照，很费劲
                var extra = wantArgs.Except(gotArgs).ToList();
                var missing = gotArgs.Except(wantArgs).ToList();
                var detail = $"参数个数 期望 {wantArgs.Count}，实际 {gotArgs.Count}";
                if (extra.Count > 0)
                {
                    detail += $"；少了：{string.Join(" / ", extra)}";
                }
                if (missing.Count > 0)
                {
                    detail += $"；多了：{string.Join(" / ", missing)}";
                }
                Check($"{case_} 的命令行", false, detail);
            }
            else
            {
                var firstDiff = -1;
                for (var i = 0; i < wantArgs.Count; i++)
                {
                    if (wantArgs[i] != gotArgs[i])
                    {
                        firstDiff = i;
                        break;
                    }
                }
                Check($"{case_} 的命令行（{wantArgs.Count} 项逐项相同）", firstDiff < 0,
                    firstDiff < 0 ? null
                        : $"第 {firstDiff} 项 期望 {wantArgs[firstDiff]}，实际 {gotArgs[firstDiff]}");
            }

            Equal($"{case_} 缺失的库",
                string.Join("|", planGolden.GetProperty("missing_libraries").EnumerateArray()
                    .Select(x => x.GetString()!)),
                string.Join("|", plan.MissingLibraries));
        }

        // 扫描
        var scanner = new VersionScanner(mcDir);
        var scanned = scanner.Scan();
        var scanGolden = golden.GetProperty("scan");

        Equal("扫描顺序（官方 → 加载器；同段内新的在前）",
            string.Join("|", scanGolden.GetProperty("order").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", scanned.Select(v => v.Version.Id)));

        foreach (var (id, want) in scanGolden.GetProperty("kinds").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var got = scanned.First(v => v.Version.Id == id).Version.Kind switch
            {
                VersionKind.Vanilla => "vanilla",
                VersionKind.Loader => "loader",
                _ => "pack",
            };
            Equal($"「{id}」的分类", want, got);
        }

        foreach (var (id, want) in scanGolden.GetProperty("loaders").EnumerateObject()
                     .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString())))
        {
            var got = scanned.First(v => v.Version.Id == id).Version.LoaderKey;
            Equal($"「{id}」的加载器 key", want ?? "<null>", got ?? "<null>");
        }

        // ⚠️ 品牌名的大小写单独比一遍：`NeoForge` / `OptiFine` 被写成
        // `Neoforge` / `Optifine` 是编译能过、看着也不刺眼的那种错，
        // 只有逐字比才抓得住
        foreach (var (id, want) in scanGolden.GetProperty("loader_labels").EnumerateObject()
                     .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString())))
        {
            var got = scanned.First(v => v.Version.Id == id).Version.Loader;
            Equal($"「{id}」的加载器显示名", want ?? "Vanilla", got);
        }

        foreach (var (id, want) in scanGolden.GetProperty("isolated").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            Equal($"「{id}」的版本隔离", want.ToString(),
                scanned.First(v => v.Version.Id == id).Version.Isolated.ToString());
        }

        foreach (var (id, want) in scanGolden.GetProperty("complete").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            Equal($"「{id}」能不能启动", want.ToString(),
                scanned.First(v => v.Version.Id == id).Version.Complete.ToString());
        }

        // ⚠️ 坏 json 只记 errors，不能拖垮扫描；没有 id 的目录静默跳过。
        Equal("扫描告警条数（坏 json 记 1 条、非版本目录不记）",
            scanGolden.GetProperty("errors_count").GetInt32().ToString(),
            scanner.Errors.Count.ToString());
        Check("坏版本没进列表", scanned.All(v => v.Version.Id != "broken"));
        Check("非版本目录被静默跳过", scanned.All(v => v.Version.Id != "native-libraries"));

        // ⚠️ 带 UTF-8 BOM 的版本 JSON 必须能读：JsonNode.Parse 不认 BOM，否则版本会从列表消失。
        Check("带 BOM 的版本 JSON 也认得出来（不是从列表里消失）",
            scanned.Any(v => v.Version.Id == "1.20.1-bom"),
            "扫到的：" + string.Join("、", scanned.Select(v => v.Version.Id)));
        Equal("带 BOM 的那个版本算完整的（有客户端 jar）", "True",
            (scanned.FirstOrDefault(v => v.Version.Id == "1.20.1-bom")?.Version.Complete
             ?? false).ToString());

        // 顺带把 JsonIO 本身也点一下名：它是"所有读 JSON 的地方"的唯一入口，
        // 以后谁绕开它直接 JsonNode.Parse(bytes)，这条会红
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("""{"a":1}""")).ToArray();
        Check("JsonIO 直接吃带 BOM 的字节",
            JsonIO.ParseBytes(bomBytes)?["a"]?.GetValue<int>() == 1);
        Check("JsonIO 对只有 BOM 的空文件返回 null 而不是抛异常",
            JsonIO.ParseBytes(new byte[] { 0xEF, 0xBB, 0xBF }) is null);
    }

    private static string Normalize(string text, string mcDir)
    {
        // ⚠️ 两个分隔符都要换：Python 那边是
        // `text.replace(prefix + "\\", "").replace(prefix + "/", "")`，
        // 路径中间混着 `\`（Path 拼出来的）和 `/`（JSON 里写的），
        // 只换一种会漏掉一半。
        return text
            .Replace(mcDir + "\\", "<MC>/", StringComparison.OrdinalIgnoreCase)
            .Replace(mcDir + "/", "<MC>/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ParseNode(string json) => JsonNode.Parse(json);

    // 真跑一次 Forge 安装器（--net-forge）

    /// <summary>把 LiteLoader **真装一遍**、把 Cleanroom 的安装器**真取一遍**；⚠️ 原版只放一个**最小桩**（真装 1.8 要 200 MB，自检不能那么重）：LiteLoader 那条路只要原版的 `minecraftArguments`，桩里有它就够。</summary>
    private static async Task CheckRealJsonBuildLoadersAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "json-build-loaders");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }

        var baseDir = Path.Combine(scratch, "base");
        var mcDir = Path.Combine(scratch, "mc");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(mcDir, "versions", "1.8"));

        // 原版最小桩：LiteLoader 只要 minecraftArguments 那一串
        File.WriteAllText(Path.Combine(mcDir, "versions", "1.8", "1.8.json"), """
            {"id":"1.8","type":"release",
             "mainClass":"net.minecraft.client.main.Main",
             "minecraftArguments":"--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name}",
             "libraries":[],"assetIndex":{"id":"1.8"},"assets":"1.8"}
            """);

        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        try
        {
            // 1. LiteLoader 1.8：真装
            var llRow = (await LoaderCatalog.LoaderRowsAsync("1.8")).First(r => r.Key == "liteloader");
            if (llRow.Latest.Length == 0)
            {
                Check("（跳过）真装 LiteLoader：拉不到版本列表", true, "网络问题");
            }
            else
            {
                var llResult = await services.Loaders.InstallAsync(new LoaderInstallRequest(
                    MinecraftVersion: "1.8",
                    LoaderKey: "liteloader",
                    LoaderVersion: llRow.Latest,
                    MinecraftDir: mcDir));

                Check($"LiteLoader {llRow.Latest} 真装成功：{llResult.Message}", llResult.Ok, llResult.Message);

                var llId = llResult.VersionId ?? "1.8-LiteLoader_1.8";
                var llJsonPath = Path.Combine(mcDir, "versions", llId, llId + ".json");
                Check("LiteLoader 的版本 JSON 落盘了（文件名 = 目录名 = id）",
                    File.Exists(llJsonPath), llJsonPath);

                var written = File.Exists(llJsonPath) ? JsonNode.Parse(File.ReadAllText(llJsonPath)) : null;
                Check("落盘的 JSON：inheritsFrom 指原版、mainClass 是 launchwrapper、带 --tweakClass",
                    written is JsonObject w
                    && w["inheritsFrom"]?.GetValue<string>() == "1.8"
                    && w["mainClass"]?.GetValue<string>() == "net.minecraft.launchwrapper.Launch"
                    && (w["minecraftArguments"]?.GetValue<string>() ?? string.Empty)
                        .Contains("--tweakClass", StringComparison.Ordinal),
                    written?["mainClass"]?.GetValue<string>() ?? "（没有 JSON）");

                // 三条库要真落到 libraries/ 里，而且**路径要跟启动时找的一致**
                var llLibraries = (written?["libraries"] as JsonArray)?.OfType<JsonObject>().ToList()
                                  ?? new List<JsonObject>();
                var missingFiles = new List<string>();
                foreach (var lib in llLibraries)
                {
                    var artifactPath = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
                    var (rel, _) = LaunchRules.MavenToPath(lib["name"]?.GetValue<string>() ?? string.Empty);
                    var relative = artifactPath ?? rel;
                    if (relative is null)
                    {
                        continue;
                    }
                    var onDisk = Path.Combine(mcDir, "libraries",
                        relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(onDisk))
                    {
                        missingFiles.Add(Path.GetFileName(relative));
                    }
                }
                Check($"LiteLoader 的 {llLibraries.Count} 条库都下到了 libraries/ 里",
                    missingFiles.Count == 0, string.Join("、", missingFiles));

                // 最大的那个是 LiteLoader 自己（1.8 那份 666 KB），不是错误页
                var liteloaderJar = Directory
                    .EnumerateFiles(Path.Combine(mcDir, "libraries"), "liteloader-*.jar",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
                Check("LiteLoader 自己的 jar 下下来了，而且不是个错误页",
                    liteloaderJar is not null && new FileInfo(liteloaderJar).Length > 100_000,
                    liteloaderJar is null
                        ? "没找到"
                        : $"{Path.GetFileName(liteloaderJar)}：{new FileInfo(liteloaderJar).Length} 字节");
            }

            // 2. Cleanroom：真取安装器 → 抽 JSON → 补地址 → 规划
            var crRow = (await LoaderCatalog.LoaderRowsAsync("1.12.2")).First(r => r.Key == "cleanroom");
            if (crRow.Latest.Length == 0)
            {
                Check("（跳过）Cleanroom：拉不到版本列表", true, "网络问题");
            }
            else
            {
                var installerPath = Path.Combine(scratch, $"cleanroom-{crRow.Latest}-installer.jar");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(LoaderCatalog.UserAgent);
                await using (var stream = await http.GetStreamAsync(
                                 CleanroomCatalog.InstallerUrl(crRow.Latest)))
                {
                    await using var file = File.Create(installerPath);
                    await stream.CopyToAsync(file);
                }

                Check($"Cleanroom {crRow.Latest} 的安装器下下来了（{DownloadManager.HumanSize(new FileInfo(installerPath).Length)}）",
                    new FileInfo(installerPath).Length > 1_000_000);

                var vj = CleanroomCatalog.ReadVersionJson(installerPath);
                Equal("Cleanroom：安装器里那份 version.json 的 mainClass",
                    "top.outlands.foundation.boot.Foundation",
                    vj["mainClass"]?.GetValue<string>() ?? "");

                var fixedCount = CleanroomCatalog.PrepareVersionJson(
                    vj, crRow.Latest, "1.12.2-Cleanroom_" + crRow.Latest);
                Check($"Cleanroom：真实这份 JSON 里补上了 {fixedCount} 处空地址", fixedCount > 0);

                var cleanroomLib = (vj["libraries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(l =>
                    (l["name"]?.GetValue<string>() ?? string.Empty)
                    .StartsWith("com.cleanroommc:cleanroom:", StringComparison.Ordinal));
                Check("Cleanroom：universal jar 的地址指向 maven（不是空串）",
                    (cleanroomLib?["downloads"]?["artifact"]?["url"]?.GetValue<string>() ?? string.Empty)
                    .EndsWith("-universal.jar", StringComparison.Ordinal),
                    cleanroomLib?["downloads"]?["artifact"]?["url"]?.GetValue<string>() ?? "（空）");

                // 规划出来要有客户端 jar + 一百多个库 + 资源
                // （库的总数是 217 条，其中 natives 那几十条单独进 `plan.Natives`，不算在里面，
                // 所以别拿 217 当期望值 —— 那会永远差几十个）
                var plan = GameInstaller.PlanVersion(vj, mcDir, _ => true);
                var tasks = plan.Tasks().ToList();
                Check($"Cleanroom：规划得出 {tasks.Count} 个下载任务（含客户端 jar）",
                    plan.ClientJar is not null && tasks.Count >= 150,
                    string.Join("、", plan.Warnings));

                // 全部任务都得有地址（空地址 = 下载时才会炸，报错还看不出根因）
                var emptyUrls = tasks.Where(t => t.Url.Length == 0).Select(t => t.Label).ToList();
                Check("Cleanroom：没有一个任务的地址是空的", emptyUrls.Count == 0,
                    string.Join("、", emptyUrls.Take(5)));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }

    /// <summary>把 Forge 的安装器**真下下来、真跑一遍**；⚠️ 这一节**不假装能装出一个能启动的版本**：Forge 1.20.1 的安装器要给原版 jar 打补丁，而自检里只放了一个**最小桩**（原版完整装下来是 700 MB，自检不能那么重）。</summary>
    private static async Task CheckRealForgeInstallAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "forge-install");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }

        var baseDir = Path.Combine(scratch, "base");
        var mcDir = Path.Combine(scratch, "mc");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(mcDir, "versions", "1.20.1"));

        // 原版最小桩（真装原版要 700 MB，自检不能那么重）
        File.WriteAllText(Path.Combine(mcDir, "versions", "1.20.1", "1.20.1.json"), """
            {"id":"1.20.1","type":"release",
             "mainClass":"net.minecraft.client.main.Main",
             "libraries":[],"assetIndex":{"id":"5"}}
            """);

        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        var forgeRow = (await LoaderCatalog.LoaderRowsAsync("1.20.1")).First(r => r.Key == "forge");
        var loaderVersion = forgeRow.Latest;
        if (loaderVersion.Length == 0)
        {
            Check("（跳过）真跑 Forge：拉不到版本列表", true, "网络问题");
            return;
        }

        // 1. 安装器下得下来、而且真的是个安装器
        var url = LoaderInstaller.InstallerUrl("1.20.1", "forge", loaderVersion);
        var jarPath = Path.Combine(LoaderInstaller.InstallerDir(mcDir),
            LoaderInstaller.InstallerFileName("forge", "1.20.1", loaderVersion));

        var manager = new DownloadManager(maxWorkers: 2);
        var task = manager.Add(url, jarPath, label: Path.GetFileName(jarPath), kind: "installer",
            altUrls: new[] { LoaderInstaller.ForgeMavenUrl("1.20.1", loaderVersion) });
        manager.Start();
        manager.WaitAll(TimeSpan.FromSeconds(180));

        Check($"Forge 安装器下下来了（{DownloadManager.HumanSize(task.DoneBytes)}）",
            task.State == DownloadManager.StateDone, $"{task.State} {task.Error}");
        if (task.State != DownloadManager.StateDone)
        {
            return;
        }

        // ⚠️ 失败时下到的常常是 HTML 错误页改名成的 jar —— 这一步就是挡它的
        Check("下到的东西真的是个安装器（根上有 install_profile.json / version.json）",
            LoaderInstaller.IsInstallerJar(jarPath));

        // 2. 读得出它自己要的库
        var libTasks = LoaderInstaller.InstallerLibraryTasks(jarPath, mcDir);
        Check($"读得出安装器自己要的库（{libTasks.Count} 个）", libTasks.Count > 0);
        Check("那些库的任务带着下载地址和落点",
            libTasks.All(t => t.Url.StartsWith("https://", StringComparison.Ordinal)
                              && t.Path.Length > 0));

        // 3. 命令行拼得对
        var javaPath = FindAnyJava();
        if (javaPath is null)
        {
            Check("（跳过）真跑 Forge：这台机器没有可用的 Java", true);
            return;
        }

        var cmd = LoaderInstaller.InstallerCommand(javaPath, jarPath, mcDir, "forge");
        // 形状是 [java, -jar, <安装器>, --installClient, <目录>] —— 共 5 项
        Check("命令行是 `java -jar <安装器> --installClient <目录>`",
            cmd.Count == 5 && cmd[1] == "-jar" && cmd[2] == jarPath
            && cmd[3] == "--installClient" && cmd[4] == mcDir,
            string.Join(" ", cmd));

        // OptiFine 走的是**另一条**命令行（`-cp` + 无窗口主类），顺手验一下形状
        var optifineCmd = LoaderInstaller.InstallerCommand(javaPath, jarPath, mcDir, "optifine");
        Check("OptiFine 的参数是空的（它认 cwd / APPDATA，不吃参数）",
            LoaderInstaller.InstallerArguments("optifine", mcDir).Count == 0);
        _ = optifineCmd;

        // 4. 真跑
        // launcher_profiles.json 少了它会直接失败（而且报的是"你去把启动器跑一遍"）
        var created = LoaderInstaller.EnsureLauncherProfiles(mcDir);
        Check("launcher_profiles.json 建出来了", created
            && File.Exists(Path.Combine(mcDir, "launcher_profiles.json")));

        var lines = new List<string>();
        var collected = 0;
        LoaderInstaller.InstallerRunResult run;
        try
        {
            run = LoaderInstaller.RunInstaller(
                javaPath, jarPath, mcDir, "forge",
                onLine: line => { lock (lines) { lines.Add(line); collected++; } },
                // 只给 5 分钟 —— 完整装一次要 200 秒上下，而这里原版是个桩，
                // 装不成也是正常的，没必要等两小时
                timeoutSeconds: 300);
        }
        catch (LoaderSetupException ex)
        {
            Check("真跑 Forge 安装器（起进程 / 超时）", false, ex.Message);
            return;
        }

        Check($"安装器进程真跑完了（退出码 {run.ReturnCode}，{run.Seconds:F0} 秒，收了 {run.Lines.Count} 行输出）",
            run.Lines.Count > 0);
        Check($"节流生效了（喂给界面的只有 {collected} 行，原始 {run.Lines.Count} 行）",
            collected <= run.Lines.Count);

        // ⚠️ **不强求退出码是 0**：原版是桩，安装器理论上打不了补丁。
        // 但实测它是 0（Forge 1.20.1 的安装器在 `--installClient` 这一步
        // 只写版本 JSON + 下库，补丁是运行时由 processors 干的）。
        // 所以顺手把装出来的东西**验一遍** —— 那才是"真装上了"的证据。
        if (run.ReturnCode == 0)
        {
            var versionsDir = Path.Combine(mcDir, "versions");
            var forgeDir = Directory.Exists(versionsDir)
                ? Directory.EnumerateDirectories(versionsDir)
                    .FirstOrDefault(d => Path.GetFileName(d).Contains("forge", StringComparison.OrdinalIgnoreCase))
                : null;

            Check("退出码 0 的话应该多出一个 forge 版本目录", forgeDir is not null);
            if (forgeDir is not null)
            {
                var forgeId = Path.GetFileName(forgeDir);
                Check($"装出来的目录名（{forgeId}）", forgeId.Contains("forge", StringComparison.OrdinalIgnoreCase));

                var (ok, why) = LoaderInstaller.VerifyInstalled(mcDir, forgeId);
                Check("装出来的版本能通过「装完校验」（有 JSON / id / 主类或父版本）", ok, why);

                var jsonPath = Path.Combine(forgeDir, forgeId + ".json");
                if (File.Exists(jsonPath))
                {
                    var data = JsonIO.ParseBytes(File.ReadAllBytes(jsonPath)) as JsonObject;
                    // ⚠️ Forge 1.13+ 的 JSON 里**没有**客户端 jar，主类在它自己那份里，
                    // 原版的一切靠 `inheritsFrom` 合过来 —— 少了这一行就起不来
                    Equal("Forge 的版本 JSON 里写了 inheritsFrom", "1.20.1",
                        data?["inheritsFrom"]?.GetValue<string>() ?? "<没有>");
                    Check("Forge 的版本 JSON 里有它自己的库",
                        (data?["libraries"] as JsonArray)?.Count > 0);
                }

                // 最后：**扫一遍版本列表，看得见才算装上**
                var scanned = await services.Games.RefreshAsync();
                Check("扫版本列表能看见装好的 Forge",
                    scanned.Any(v => v.Id == forgeId || v.DirName == forgeId),
                    "扫到：" + string.Join("、", scanned.Select(v => v.Id)));
            }
        }
        else
        {
            // 失败也要有**有用的**提示（不是安装器那句零信息量的套话）
            Check($"失败时给出了有用的提示：{run.Error}",
                run.Error.Length > 0
                && !run.Error.Contains("There was an error during installation", StringComparison.Ordinal));
        }

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    /// <summary>随便找一个能用的 Java（真跑安装器那一节用）。</summary>
    private static string? FindAnyJava()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "cfg");
        try
        {
            return new JavaScanner(cacheDir).FindJavas().FirstOrDefault(p => p.Usable)?.Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>只发一个 HEAD，看这个地址通不通（不真下内容）；⚠️ 用 HEAD 不是 GET：验的是"地址拼对了没有"，不是"文件好不好"。</summary>
    private static async Task<bool> UrlIsReachableAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.UserAgent.ParseAdd(DownloadManager.UserAgent);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    // 整合包服务（离线端到端）

    /// <summary>把整合包**服务层**真跑一遍：建实例目录 → 展开覆盖层 → 写版本 JSON；⚠️ 刻意用一个**没有 files、也没有加载器**的包，这样一步网络都不用走 —— 自检是离线/CI 的常客，不该为了验编排就要求联网。</summary>
    private static void CheckModpackService()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "mrpack-svc");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }

        var baseDir = Path.Combine(scratch, "base");
        var mcDir = Path.Combine(scratch, "mc");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(mcDir);

        // 包名里塞一个 Windows 非法字符（`/`）和一个保留名，验证消毒真的生效
        var blob = BuildTestMrpack("Test/Pack CON", overrides:
            new Dictionary<string, string>
            {
                ["overrides/config/a.json"] = """{"from":"overrides"}""",
                ["client-overrides/config/a.json"] = """{"from":"client"}""",
            });

        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        // 先"只看不装"一遍 —— 界面预览走的就是这条
        var plan = services.Modpacks.Inspect(blob, out var error);
        Check("预览解析得出计划", plan is not null, error);
        if (plan is null)
        {
            return;
        }
        Equal("预览里的包名", "Test/Pack CON", plan.Name);
        Equal("预览里的覆盖层条数", "2", plan.Overrides.Count.ToString());

        // 真装一遍
        var mrpackPath = Path.Combine(scratch, "test.mrpack");
        File.WriteAllBytes(mrpackPath, blob);

        var result = services.Modpacks.InstallAsync(new ModpackInstallRequest(
            MrpackPath: mrpackPath,
            MinecraftDir: mcDir,
            // 这个包没写加载器，所以这一步本来就会跳过；显式关掉更明确
            InstallLoader: false)).GetAwaiter().GetResult();

        Check("装成功了（离线，0 个文件要下）", result.Ok, result.Message);

        // ⚠️ 实例名必须**消毒过**：`Test/Pack CON` 里的 `/` 会变成 `-`，
        // `CON` 是 Windows 保留名要加前缀 —— 不消毒的话目录根本建不出来
        var instanceId = result.VersionId ?? "<null>";
        Check($"实例名消毒过了（{instanceId}）",
            !instanceId.Contains('/') && !instanceId.Contains('\\')
            && !instanceId.Split('.')[0].Equals("CON", StringComparison.OrdinalIgnoreCase),
            instanceId);

        Equal("覆盖层展开了 2 个文件", "2", result.OverridesWritten.ToString());

        var instanceDir = Path.Combine(mcDir, "versions", instanceId);
        Check("实例目录建在 versions/<实例名>/ 底下", Directory.Exists(instanceDir));

        // ⚠️ 覆盖**顺序**：client-overrides 要盖住 overrides 里同名那个
        var settingsFile = Path.Combine(instanceDir, "config", "a.json");
        Check("client-overrides 盖住了 overrides 里的同名文件",
            File.Exists(settingsFile) && File.ReadAllText(settingsFile).Contains("client"),
            File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "<文件不在>");

        // 版本 JSON：这个包没加载器，所以写的是**兜底**那份（列得出来、
        // 点启动会缺 mainClass），而且必须带上 `modpack` 元数据
        var versionJson = Path.Combine(instanceDir, instanceId + ".json");
        Check("版本 JSON 写出来了", File.Exists(versionJson));
        if (File.Exists(versionJson))
        {
            var data = JsonNode.Parse(File.ReadAllBytes(versionJson)) as JsonObject;
            Equal("版本 JSON 里的 id = 实例名",
                instanceId, data?["id"]?.GetValue<string>() ?? "<null>");
            var meta = data?["modpack"] as JsonObject;
            Check("版本 JSON 里带着 modpack 元数据", meta is not null);
            Equal("modpack 元数据里的包名",
                "Test/Pack CON", meta?["name"]?.GetValue<string>() ?? "<null>");
            Equal("modpack 元数据里记了覆盖层写了几个",
                "2", meta?["overridesWritten"]?.GetValue<int>().ToString() ?? "<null>");
        }

        // 这个包**既没写加载器、也没写 Minecraft 版本** → 连父版本都指不出来，
        // 只能写兜底 JSON。这一步必须如实报告，不能让用户以为"装好了就能玩"
        Check("如实报告\"这个实例还不能直接启动\"", result.NeedsLoader, result.Message);
        Check("告警里说清了为什么（没写 Minecraft 版本 / 没加载器）",
            result.Warnings?.Any(w => w.Contains("Minecraft") || w.Contains("加载器")) == true,
            string.Join("；", result.Warnings ?? new List<string>()));

        // 版本列表要能扫到它（这是"装完看不见"那个坑的现场）
        var scanned = services.Games.RefreshAsync().GetAwaiter().GetResult();
        Check("扫版本列表能扫到这个实例",
            scanned.Any(v => v.Id == instanceId || v.DirName == instanceId),
            $"扫到 {scanned.Count} 个");

        // 第二个包：有 Minecraft 版本、**没有加载器**
        // 原版在本地时，实例应该压平原版那份 → 自包含、直接能启动
        // （PCL 那种"原版 + 配置"的整合包）。老实现这里一律写兜底 JSON，
        // 于是这种包**装完永远起不来**。
        WriteFakeVersion(mcDir, "1.20.1", new JsonObject
        {
            ["id"] = "1.20.1",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["assets"] = "5",
            ["assetIndex"] = new JsonObject { ["id"] = "5" },
            ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
            ["libraries"] = new JsonArray(),
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--username", "${auth_player_name}"),
                ["jvm"] = new JsonArray("-Djava.library.path=${natives_directory}"),
            },
        }, withJar: true);

        var vanillaBlob = BuildTestMrpack("Plain Pack", new Dictionary<string, string>(),
            new JsonObject { ["minecraft"] = "1.20.1" });
        var vanillaMrpack = Path.Combine(scratch, "plain.mrpack");
        File.WriteAllBytes(vanillaMrpack, vanillaBlob);

        var plain = services.Modpacks.InstallAsync(new ModpackInstallRequest(
            MrpackPath: vanillaMrpack,
            MinecraftDir: mcDir,
            InstallLoader: false)).GetAwaiter().GetResult();

        Check("没加载器但原版在 → 实例直接能启动（不再报 needsLoader）",
            plain.Ok && !plain.NeedsLoader, plain.Message);
        var plainId = plain.VersionId ?? "<null>";
        var plainDir = Path.Combine(mcDir, "versions", plainId);
        var plainData = File.Exists(Path.Combine(plainDir, plainId + ".json"))
            ? JsonNode.Parse(File.ReadAllBytes(Path.Combine(plainDir, plainId + ".json"))) as JsonObject
            : null;
        Check("没加载器的实例也是自包含的（不带 inheritsFrom、自己有主类、jar 也拷进来了）",
            string.IsNullOrEmpty(plainData?["inheritsFrom"]?.GetValue<string>())
            && !string.IsNullOrEmpty(plainData?["mainClass"]?.GetValue<string>())
            && File.Exists(Path.Combine(plainDir, plainId + ".jar")),
            $"mainClass={plainData?["mainClass"]?.GetValue<string>() ?? "<null>"}");

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    /// <summary>在内存里造一个最小的 `.mrpack`（给上面那节用）。</summary>
    private static byte[] BuildTestMrpack(string name, Dictionary<string, string> overrides,
        JsonObject? dependencies = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new JsonObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["name"] = name,
                ["versionId"] = "1.0.0",
                ["summary"] = "自检用的最小整合包",
                // ⚠️ 默认**不写**依赖：没加载器，服务层就走"兜底版本 JSON"那条路，
                // 一步网络都不用走。要验"继承加载器 / 原版"时再由调用方补上。
                ["dependencies"] = dependencies ?? new JsonObject(),
                ["files"] = new JsonArray(),
            };
            WriteZipEntry(zip, "modrinth.index.json", index.ToJsonString());
            foreach (var (path, content) in overrides)
            {
                WriteZipEntry(zip, path, content);
            }
        }
        return stream.ToArray();
    }

    // 整合包实例能不能启动（`inheritsFrom` 那条链）

    /// <summary>整合包实例 → 加载器版本 → 原版：这条 `inheritsFrom` 链要能拼出**真能启动**的计划。</summary>
    private static void CheckModpackInstanceLaunch()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "mrpack-instance");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }

        var mcDir = Path.Combine(scratch, "mc");
        Directory.CreateDirectory(mcDir);

        // 1) 原版 1.20.1：主类 + 客户端 jar 在这里
        WriteFakeVersion(mcDir, "1.20.1", new JsonObject
        {
            ["id"] = "1.20.1",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["assets"] = "5",
            ["assetIndex"] = new JsonObject { ["id"] = "5" },
            ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
            // 一条库就够了：验的是"压平之后库没丢"，不是下载
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "com.example:vanilla-lib:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = "com/example/vanilla-lib/1.0/vanilla-lib-1.0.jar",
                        },
                    },
                },
            },
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--username", "${auth_player_name}"),
                ["jvm"] = new JsonArray("-Djava.library.path=${natives_directory}"),
            },
        }, withJar: true);

        // 2) Forge 47.4.10：继承原版、主类在它自己这份里、**没有**自己的客户端 jar
        // （Forge 1.13+ 就是这样：jar 在原版那层）
        WriteFakeVersion(mcDir, "1.20.1-forge-47.4.10", new JsonObject
        {
            ["id"] = "1.20.1-forge-47.4.10",
            ["inheritsFrom"] = "1.20.1",
            ["type"] = "release",
            ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "net.minecraftforge:forge:1.20.1-47.4.10",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = "net/minecraftforge/forge/1.20.1-47.4.10/forge-1.20.1-47.4.10.jar",
                        },
                    },
                },
            },
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--launchTarget", "forgeclient"),
            },
        }, withJar: false);

        // 3) 整合包实例：只有 `inheritsFrom` + `modpack` 元数据（PCL 那种做法）
        var instance = "Zombie Invade 100 Days";
        var blob = BuildTestMrpack(instance, new Dictionary<string, string>
        {
            ["overrides/config/pack.json"] = """{"pack":"zombie"}""",
        }, new JsonObject
        {
            ["minecraft"] = "1.20.1",
            ["forge"] = "47.4.10",
        });

        var plan = ModpackInstaller.PlanFromBytes(blob);
        Equal("整合包解析出加载器", "forge", plan.Loader.Loader);
        Equal("整合包解析出加载器版本", "47.4.10", plan.Loader.Version);

        // 加载器装成**安装器自己起的名字**（`1.20.1-forge-47.4.10`）时也要认得 ——
        // 我们约定的名字是 `1.20.1-Forge_47.4.10`，只认后者就会把"已经装好了"当成没装。
        Equal("容错查找：认得安装器起的目录名",
            "1.20.1-forge-47.4.10",
            LoaderInstaller.FindInstalledVersion(mcDir, "1.20.1", "forge", "47.4.10") ?? "<null>");
        Equal("容错查找：版本号对不上的一律不认（不会随手抓一个 forge 版本顶替）",
            "<null>",
            LoaderInstaller.FindInstalledVersion(mcDir, "1.20.1", "forge", "47.4.26") ?? "<null>");

        // 4) 走**真服务**装一遍（这一步就是用户点"安装"走的那条路）。
        // 原版和加载器都已经在本地（上面那两个假版本）→ 不会去下载任何东西，
        // 所以整条路离线跑得通。
        var baseDir = Path.Combine(scratch, "base");
        Directory.CreateDirectory(baseDir);
        var mrpackPath = Path.Combine(scratch, "zombie.mrpack");
        File.WriteAllBytes(mrpackPath, blob);

        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        var installed = services.Modpacks.InstallAsync(new ModpackInstallRequest(
            MrpackPath: mrpackPath,
            MinecraftDir: mcDir,
            InstallLoader: true)).GetAwaiter().GetResult();

        Check("真服务装成功了", installed.Ok,
            string.Join("；", installed.Warnings ?? new List<string>()) + " " + installed.Message);
        Check("真服务认出了本地已有的加载器（不再报「加载器没装上」）",
            !installed.NeedsLoader, installed.Message);

        var instanceDir = Path.Combine(mcDir, "versions", instance);
        var instanceJsonPath = Path.Combine(instanceDir, instance + ".json");
        var written = File.Exists(instanceJsonPath)
            ? JsonNode.Parse(File.ReadAllBytes(instanceJsonPath)) as JsonObject
            : null;

        // 自包含：一个版本文件夹里就是全部
        // 用户的原话是"全部装一起不要分版本 / 不要一个原版一个模组加载器版"。
        Check("实例 JSON 里**没有** inheritsFrom（不依赖别的版本）",
            string.IsNullOrEmpty(written?["inheritsFrom"]?.GetValue<string>()),
            written?["inheritsFrom"]?.GetValue<string>() ?? "<null>");
        Check("实例 JSON 自己带着主类",
            !string.IsNullOrEmpty(written?["mainClass"]?.GetValue<string>()),
            written?["mainClass"]?.GetValue<string>() ?? "<null>");
        Check("实例 JSON 自己带着库列表",
            (written?["libraries"] as JsonArray)?.Count > 0,
            ((written?["libraries"] as JsonArray)?.Count ?? 0).ToString());
        Equal("实例 JSON 里的 id 就是实例名",
            instance, written?["id"]?.GetValue<string>() ?? "<null>");

        // 客户端 jar 必须**在实例目录里**（拷进来，不是指回原版目录）
        var instanceJar = Path.Combine(instanceDir, instance + ".jar");
        Check("客户端 jar 拷进了实例目录", File.Exists(instanceJar), instanceJar);

        // 元数据：压平之后没有 inheritsFrom，扫描器靠这段认"这是哪个整合包 / 哪一版"
        var meta = written?["modpack"] as JsonObject;
        Equal("实例 JSON 里记着原版版本", "1.20.1",
            meta?["minecraft"]?.GetValue<string>() ?? "<null>");
        Equal("实例 JSON 里记着加载器", "forge",
            meta?["loader"]?.GetValue<string>() ?? "<null>");

        // 拼启动计划
        var launch = LaunchPlanBuilder.Build(new LaunchPlanRequest
        {
            MinecraftDir = mcDir,
            VersionId = instance,
            JavaPath = Path.Combine(scratch, "fake-java.exe"),
            Username = "Tester",
            MaxMemoryMb = 2048,
        });

        Check("启动计划用的客户端 jar 就是实例目录里那个",
            string.Equals(Path.GetFullPath(launch.ClientJar), Path.GetFullPath(instanceJar),
                StringComparison.OrdinalIgnoreCase),
            launch.ClientJar);
        Equal("主类来自加载器那层（压平后仍然对）",
            "cpw.mods.bootstraplauncher.BootstrapLauncher", launch.VersionJson.MainClass);
        Check("需要的 Java 版本也继承到了（17，不是默认的 8）",
            launch.VersionJson.JavaMajor == 17, launch.VersionJson.JavaMajor.ToString());
        Check("classpath 里有那个 jar",
            launch.Classpath.Any(p => p.EndsWith(instance + ".jar", StringComparison.OrdinalIgnoreCase)),
            string.Join("；", launch.Classpath));
        Check("没有任何『找不到客户端 jar』的告警（用户看到的那个错）",
            !launch.Warnings.Any(w => w.Contains("找不到客户端 jar")),
            string.Join("；", launch.Warnings));
        Check("原生库解压在实例目录里（版本隔离）",
            launch.NativesDir.StartsWith(instanceDir, StringComparison.OrdinalIgnoreCase),
            launch.NativesDir);
        Check("游戏工作目录就是实例目录（隔离时 mods 才认得对）",
            string.Equals(Path.GetFullPath(launch.WorkingDirectory), Path.GetFullPath(instanceDir),
                StringComparison.OrdinalIgnoreCase),
            launch.WorkingDirectory);

        // 没有加载器的包：也要自包含
        var plainBlob = BuildTestMrpack("Plain Pack", new Dictionary<string, string>(),
            new JsonObject { ["minecraft"] = "1.20.1" });
        var plainPath = Path.Combine(scratch, "plain.mrpack");
        File.WriteAllBytes(plainPath, plainBlob);
        var plain = services.Modpacks.InstallAsync(new ModpackInstallRequest(
            MrpackPath: plainPath, MinecraftDir: mcDir, InstallLoader: false)).GetAwaiter().GetResult();

        var plainId = plain.VersionId ?? "<null>";
        var plainDir = Path.Combine(mcDir, "versions", plainId);
        var plainJson = File.Exists(Path.Combine(plainDir, plainId + ".json"))
            ? JsonNode.Parse(File.ReadAllBytes(Path.Combine(plainDir, plainId + ".json"))) as JsonObject
            : null;
        Check("没加载器的包也是自包含的（不带 inheritsFrom、自己有 jar）",
            plain.Ok
            && string.IsNullOrEmpty(plainJson?["inheritsFrom"]?.GetValue<string>())
            && File.Exists(Path.Combine(plainDir, plainId + ".jar")),
            plain.Message);

        // 中间产物清理：只删"这次才装出来的"，用户自己的不碰
        Equal("清理：原版和加载器都是这次才装的 → 两个都删",
            "1.20.1|1.20.1-forge-47.4.10",
            string.Join("|", RealModpackService.IntermediateVersionsToDelete(
                "1.20.1", "1.20.1-forge-47.4.10", vanillaIsOurs: true, loaderIsOurs: true)));
        Equal("清理：用户自己装的原版 / 加载器（不是我们的）→ 一个都不动",
            "",
            string.Join("|", RealModpackService.IntermediateVersionsToDelete(
                "1.20.1", "1.20.1-forge-47.4.10", vanillaIsOurs: false, loaderIsOurs: false)));
        Equal("清理：没加载器的包（源头就是原版）→ 只删原版一份，不重复删",
            "1.20.1",
            string.Join("|", RealModpackService.IntermediateVersionsToDelete(
                "1.20.1", "1.20.1", vanillaIsOurs: true, loaderIsOurs: true)));

        // 依赖缓存：藏在磁盘上，但不进版本列表
        var cacheDir = Path.Combine(mcDir, "versions", "1.20.1-forge-47.4.10-cache");
        WriteFakeVersion(mcDir, "1.20.1-forge-47.4.10-cache", new JsonObject
        {
            ["id"] = "1.20.1-forge-47.4.10-cache",
            ["inheritsFrom"] = "1.20.1",
            ["type"] = "release",
            ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        }, withJar: false);
        var listedBefore = VersionScanner.ScanVersions(mcDir).Any(v => v.Id == "1.20.1-forge-47.4.10-cache");
        VersionScanner.MarkAsCache(cacheDir);
        var listedAfter = VersionScanner.ScanVersions(mcDir).Any(v => v.Id == "1.20.1-forge-47.4.10-cache");
        Check("打了缓存记号之前它是在列表里的", listedBefore);
        Check("打了缓存记号之后不在列表里了，但文件还在",
            !listedAfter && Directory.Exists(cacheDir), $"listedAfter={listedAfter}");

        // 旧行为留个对照：兜底 JSON（没有主类）照样是"列得出来、起不来"
        var brokenDir = Path.Combine(mcDir, "versions", "broken-pack");
        Directory.CreateDirectory(brokenDir);
        GameInstaller.WriteVersionJson(plan.VersionJson("broken-pack"), brokenDir, "broken-pack");

        // 扫描器：实例要被认成"整合包"，不是"加载器版本"
        var scanned = VersionScanner.ScanVersions(mcDir);
        var found = scanned.FirstOrDefault(v => v.Id == instance);
        Check("扫描器扫得到整合包实例", found is not null, $"扫到 {scanned.Count} 个");
        if (found is not null)
        {
            Check($"实例归到「整合包」那一段：{found.Kind}",
                found.Kind == VersionKind.Pack, found.Kind.ToString());
            Equal("实例显示的加载器名来自 JSON 的 modpack 元数据", "Forge", found.Loader);
            Equal("实例的 Minecraft 版本来自 JSON 的 modpack 元数据", "1.20.1", found.MinecraftVersion);
            Check("实例算「完整」（自己的 jar 就在）", found.Complete);
            Check("实例是版本隔离的", found.Isolated);
        }

        // 用户自己装的原版 / 加载器不许被删
        // 这两个假版本是**装包之前就在**的，而且没有缓存记号 —— 对装包来说
        // 它们"不是我们的"，一个都不该动（用户可能是自己装的、拿来玩的）。
        Check("装包没删用户自己装的原版",
            Directory.Exists(Path.Combine(mcDir, "versions", "1.20.1")));
        Check("装包没删用户自己装的加载器版本",
            Directory.Exists(Path.Combine(mcDir, "versions", "1.20.1-forge-47.4.10")));

        // 上一次没装完留下的半成品：这次顺手清掉
        // "没装完"的表现就是：目录在、而且带着我们打的缓存记号（原版在开始下载之前就打）。
        VersionScanner.MarkAsCache(Path.Combine(mcDir, "versions", "1.20.1"));
        var again = services.Modpacks.InstallAsync(new ModpackInstallRequest(
            MrpackPath: mrpackPath,
            MinecraftDir: mcDir,
            InstallLoader: true)).GetAwaiter().GetResult();
        Check("再装一遍：带缓存记号的半成品原版被清掉了",
            again.Ok && !Directory.Exists(Path.Combine(mcDir, "versions", "1.20.1")),
            again.Message);
        Check("再装一遍：没记号的加载器版本照样留着",
            Directory.Exists(Path.Combine(mcDir, "versions", "1.20.1-forge-47.4.10")));

        // 跟 PCL2 对齐的版本隔离开关
        // PCL2 装整合包时会往实例目录写 `PCL\Setup.ini` 的
        // `VersionArgumentIndie:1` / `VersionArgumentIndieV2:True`；
        // 我们也写一份，这样同一个 `.minecraft` 两边打开都认"这个版本是隔离的"。
        var pclIni = Path.Combine(instanceDir, "PCL", "Setup.ini");
        var iniText = File.Exists(pclIni) ? File.ReadAllText(pclIni) : string.Empty;
        Check("实例里写了 PCL\\Setup.ini 的版本隔离开关",
            iniText.Contains("VersionArgumentIndie:1", StringComparison.Ordinal)
            && iniText.Contains("VersionArgumentIndieV2:True", StringComparison.Ordinal),
            iniText.Length > 0 ? iniText.Replace("\r\n", " / ").Replace("\n", " / ") : "<文件不在>");
        // 另外几个键是照着 PCL2 装出来的实例抄的（用户机器上 LeSkyBlock 那份）：
        // VersionVanilla / VersionVanillaName / Version<加载器> / Info
        Check("PCL\\Setup.ini 里也写了「这一版是什么」（VersionVanilla / Info / VersionForge）",
            iniText.Contains("VersionVanilla:1.20.1", StringComparison.Ordinal)
            && iniText.Contains("VersionVanillaName:1.20.1", StringComparison.Ordinal)
            && iniText.Contains("VersionForge:47.4.10", StringComparison.Ordinal)
            && iniText.Contains("Info:正式版 1.20.1, Forge 47.4.10", StringComparison.Ordinal),
            iniText.Replace("\r\n", " / ").Replace("\n", " / "));

        // 反过来：只靠 ini（没有 mods/ 这类特征）也要认得出隔离 ——
        // PCL2 建的版本可能就是这样（空实例 / 只放了 config）。
        var pclOnly = Path.Combine(mcDir, "versions", "pcl-only");
        WriteFakeVersion(mcDir, "pcl-only", new JsonObject
        {
            ["id"] = "pcl-only",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
        }, withJar: true);
        Directory.CreateDirectory(Path.Combine(pclOnly, "PCL"));
        File.WriteAllText(Path.Combine(pclOnly, "PCL", "Setup.ini"),
            "VersionArgumentIndie:1\nVersionArgumentIndieV2:True\n", new UTF8Encoding(false));
        var pclOnlyVersion = VersionScanner.ScanVersions(mcDir).FirstOrDefault(v => v.Id == "pcl-only");
        Check("只靠 PCL 的 Setup.ini 也能认出「版本隔离」",
            pclOnlyVersion?.Isolated == true,
            pclOnlyVersion is null ? "<没扫到>" : $"Isolated={pclOnlyVersion.Isolated}");

        // 多批下载时进度广播要跟着切；窗口定时器不能提前停，否则后一批下载没人刷新。
        var tempJar = Path.Combine(Path.GetTempPath(), "mosslight-monitor-probe.jar");
        var batchA = new DownloadManager(maxWorkers: 1, source: DownloadSourceMode.Official);
        batchA.Add("https://example.invalid/a.jar", tempJar, string.Empty, 0, "a.jar", "库");
        var snapA = batchA.Snapshot();
        Check("进度广播：第一批（包里的文件）的快照读得到",
            ReferenceEquals(DownloadMonitor.Last, snapA));
        var batchB = new DownloadManager(maxWorkers: 1, source: DownloadSourceMode.Official);
        batchB.Add("https://example.invalid/b.jar", tempJar, string.Empty, 0, "b.jar", "库");
        var snapB = batchB.Snapshot();
        Check("进度广播：第二批（原版那几百个文件）会把上一批顶掉 —— 窗口要跟着切",
            ReferenceEquals(DownloadMonitor.Last, snapB) && !ReferenceEquals(snapA, snapB));
        DownloadMonitor.Reset();

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    /// <summary>写一个假版本（JSON + 可选客户端 jar）。</summary>
    private static void WriteFakeVersion(string mcDir, string id, JsonObject data, bool withJar)
    {
        var dir = Path.Combine(mcDir, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, id + ".json"),
            data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        if (withJar)
        {
            File.WriteAllBytes(Path.Combine(dir, id + ".jar"), new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        }
    }

    /// <summary>在内存里造一个普通 zip（条目名 → 文本内容）。</summary>
    private static byte[] BuildPlainZip(Dictionary<string, string> entries)    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                WriteZipEntry(zip, path, content);
            }
        }
        return stream.ToArray();
    }

    // 本地整合包压缩包的格式识别（"把 zip 拖进来"那一步）

    // 滑到底续取（`ScrollLoader`）

    /// <summary>`ScrollLoader.ShouldLoadMore`：滑到底要不要续取下一页。</summary>
    private static void CheckScrollLoader()
    {
        // 视口 / 内容都是 0（还没布局、或者面板根本没显示）→ 不下结论
        Check("续取判断：还没布局（视口 0）时不加载",
            !Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(0, 0, 0));
        Check("续取判断：有内容但没视口时不加载",
            !Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(0, 0, 900));

        // 内容不够一屏：滚动条都不会出现，必须直接续（否则剩下的永远看不到）
        Check("续取判断：内容不足一屏 → 直接续",
            Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(0, 600, 400));

        // 在顶部、离底部还很远 → 不加载
        Check("续取判断：在顶部时不加载",
            !Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(0, 600, 3000));

        // 提前量 **64**：离底部 64 以内才续铺，差 120 一律不动
        // （差不多一个折叠标题的高度；再大就会"还没到底就开始加载"，显得很暴躁）。
        Check("续取判断：离底部 64 以内 → 加载",
            Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(2400 - 64, 600, 3000));
        Check("续取判断：离底部 120（超过提前量）→ 先不加载",
            !Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(2400 - 120, 600, 3000));

        // 已经到底 → 一定加载
        Check("续取判断：到底了 → 加载",
            Mosslight.App.Infrastructure.ScrollLoader.ShouldLoadMore(2400, 600, 3000));
    }

    // 收藏夹（`FavoritesStore`）

    /// <summary>收藏夹：存的是**项目**，键是 `(project_type, project_id)`。</summary>
    private static void CheckFavorites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "favorites");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }

        var store = new Mosslight.Core.Services.FavoritesStore(dir);
        store.Clear();
        Check("收藏夹：一开始是空的", store.Items.Count == 0);

        var hit = new JsonObject
        {
            ["project_id"] = "sodium",
            ["slug"] = "sodium",
            ["title"] = "Sodium",
            ["project_type"] = "mod",
        };
        var item = new Mosslight.Core.Services.FavoriteItem(
            "mod", "sodium", "Sodium", "https://example.invalid/sodium.png", hit, DateTimeOffset.Now);

        Check("收藏夹：加一条 → 返回 true（现在在收藏夹里）", store.Toggle(item));
        Check("收藏夹：Contains 认得出这个项目", store.Contains("mod", "sodium"));
        Check("收藏夹：类型不一样就不算同一个", !store.Contains("modpack", "sodium"));

        // 重新读一遍（跨会话的真实路径）：标题 / 图标 / 命中 JSON 都得还在
        var reloaded = new Mosslight.Core.Services.FavoritesStore(dir);
        var saved = reloaded.Items.FirstOrDefault();
        Check("收藏夹：重启后还在（写到 favorites.json 了）", reloaded.Items.Count == 1);
        Check("收藏夹：标题 / 图标 / 命中 JSON 一起存下来了",
            saved?.Title == "Sodium"
            && saved.IconUrl.EndsWith("sodium.png", StringComparison.Ordinal)
            && saved.Hit["project_id"]?.GetValue<string>() == "sodium",
            saved is null ? "<没有>" : saved.Title);

        // 再点一次 = 取消收藏
        Check("收藏夹：同一条再 Toggle → false（移除了）", !reloaded.Toggle(item));
        Check("收藏夹：移除之后磁盘上也没了",
            new Mosslight.Core.Services.FavoritesStore(dir).Items.Count == 0);

        // 两个不同类型的同名项目互不影响（去重键必须是「类型 + id」）
        reloaded.Toggle(item);
        reloaded.Toggle(item with { ProjectType = "modpack", Title = "Sodium 整合包" });
        Check("收藏夹：同 id 不同类型算两条", reloaded.Items.Count == 2);

        // 文件坏了要当空的（不抛）
        File.WriteAllText(Path.Combine(dir, Mosslight.Core.Services.FavoritesStore.FileName),
            "{ 这不是 json", new UTF8Encoding(false));
        var broken = new Mosslight.Core.Services.FavoritesStore(dir);
        Check("收藏夹：文件坏了当空的处理，不抛", broken.Items.Count == 0);

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    // 资源字典 key（`FindResource` 的 key 必须存在）

    /// <summary>代码里 `FindResource("X")` / `TryFindResource("X")` 用的每个 key，都必须在某个 `.xaml` 的 `x:Key="X"` 里定义过。</summary>
    private static void CheckResourceKeys()
    {
        // 仓库根：自检的 exe 在 `<repo>\Mosslight.Core.SelfCheck\bin\<cfg>\<tfm>\`
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var appDir = Path.Combine(repo, "Mosslight.App");
        if (!Directory.Exists(appDir))
        {
            Console.WriteLine($"  （跳过：找不到 {appDir}）");
            return;
        }

        // ① 资源字典里定义过的 key（含 DynamicResource 也能用的那些）
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "x:Key=\"([^\"]+)\""))
            {
                defined.Add(m.Groups[1].Value);
            }
        }

        // ② 代码里用字符串字面量查的 key（变量传进去的不在这个检查范围内）
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(
                             lines[i], "(?:FindResource|TryFindResource)\\(\"([^\"]+)\"\\)"))
                {
                    used[m.Groups[1].Value] = $"{name}:{i + 1}";
                }
            }
        }

        var missing = used.Keys.Where(k => !defined.Contains(k))
            .Select(k => $"{k}（{used[k]}）")
            .ToList();
        Check($"代码里 FindResource 用的 {used.Count} 个 key 都在 *.xaml 里定义过",
            missing.Count == 0, string.Join("、", missing));

        // ③ 图标几何：`NavIcons` 里那张「导航 key → Icon.Xxx」的表是**字符串**，
        // 拼错一个字母不会崩 —— 只会静默变成"这一行没有图标"（`Create` 认不出就返回 null），
        // 而界面上少一个图标没人能一眼看出来。NavIcons.xaml 是生成物，
        // 表却是手写的，两边就是会错开，所以这里把表上的每一条都对一遍。
        var navIcons = Path.Combine(appDir, "Infrastructure", "NavIcons.cs");
        if (File.Exists(navIcons))
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(navIcons), "\"(Icon\\.[A-Za-z0-9]+)\""))
            {
                wanted.Add(m.Groups[1].Value);
            }
            var gone = wanted.Where(k => !defined.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Check($"NavIcons 表里的 {wanted.Count} 个图标在 NavIcons.xaml 里都有几何定义",
                gone.Count == 0, string.Join("、", gone));
        }
    }

    // 玩家头像（`PlayerAvatars`）

    /// <summary>`PlayerAvatars`：皮肤 → 头像。</summary>
    private static void CheckPlayerAvatars()
    {
        // 一、纯算术：UUID → 挑哪张默认皮肤
        // 两边异或成 0，挑第 0 张（Steve）
        Check("挑皮肤：高低 64 位相同的 UUID → 哈希 0 → steve",
            PlayerAvatars.JavaUuidHash("01234567-89ab-cdef-0123-456789abcdef") == 0
            && PlayerAvatars.DefaultSkinFor("01234567-89ab-cdef-0123-456789abcdef") == "steve");
        Check("挑皮肤：全 0 的 UUID → steve",
            PlayerAvatars.DefaultSkinFor("00000000-0000-0000-0000-000000000000") == "steve");

        // 最低位是 1 → 哈希 1 → 第 2 张（alex）
        Check("挑皮肤：低位为 1 的 UUID → 哈希 1 → alex",
            PlayerAvatars.JavaUuidHash("00000000-0000-0000-0000-000000000001") == 1
            && PlayerAvatars.DefaultSkinFor("00000000-0000-0000-0000-000000000001") == "alex");

        // ⚠️ 这条是**有符号 / 无符号**的分水岭：
        // hi = 0x8000000000000000 → 折成 int 是 -2147483648，
        // Java 的 floorMod(-2147483648, 9) = 7 → sunny；
        // 要是按无符号算（2147483648 % 9 = 2）会挑成 ari —— 完全是另一个人。
        Check("挑皮肤：负哈希要 floorMod 折回来（0x80000000 → sunny，不是 ari）",
            PlayerAvatars.JavaUuidHash("80000000-0000-0000-0000-000000000000") == int.MinValue
            && PlayerAvatars.DefaultSkinFor("80000000-0000-0000-0000-000000000000") == "sunny",
            PlayerAvatars.DefaultSkinFor("80000000-0000-0000-0000-000000000000"));

        // 带不带杠要算出同一个哈希（档案文件里两种写法都见过）
        Check("挑皮肤：带杠 / 不带杠 / 大写 的 UUID 结果一致",
            PlayerAvatars.JavaUuidHash("80000000-0000-0000-0000-000000000000")
            == PlayerAvatars.JavaUuidHash("80000000000000000000000000000000")
            && PlayerAvatars.JavaUuidHash("80000000-0000-0000-0000-000000000000")
            == PlayerAvatars.JavaUuidHash("80000000-0000-0000-0000-000000000000".ToUpperInvariant()));

        // 读不懂的 UUID 别抛：当成 0（= steve），档案文件是用户能手改的
        Check("挑皮肤：坏 UUID 不抛异常，退回 steve",
            PlayerAvatars.JavaUuidHash("not-a-uuid") == 0
            && PlayerAvatars.JavaUuidHash(null) == 0
            && PlayerAvatars.DefaultSkinFor("zzz") == "steve");

        // 哈希挑出来的**必须落在表里**，而且 9 张都得被挑到过（索引算错会永远少一张）
        var picked = Enumerable.Range(0, 200)
            .Select(i => PlayerAvatars.DefaultSkinFor(LaunchRules.OfflineUuid("Player" + i)))
            .ToList();
        Check($"挑皮肤：200 个离线档案覆盖全部 9 张（挑到 {picked.Distinct().Count()} 张）",
            picked.Distinct().Count() == PlayerAvatars.DefaultSkins.Count
            && picked.All(name => PlayerAvatars.DefaultSkins.Contains(name)));

        // 名字不合法就别去拼路径（档案名来自配置文件，指到别处就是越界读）
        Check("自定义皮肤：名字里带路径的分隔符 → 不认",
            PlayerAvatars.CustomSkinFile(Path.GetTempPath(), "../evil") is null
            && PlayerAvatars.CustomSkinFile(Path.GetTempPath(), @"..\evil") is null
            && PlayerAvatars.CustomSkinFile(Path.GetTempPath(), "a/b") is null
            && PlayerAvatars.CustomSkinFile(Path.GetTempPath(), "..") is null
            && PlayerAvatars.CustomSkinFile(Path.GetTempPath(), "") is null);

        // 二、真画图（要 STA 线程）
        var results = new List<(string Name, bool Ok, string? Detail)>();
        var render = new Thread(() => CheckPlayerAvatarRendering(results))
        {
            IsBackground = true,
            Name = "自检-头像渲染（STA）",
        };
        render.SetApartmentState(ApartmentState.STA);
        render.Start();
        render.Join();

        foreach (var (name, ok, detail) in results)
        {
            Check(name, ok, detail);
        }
    }

    /// <summary>头像渲染那几项（在 STA 线程上跑，结果丢回 results）。</summary>
    private static void CheckPlayerAvatarRendering(List<(string Name, bool Ok, string? Detail)> results)
    {
        // 1. 9 张内置皮肤都得能读出来、都能抠出脸
        var sizes = new List<string>();
        var allSized = true;
        foreach (var skin in PlayerAvatars.DefaultSkins)
        {
            if (PlayerAvatars.FromEmbedded(skin, 32, 1.0) is not { } avatar)
            {
                allSized = false;
                sizes.Add($"{skin}=null");
                continue;
            }
            if (avatar.PixelWidth != 32 || avatar.PixelHeight != 32)
            {
                allSized = false;
            }
            sizes.Add($"{skin}={avatar.PixelWidth}x{avatar.PixelHeight}");
        }
        results.Add(("内置 9 张皮肤都能读出来，抠出的头像 32×32", allSized, string.Join(" ", sizes)));

        // 2. 屏幕缩放要跟着放大（125% 的屏：8 像素一行要放大 5 倍 → 40 物理像素），
        // 但**逻辑尺寸**必须还是 32 —— 不然装进 32px 的圆形头像位里会被缩放：
        // 放大再缩小，像素画立刻糊成一团（而且 40 逻辑像素的内容塞进 40×40 的
        // 120 DPI 位图里会被裁掉右下角，这个坑真踩过）。
        var hd = PlayerAvatars.FromEmbedded("steve", 32, 1.25);
        var hdDip = hd is null ? 0 : hd.PixelWidth * 96.0 / hd.DpiX;
        results.Add(($"125% 缩放：40 物理像素 / {hdDip:0.#} 逻辑像素（逻辑尺寸必须还是 32）",
            hd is { PixelWidth: 40, PixelHeight: 40 } && Math.Abs(hdDip - 32) < 0.01,
            hd is null ? "null" : $"{hd.PixelWidth}x{hd.PixelHeight} @ {hd.DpiX:0}dpi = {hdDip:0.#}dip"));

        // 1.0 缩放时两者相等（32 物理 = 32 逻辑），界面里任何缩放级别都不该出现"尺寸跑掉"
        if (PlayerAvatars.FromEmbedded("steve", 32, 1.0) is { } plain)
        {
            var plainDip = plain.PixelWidth * 96.0 / plain.DpiX;
            results.Add(($"100% 缩放：32 物理像素 / {plainDip:0.#} 逻辑像素",
                plain is { PixelWidth: 32, PixelHeight: 32 } && Math.Abs(plainDip - 32) < 0.01,
                $"{plain.PixelWidth}x{plain.PixelHeight} @ {plain.DpiX:0}dpi"));
        }

        // ⚠️ 圆角必须靠外层裁剪；判“是圆”用不透明像素占比（正圆约 78.5%，方形 100%），不要写死坐标。
        if (PlayerAvatars.FromEmbedded("steve", 32, 1.0) is { } steve)
        {
            var center = PixelAt(steve, 16, 16);
            var ratio = OpaqueRatio(steve);
            var cornersEmpty = PixelAt(steve, 0, 0).A == 0
                               && PixelAt(steve, 31, 0).A == 0
                               && PixelAt(steve, 0, 31).A == 0
                               && PixelAt(steve, 31, 31).A == 0;
            results.Add(("脸画上了（正中不透明且有色）",
                center.A == 255 && (center.R + center.G + center.B) > 0,
                $"中心 BGRA=({center.B},{center.G},{center.R},{center.A})"));
            results.Add(($"圆角裁成了正圆（不透明像素 {ratio:P1}，正圆约 78.5%；四角是空的）",
                cornersEmpty && ratio is > 0.72 and < 0.87, $"四角空={cornersEmpty}"));
        }
        else
        {
            results.Add(("内置皮肤 steve 读不出来", false, null));
        }

        // 4. 帽子层要**叠在脸上**（Noor 的头巾、Ari 的头发都在第二层）。
        // 用合成皮肤验：脸涂红、帽子层涂绿 → 抠出来正中必须是绿的。
        var withHat = PlayerAvatars.ComposeFace(
            MakeSkin(64, 64, face: (0, 0, 255), hat: (0, 255, 0)), 32, 1.0);
        if (withHat is not null)
        {
            var center = PixelAt(withHat, 16, 16);
            results.Add(("帽子层叠在脸上（合成皮肤：脸红 + 帽子绿 → 中心是绿）",
                center.A == 255 && center.G > 200 && center.R < 60,
                $"中心 BGRA=({center.B},{center.G},{center.R},{center.A})"));
        }
        else
        {
            results.Add(("合成皮肤抠脸失败", false, null));
        }

        // 5. 老式 64×32 皮肤**没有帽子层**：硬抠 (40,8) 会越界抛异常 → 整张头像没了。
        var oldSkin = PlayerAvatars.ComposeFace(
            MakeSkin(64, 32, face: (0, 0, 255), hat: null), 32, 1.0);
        if (oldSkin is not null)
        {
            var center = PixelAt(oldSkin, 16, 16);
            results.Add(("老式 64×32 皮肤（没帽子层）也能出图，脸本身是红的",
                center.A == 255 && center.R > 200 && center.G < 60,
                $"中心 BGRA=({center.B},{center.G},{center.R},{center.A})"));
        }
        else
        {
            results.Add(("老式 64×32 皮肤抠脸失败（越界）", false, null));
        }

        // 6. 配置目录里的自定义皮肤优先于内置那 9 张
        var tempRoot = Path.Combine(Path.GetTempPath(), "mosslight-avatar-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayerAvatars.EnsureSkinDir(tempRoot);
            var skinDir = Path.Combine(tempRoot, PlayerAvatars.CustomSkinDirName);
            results.Add(("EnsureSkinDir：目录建出来了", Directory.Exists(skinDir), skinDir));
            results.Add(("EnsureSkinDir：说明文件也放了",
                File.Exists(Path.Combine(skinDir, PlayerAvatars.ReadmeName)), null));

            // 用户改过说明之后**不能**再被覆盖掉
            var readme = Path.Combine(skinDir, PlayerAvatars.ReadmeName);
            File.WriteAllText(readme, "用户自己改的");
            PlayerAvatars.EnsureSkinDir(tempRoot);
            results.Add(("EnsureSkinDir：不覆盖用户改过的说明",
                File.ReadAllText(readme) == "用户自己改的", null));

            // 「Player」这个离线档案本来挑的是某张默认皮肤，放了自定义皮肤就得用它
            var account = new Account("Player", AccountKind.Offline);
            var beforeSkin = PlayerAvatars.DefaultSkinFor(LaunchRules.OfflineUuid("Player"));
            SavePng(MakeSkin(64, 64, face: (255, 0, 255), hat: null),   // 脸涂品红
                Path.Combine(skinDir, "Player.png"));
            var custom = PlayerAvatars.Avatar(account, tempRoot, 32, 1.0);
            var customCenter = custom is null ? default : PixelAt(custom, 16, 16);
            results.Add(($"自定义皮肤优先（档案 Player 默认该是 {beforeSkin}，放了自己那张就用品红）",
                custom is not null && customCenter.A == 255
                && customCenter.R > 200 && customCenter.B > 200 && customCenter.G < 60,
                custom is null ? "null" : $"中心 BGRA=({customCenter.B},{customCenter.G},{customCenter.R},{customCenter.A})"));

            // 没放自定义皮肤的档案不能跟着变（别把「优先」做成「全用同一张」）
            var other = PlayerAvatars.Avatar(new Account("Other", AccountKind.Offline), tempRoot, 32, 1.0);
            var otherCenter = other is null ? default : PixelAt(other, 16, 16);
            results.Add(("没放自定义皮肤的档案不受影响（不是同一张图）",
                other is not null && !(otherCenter.R > 200 && otherCenter.B > 200 && otherCenter.G < 60),
                other is null ? "null" : $"中心 BGRA=({otherCenter.B},{otherCenter.G},{otherCenter.R},{otherCenter.A})"));

            // 皮肤文件坏了（不是 png）→ 退回默认皮肤，别让界面空一块
            File.WriteAllBytes(Path.Combine(skinDir, "Broken.png"), new byte[] { 1, 2, 3, 4 });
            var broken = PlayerAvatars.Avatar(new Account("Broken", AccountKind.Offline), tempRoot, 32, 1.0);
            results.Add(("坏掉的皮肤文件 → 退回默认皮肤（不抛、不空）", broken is not null,
                broken is null ? "null" : $"{broken.PixelWidth}x{broken.PixelHeight}"));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响结论
            }
        }
    }

    /// <summary>读一张位图某个像素（BGRA；预乘格式读出来就是预乘值，只用来比不透明像素）。</summary>
    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4];
        // ⚠️ 这里用全名：`using System.Windows;` 会把 `System.Windows.ThemeMode`
        // （.NET 9 新加的）引进来，跟 `Mosslight.Core.Theme.ThemeMode` 撞名。
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return (bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    /// <summary>整张图里"不是全透明"的像素占多少（用来判它是不是个圆：正圆 ≈ 78.5%）。</summary>
    private static double OpaqueRatio(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var bytes = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(bytes, stride, 0);

        var opaque = 0;
        for (var i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] > 0)
            {
                opaque++;
            }
        }
        return opaque / (double)(bitmap.PixelWidth * bitmap.PixelHeight);
    }

    /// <summary>造一张测试皮肤：全透明，脸那 8×8 涂 face，给了 hat 就把帽子层那 8×8 也涂上。</summary>
    private static BitmapSource MakeSkin(int width, int height,
        (byte B, byte G, byte R) face, (byte B, byte G, byte R)? hat)
    {
        var pixels = new byte[width * height * 4];

        void Fill(int x0, int y0, (byte B, byte G, byte R) color)
        {
            for (var y = y0; y < y0 + 8; y++)
            {
                for (var x = x0; x < x0 + 8; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i] = color.B;
                    pixels[i + 1] = color.G;
                    pixels[i + 2] = color.R;
                    pixels[i + 3] = 255;
                }
            }
        }

        Fill(8, 8, face);
        if (width >= 64 && height >= 64 && hat is { } hatColor)
        {
            Fill(40, 8, hatColor);
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // 下载进度广播（`DownloadMonitor`）

    /// <summary>`DownloadMonitor`：下载窗口取数的那条路。</summary>
    private static void CheckDownloadMonitor()
    {
        DownloadMonitor.Reset();
        Check("进度广播：刚 Reset 完是空的", DownloadMonitor.Last is null,
            DownloadMonitor.Last is null ? "null" : "还有值");

        // 空管理器取一次快照也会广播（服务层就是这么一条条往外喂的）
        var manager = new DownloadManager(maxWorkers: 1, source: DownloadSourceMode.Official);
        var snapshot = manager.Snapshot();
        Check("进度广播：取一次快照就能被读到",
            ReferenceEquals(DownloadMonitor.Last, snapshot), "已广播");
        Check($"进度广播：空管理器 0 个任务（{DownloadMonitor.Last?.Count}）",
            DownloadMonitor.Last?.Count == 0);

        // 没有在跑的时候取消不该炸（窗口那颗按钮会调它）
        Check("进度广播：空着的时候取消不抛异常", !DownloadMonitor.CancelAll() || true);

        DownloadMonitor.Reset();
        Check("进度广播：Reset 之后又空了", DownloadMonitor.Last is null);
    }

    // 详情页的版本分组（`ModVersionGroups`）

    /// <summary>`ModVersionGroups.Group`：把项目的版本分成「游戏版本 × 加载器 × 正式/预览」。</summary>
    private static void CheckModVersionGroups()
    {
        // 造几个版本：1.21.5 / 1.20.1 两个正式版（fabric+quilt 双加载器），
        // 1.20.1 一个预览版，一个没有 loaders 字段的怪东西，再加一个更老的 1.9
        static JsonObject Version(string type, string date, string[] games, string[]? loaders)
        {
            var node = new JsonObject
            {
                ["version_type"] = type,
                ["date_published"] = date,
                ["game_versions"] = new JsonArray(games.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
            };
            if (loaders is not null)
            {
                node["loaders"] = new JsonArray(
                    loaders.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            }
            return node;
        }

        var versions = new[]
        {
            Version("release", "2026-01-01", new[] { "1.21.5" }, new[] { "fabric", "quilt" }),
            Version("release", "2026-01-02", new[] { "1.20.1" }, new[] { "fabric" }),
            Version("beta", "2026-01-03", new[] { "1.20.1" }, new[] { "fabric" }),
            Version("release", "2026-01-04", new[] { "1.9" }, new[] { "fabric" }),
            Version("release", "2026-01-05", new[] { "1.20.1" }, loaders: null),
        };

        var groups = ModVersionGroups.Group(versions, "1.20.1", "fabric");

        // 一对多：1.21.5 那个版本同时支持 fabric 和 quilt → 两个组
        Check("版本分组：一个版本支持两家加载器就进两个组",
            groups.Count == 6, $"{groups.Count} 组");

        // 匹配的排最前，而且 1.20.1+fabric 是**唯一**对得上（加载器也要对）
        Check("版本分组：匹配的分组排第一",
            groups[0].MatchesFilter && groups[0].GameVersion == "1.20.1" && groups[0].Loader == "fabric",
            $"{groups[0].GameVersion} {groups[0].Loader}");
        // ⚠️ 正式版和预览版**都算**对得上：
        // 用户筛的是"1.20.1 + fabric"，那两版都符合，都得置顶
        Check("版本分组：1.20.1 + fabric 的正式版和预览版都算对得上",
            groups.Count(g => g.MatchesFilter) == 2, $"{groups.Count(g => g.MatchesFilter)} 组");

        // 游戏版本新的在前：1.20.1 的那几组要在 1.9 前面（按数字比，不是字符串）
        var allGroups = groups.ToList();
        var idx120 = allGroups.FindLastIndex(g => g.GameVersion == "1.20.1");
        var idx19 = allGroups.FindIndex(g => g.GameVersion == "1.9");
        Check("版本分组：1.20.1 排在 1.9 前面（按数字比，不是字符串）",
            idx120 >= 0 && idx19 > idx120, $"最后一个 1.20.1@{idx120} vs 1.9@{idx19}");

        // 正式版在预览版前面（同一个 1.20.1 + fabric 下）
        var sameGroup = groups.Where(g => g.GameVersion == "1.20.1" && g.Loader == "fabric").ToList();
        Check("版本分组：正式版和预览版分成两组",
            sameGroup.Count == 2 && sameGroup.Count(g => g.Prerelease) == 1, $"{sameGroup.Count} 组");
        var idxRelease = allGroups.FindIndex(g => g.GameVersion == "1.20.1" && g.Loader == "fabric" && !g.Prerelease);
        var idxBeta = allGroups.FindIndex(g => g.GameVersion == "1.20.1" && g.Loader == "fabric" && g.Prerelease);
        Check("版本分组：正式版排在预览版前面", idxRelease < idxBeta, $"{idxRelease} < {idxBeta}");

        // 没有 loaders 字段的版本也要成组（不然那些版本在界面上永远看不到）
        Check("版本分组：没有 loaders 字段的版本也成组（加载器为空）",
            groups.Any(g => g.GameVersion == "1.20.1" && g.Loader.Length == 0));

        // 组内新的在前
        var betaGroup = groups.First(g => g.Prerelease);
        Check("版本分组：组内按发布日期新的在前",
            betaGroup.Versions.Count == 1, $"{betaGroup.Versions.Count} 个");

        Check("版本分组：标题是 `Fabric 1.20.1`",
            ModVersionGroups.Title(groups[0]) == "Fabric 1.20.1", ModVersionGroups.Title(groups[0]));
        Check("版本分组：预览版标题用接口原本的类型 `beta`（不翻译）",
            ModVersionGroups.Title(betaGroup) == "Fabric 1.20.1 beta", ModVersionGroups.Title(betaGroup));
        Check("版本分组：没有加载器时标题只有版本号",
            ModVersionGroups.Title(groups.First(g => g.Loader.Length == 0)) == "1.20.1",
            ModVersionGroups.Title(groups.First(g => g.Loader.Length == 0)));

        // 「全部」= 没筛：一个都不标匹配（这样就没有组会默认展开）
        var any = ModVersionGroups.Group(versions, "全部", "全部");
        Check("版本分组：「全部」等于没筛（没有组标匹配）",
            any.Count == 6 && any.Count(g => g.MatchesFilter) == 0, $"{any.Count} 组");

        // 空输入不炸
        Check("版本分组：空列表返回 0 组",
            ModVersionGroups.Group(Array.Empty<JsonObject>()).Count == 0);

        // 资源包 / 光影包：**不分加载器**
        // ⚠️ 资源包 / 光影包不按加载器拆分，否则同一批版本会列成两套。
        var merged = ModVersionGroups.Group(versions, "", "", splitByLoader: false);
        // 6 → 4：1.21.5 的 fabric / quilt 合成一组，1.20.1 的两个正式版
        // （一个 fabric、一个没写 loaders）也并进同一组
        Check($"版本分组：不分加载器时组数变少（{groups.Count} → {merged.Count}）",
            merged.Count == 4, $"{merged.Count} 组");
        Check("版本分组：不分加载器时 1.21.5 只有一组（fabric / quilt 合成一个）",
            merged.Count(g => g.GameVersion == "1.21.5") == 1);
        Check("版本分组：不分加载器时标题只有版本号",
            ModVersionGroups.Title(merged.First(g => g.GameVersion == "1.21.5")) == "1.21.5",
            ModVersionGroups.Title(merged.First(g => g.GameVersion == "1.21.5")));
    }

    /// <summary>`PackArchive.Detect`：一个 zip 到底该按哪种整合包装。</summary>
    private static void CheckPackArchive()
    {
        // 1. Modrinth（`.mrpack`）
        var mrpack = BuildTestMrpack("自检包", new Dictionary<string, string>
        {
            ["overrides/config/test.txt"] = "hello",
        });
        var modrinth = PackArchive.Detect(mrpack);
        Check("拖入识别：.mrpack 认成 Modrinth",
            modrinth.Kind == PackKind.Modrinth, modrinth.Kind.ToString());
        Check($"拖入识别：.mrpack 带得出包名（{modrinth.Name}）", modrinth.Name == "自检包");
        Check($"拖入识别：.mrpack 的覆盖层数对（{modrinth.OverlayCount}）", modrinth.OverlayCount == 1);

        // 2. CurseForge（`manifest.json`）
        var curse = BuildPlainZip(new Dictionary<string, string>
        {
            ["manifest.json"] = new JsonObject
            {
                ["manifestType"] = "minecraftModpack",
                ["manifestVersion"] = 1,
                ["name"] = "CF 包",
                ["version"] = "1.0.0",
                ["minecraft"] = new JsonObject
                {
                    ["version"] = "1.21.1",
                    ["modLoaders"] = new JsonArray(
                        new JsonObject { ["id"] = "neoforge-21.1.100", ["primary"] = true }),
                },
                ["files"] = new JsonArray(
                    new JsonObject { ["projectID"] = 1, ["fileID"] = 2, ["required"] = true }),
            }.ToJsonString(),
            ["overrides/config/a.cfg"] = "x",
        });
        var curseForge = PackArchive.Detect(curse);
        Check("拖入识别：CurseForge 清单认得出",
            curseForge.Kind == PackKind.CurseForge, curseForge.Kind.ToString());
        Check($"拖入识别：CurseForge 的版本 / 加载器拆得对"
              + $"（{curseForge.Minecraft} / {curseForge.Loader} {curseForge.LoaderVersion}）",
            curseForge.Minecraft == "1.21.1"
            && curseForge.Loader == "neoforge"
            && curseForge.LoaderVersion == "21.1.100");
        Check($"拖入识别：CurseForge 的 mod 数对（{curseForge.FileCount}）",
            curseForge.FileCount == 1);

        // ⚠️ 别的启动器也有 manifest.json：manifestType 不是 minecraftModpack 就不算
        var fakeManifest = BuildPlainZip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{\"manifestType\":\"otherLauncher\",\"name\":\"x\"}",
            ["mods/a.jar"] = "a",
        });
        Check("拖入识别：manifestType 不对的不算 CurseForge",
            PackArchive.Detect(fakeManifest).Kind == PackKind.Overlay);

        // 3. 覆盖包（根上直接是 mods / config）
        var overlay = BuildPlainZip(new Dictionary<string, string>
        {
            ["mods/a.jar"] = "a",
            ["config/b.txt"] = "b",
            ["readme.txt"] = "hi",
            ["unknown/c.txt"] = "c",
        });
        var overlayPack = PackArchive.Detect(overlay);
        Check("拖入识别：没清单但有 mods → 覆盖包",
            overlayPack.Kind == PackKind.Overlay, overlayPack.Kind.ToString());
        // readme.txt（根上的散文件）和 unknown/（不认识的目录）都不该算进去
        Check($"拖入识别：覆盖包只数认得的目录（{overlayPack.OverlayCount}）",
            overlayPack.OverlayCount == 2);

        // 4. 覆盖包真解压一遍
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "overlay");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
            Directory.CreateDirectory(scratch);

            var plan = PackArchive.OverlayPlan(overlay, "覆盖包");
            var applied = ModpackInstaller.ApplyOverrides(overlay, plan, scratch);
            Check($"覆盖包展开：写了 {applied.Written} 个文件", applied.Written == 2);
            Check("覆盖包展开：mods/a.jar 真的落了盘",
                File.Exists(Path.Combine(scratch, "mods", "a.jar")));
            Check("覆盖包展开：config/b.txt 真的落了盘",
                File.Exists(Path.Combine(scratch, "config", "b.txt")));
            Check("覆盖包展开：readme.txt 没被乱放（根上的散文件不展开）",
                !File.Exists(Path.Combine(scratch, "readme.txt")));
            Check($"覆盖包展开：报错是空的（{applied.Errors.Count}）", applied.Errors.Count == 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无所谓
            }
        }

        // 5. 不该认出来的那几种
        Check("拖入识别：没清单也没目录的 zip 不认",
            PackArchive.Detect(BuildPlainZip(new Dictionary<string, string>
            {
                ["foo/bar.txt"] = "x",
            })).Kind == PackKind.Unknown);
        Check("拖入识别：不是 zip 的字节流不认",
            PackArchive.Detect(new byte[] { 1, 2, 3, 4 }).Kind == PackKind.Unknown);
        Check("拖入识别：空文件不认", PackArchive.Detect(Array.Empty<byte>()).Kind == PackKind.Unknown);

        // 6. 带 `../` 的条目要被挡掉
        var evil = BuildPlainZip(new Dictionary<string, string>
        {
            ["mods/../../evil.txt"] = "x",
            ["mods/ok.jar"] = "y",
        });
        var evilSummary = PackArchive.Detect(evil);
        Check($"拖入识别：带 ../ 的条目不算数（只剩 {evilSummary.OverlayCount} 个）",
            evilSummary.OverlayCount == 1);
    }

    // 整合包

    /// <summary>整合包解析跟 Python 版逐值比；⚠️ 这一节里**最要紧的是路径安全**：整合包是用户从网上下回来的 ZIP，里面 `files[].path` 和 `overrides/` 的条目名完全由打包的人说了算。</summary>
    private static void CheckModpack(string themeGoldenPath)
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(themeGoldenPath))!;
        var goldenPath = Path.Combine(testsDir, "launch_golden.json");
        var mrpackPath = Path.Combine(testsDir, "fixtures", "test.mrpack");

        if (!File.Exists(goldenPath))
        {
            Check("金标准文件在", false, $"找不到 {goldenPath}");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        if (!doc.RootElement.TryGetProperty("modpack", out var mp))
        {
            Check("金标准里有 modpack 一节", false, "先生成：python tools\\gen_launch_golden.py");
            return;
        }

        // 名字消毒
        foreach (var (name, want) in mp.GetProperty("sanitize").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"名字消毒「{name}」", want, ModpackInstaller.SanitizeInstanceName(name));
        }
        // ⚠️ 单独点名几条：它们是"直接拿去建目录会失败"的现场
        Equal("`CON` 是 Windows 保留名，要加前缀", "_CON",
            ModpackInstaller.SanitizeInstanceName("CON"));
        Equal("`con.txt` 也不行（比的是前缀）", "_con.txt",
            ModpackInstaller.SanitizeInstanceName("con.txt"));
        Equal("单个连字符是名字的一部分，不能被收掉",
            "Better MC [FABRIC] - BMC2",
            ModpackInstaller.SanitizeInstanceName("Better MC [FABRIC] - BMC2"));
        Equal("结尾的点要去掉（`abc.` 在 Windows 上非法）", "trailing",
            ModpackInstaller.SanitizeInstanceName("trailing..."));
        Equal("全是空的就用兜底名", "modpack",
            ModpackInstaller.SanitizeInstanceName("   "));
        Equal($"超长的截到 {ModpackInstaller.MaxInstanceName}",
            ModpackInstaller.MaxInstanceName.ToString(),
            ModpackInstaller.SanitizeInstanceName(new string('a', 200)).Length.ToString());

        // 路径安全（Zip Slip 的现场）
        foreach (var (path, want) in mp.GetProperty("safe_relpath").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            Equal($"路径安全「{path}」", want.ToString(), ModpackInstaller.IsSafeRelPath(path).ToString());
        }
        foreach (var bad in new[]
                 {
                     "../evil.txt", "mods/../../evil.txt", "/etc/passwd",
                     "\\windows\\x", "C:/Windows/x.dll", "c:\\x", "a\0b",
                 })
        {
            Check($"挡得住不安全路径「{bad}」", !ModpackInstaller.IsSafeRelPath(bad));
        }
        // ⚠️ 反过来也要验：`. /` 和 `//` 是**正常**的相对路径
        // （PurePosixPath 会把它们规范化掉），拒了就是误伤
        foreach (var ok in new[] { "mods/sodium.jar", "a/./b", "a//b", "./a" })
        {
            Check($"不误伤正常路径「{ok}」", ModpackInstaller.IsSafeRelPath(ok));
        }
        // 双保险那一层：拼出目录外的要抛，而不是悄悄写出去
        var outsideThrew = false;
        try
        {
            _ = ModpackInstaller.JoinUnder(Path.GetTempPath(), "../evil.txt");
        }
        catch (ModpackException)
        {
            outsideThrew = true;
        }
        Check("JoinUnder 对越界路径会抛（不是悄悄写出去）", outsideThrew);

        // 找 index / 找覆盖层
        var idx = mp.GetProperty("find_index");
        var fakeRoot = new[] { "modrinth.index.json", "overrides/x" };
        var fakeNested = new[] { "pack/modrinth.index.json", "pack/overrides/x" };
        var fakeNone = new[] { "a.txt" };
        var fakeBoth = new[] { "modrinth.index.json", "p/modrinth.index.json" };

        foreach (var (caseName, names, want) in new (string, string[], JsonElement)[]
                 {
                     ("root", fakeRoot, idx.GetProperty("root")),
                     ("nested", fakeNested, idx.GetProperty("nested")),
                     ("none", fakeNone, idx.GetProperty("none")),
                     ("both", fakeBoth, idx.GetProperty("both")),
                 })
        {
            var (entry, baseDir) = ModpackInstaller.FindIndex(names);
            Equal($"找 index（{caseName}）",
                string.Join("|", want.EnumerateArray().Select(x => x.GetString())),
                $"{entry}|{baseDir}");
        }
        // ⚠️ 外面套一层文件夹也要认（用户重打包过就会这样）——
        // 只认根目录的话这类包会直接报「这不是整合包」
        Equal("index 在子目录里也认得出来", "pack/modrinth.index.json",
            ModpackInstaller.FindIndex(fakeNested).Entry);

        var zipNames = new[]
        {
            "modrinth.index.json", "overrides/config/a.json", "overrides/mods/x.jar",
            "overrides/", "overrides/../evil.txt",
            "client-overrides/config/a.json", "client-overrides/", "other/file.txt",
        };
        Equal("覆盖层清单（顺序固定：overrides 在前、client-overrides 在后）",
            string.Join("|", mp.GetProperty("find_overrides_root").EnumerateArray()
                .Select(x => $"{x[0].GetString()}/{x[1].GetString()}")),
            string.Join("|", ModpackInstaller.FindOverrides(zipNames)
                .Select(o => $"{o.Prefix}/{o.Relative}")));
        Check("覆盖层里那条不安全的路径被跳过了",
            ModpackInstaller.FindOverrides(zipNames).All(o => o.Relative != "../evil.txt"));

        // 读一个真的 .mrpack
        if (!File.Exists(mrpackPath))
        {
            Check("有一个真的 .mrpack fixture", false, $"找不到 {mrpackPath}");
            return;
        }

        var blob = File.ReadAllBytes(mrpackPath);
        // 金标准里存了同一份字节的 base64 —— 比对一下，确认两边读的是同一个包
        var wantB64 = mp.GetProperty("mrpack").GetProperty("bytes_b64").GetString()!;
        Equal("两边读的是同一份 .mrpack 字节",
            wantB64, Convert.ToBase64String(blob));

        var plan = ModpackInstaller.PlanFromBytes(blob);
        var wantPlan = mp.GetProperty("plan");

        Equal("整合包名字", wantPlan.GetProperty("name").GetString()!, plan.Name);
        Equal("整合包版本号", wantPlan.GetProperty("version_id").GetString()!, plan.VersionId);
        Equal("整合包摘要", wantPlan.GetProperty("summary").GetString()!, plan.Summary);
        Equal("formatVersion",
            wantPlan.GetProperty("format_version").GetInt32().ToString(), plan.FormatVersion.ToString());
        Equal("要装的 Minecraft 版本",
            wantPlan.GetProperty("minecraft").GetString()!, plan.Minecraft);

        var wantLoader = wantPlan.GetProperty("loader").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        var (loaderKey, loaderVersion) = plan.Loader;
        Equal("依赖里的加载器（`fabric-loader` → `fabric`）",
            string.Join("|", wantLoader), $"{loaderKey}|{loaderVersion}");

        Equal("客户端要装的条数（unsupported / 不安全的 / 重复的 / 没 url 的都排掉）",
            wantPlan.GetProperty("files").GetArrayLength().ToString(), plan.Files.Count.ToString());
        Equal("被 env 过滤掉的条数（服务端专用）",
            wantPlan.GetProperty("skipped").GetInt32().ToString(), plan.Skipped.ToString());
        Equal("总大小",
            wantPlan.GetProperty("total_bytes").GetInt64().ToString(), plan.TotalBytes.ToString());
        Equal("覆盖层清单",
            string.Join("|", wantPlan.GetProperty("overrides").EnumerateArray()
                .Select(x => $"{x[0].GetString()}/{x[1].GetString()}")),
            string.Join("|", plan.Overrides.Select(o => $"{o.Prefix}/{o.Relative}")));
        Equal("warning 条数与内容",
            string.Join("|", wantPlan.GetProperty("warnings").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", plan.Warnings));

        // 逐条比文件清单
        var wantFiles = wantPlan.GetProperty("files").EnumerateArray().ToList();
        var fileDiffs = new List<string>();
        for (var i = 0; i < Math.Min(wantFiles.Count, plan.Files.Count); i++)
        {
            var w = wantFiles[i];
            var a = plan.Files[i];
            if (w.GetProperty("path").GetString() != a.Path) fileDiffs.Add($"#{i}.path");
            if (w.GetProperty("url").GetString() != a.Url) fileDiffs.Add($"#{i}.url");
            if (w.GetProperty("sha1").GetString() != a.Sha1) fileDiffs.Add($"#{i}.sha1");
            if (w.GetProperty("size").GetInt64().ToString() != a.Size.ToString()) fileDiffs.Add($"#{i}.size");
            if (w.GetProperty("client").GetString() != a.Client) fileDiffs.Add($"#{i}.client");
        }
        Check("文件清单逐条一致（path / url / sha1 / size / client）", fileDiffs.Count == 0,
            fileDiffs.Count == 0 ? null : string.Join("；", fileDiffs));

        // ⚠️ optional 要**当成要装**（整合包作者标 optional 通常是"可选但推荐"）；
        // unsupported 是服务端专用，客户端不该装
        Check("optional 的文件照装（不跳过）",
            plan.Files.Any(f => f.Path == "mods/optional.jar"));
        Check("unsupported 的文件被跳过（服务端专用）",
            plan.Files.All(f => f.Path != "mods/serveronly.jar"));

        // 展开覆盖层
        var instanceDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "mrpack");
        try
        {
            if (Directory.Exists(instanceDir))
            {
                Directory.Delete(instanceDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }
        Directory.CreateDirectory(instanceDir);

        var result = ModpackInstaller.ApplyOverrides(blob, plan, instanceDir);
        Check($"覆盖层展开了 {result.Written} 个文件、没有错误", result.Errors.Count == 0,
            result.Errors.Count == 0 ? null : string.Join("；", result.Errors));

        // ⚠️ 覆盖**顺序**：client-overrides 要盖住 overrides 里同名那个
        var settingsPath = Path.Combine(instanceDir, "config", "settings.json");
        Check("client-overrides 盖住了 overrides 里的同名文件",
            File.Exists(settingsPath) && File.ReadAllText(settingsPath).Contains("client"),
            File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "<文件不在>");
        Check("overrides 里另一个文件也展开了",
            File.Exists(Path.Combine(instanceDir, "mods", "extra.jar")));
        // 不安全的条目**一个字节都不能落出去**
        Check("不安全的覆盖层条目没被写出来",
            !File.Exists(Path.Combine(Path.GetDirectoryName(instanceDir)!, "evil.txt")));

        try
        {
            Directory.Delete(instanceDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    // 补全缺失的库 + mod 目录反查

    private static void CheckRepairAndModsDir(string themeGoldenPath)
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(themeGoldenPath))!;
        var goldenPath = Path.Combine(testsDir, "launch_golden.json");
        var mcDir = Path.Combine(testsDir, "fixtures", "mc");
        var modsMcDir = Path.Combine(testsDir, "fixtures", "mc-mods");

        if (!File.Exists(goldenPath) || !Directory.Exists(mcDir) || !Directory.Exists(modsMcDir))
        {
            Check("金标准和两个 fixture 都在", false,
                $"缺少 {goldenPath} / {mcDir} / {modsMcDir} —— 先生成：python tools\\gen_launch_golden.py");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var root = doc.RootElement;

        // repair：什么算"缺"
        if (root.TryGetProperty("repair", out var repair))
        {
            var merged = VersionJsonLoader.Load(mcDir, "1.20.1");
            var missing = GameRepair.MissingLibraries(mcDir, merged.Raw);

            // 注意：这里比的是 `<版本目录>/<id>.json` 里**原始**的 libraries，
            // 不是合并后的 —— 合并会把父版本的库也带进来，而补全只该管这一份。
            // 单层版本两者一样，但用 Raw 语义更清楚。
            var want = repair.GetProperty("missing").EnumerateArray().ToList();
            Equal("缺的库条数", want.Count.ToString(), missing.Count.ToString());

            var diffs = new List<string>();
            for (var i = 0; i < Math.Min(want.Count, missing.Count); i++)
            {
                var w = want[i];
                var a = missing[i];
                if (w.GetProperty("name").GetString() != a.Name) diffs.Add($"#{i}.name");
                if (w.GetProperty("path").GetString() != a.Path) diffs.Add($"#{i}.path");
                if (w.GetProperty("sha1").GetString() != a.Sha1) diffs.Add($"#{i}.sha1");
                if (w.GetProperty("size").GetInt64().ToString() != a.Size.ToString()) diffs.Add($"#{i}.size");
            }
            Check("缺的库逐条一致（name / path / sha1 / size）", diffs.Count == 0,
                diffs.Count == 0 ? null : string.Join("；", diffs));

            // ⚠️ 这三条是"不该算缺"的三种情况，逐条点名 —— 报错了用户会看到
            // 一堆"失败"，其实什么都不缺
            var names = missing.Select(m => m.Name).ToList();
            Check("本地已有的库不算缺（always）", !names.Contains("com.example:always:1.0"));
            Check("只有 natives、没有主 jar 的不算缺（nativesonly）",
                !names.Contains("com.example:nativesonly:2.0"));
            Check("rules 不匹配当前系统的不算缺（maconly / lastwins）",
                !names.Contains("com.example:maconly:3.0")
                && !names.Contains("com.example:lastwins:4.0"));
        }

        // mods：该装到哪个 mods 目录
        if (!root.TryGetProperty("mods_dir", out var modsDir))
        {
            Check("金标准里有 mods_dir 一节", false);
            return;
        }

        Equal("versions/ 下认得出版本文件夹（没有同名 json 的排除掉）",
            string.Join("|", modsDir.GetProperty("list_version_dirs").EnumerateArray()
                .Select(x => x.GetString())),
            string.Join("|", ModsDirectory.ListVersionDirs(modsMcDir)));

        foreach (var (name, want) in modsDir.GetProperty("split").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var expected = want.EnumerateArray().Select(x => x.GetString()!).ToList();
            var (head, tail) = ModsDirectory.SplitVersionFolder(name);
            Equal($"拆分「{name}」", string.Join("|", expected), $"{head}|{tail}");
        }

        foreach (var (tail, want) in modsDir.GetProperty("loader_token").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"抠加载器名「{tail}」", want, ModsDirectory.LoaderToken(tail));
        }

        foreach (var (key, want) in modsDir.GetProperty("match").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var parts = key.Split('|');
            var expectedFolder = want[0].ValueKind == JsonValueKind.Null ? null : want[0].GetString();
            var result = ModsDirectory.MatchVersionDir(modsMcDir, parts[0], parts.Length > 1 ? parts[1] : "");
            Equal($"反查「{key}」", expectedFolder ?? "<null>", result.Folder ?? "<null>");
        }

        // ⚠️ 单独把最要命的那条点名：找 forge **不能**命中 NeoForge 的文件夹
        // （`"neoforge".Contains("forge")` 是 true，写成 Contains 就中招）
        var forgeResult = ModsDirectory.MatchVersionDir(modsMcDir, "1.20.1", "forge");
        Check("找 forge 不会命中 NeoForge 的文件夹", forgeResult.Folder is null,
            $"实际匹配到 {forgeResult.Folder}");
        var neoResult = ModsDirectory.MatchVersionDir(modsMcDir, "1.20.1", "neoforge");
        Check("找 neoforge 命中 NeoForge 的文件夹",
            neoResult.Folder == "1.20.1-NeoForge_47.1.106", neoResult.Folder ?? "<null>");

        // 同一个游戏版本装了两份 Fabric → 取加载器版本号**最高**的
        var fabricResult = ModsDirectory.MatchVersionDir(modsMcDir, "1.20.1", "fabric");
        Check("两份 Fabric 取版本号最高的那个（0.19.10 而不是 0.19.5）",
            fabricResult.Folder == "1.20.1-Fabric 0.19.10", fabricResult.Folder ?? "<null>");

        foreach (var (key, want) in modsDir.GetProperty("mods_dir_for").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var parts = key.Split('|');
            var got = ModsDirectory.ModsDirFor(modsMcDir, parts[0], parts.Length > 1 ? parts[1] : "");
            Equal($"mods 目录「{key}」", want, Path.GetFileName(got));
        }

        // 存档目录（数据包的落点在这些下面）
        Equal("存档目录跟着版本隔离走（隔离版本 → versions/<名>/saves）",
            "saves",
            Path.GetFileName(ModsDirectory.SavesDirFor(modsMcDir, "1.20.1", "fabric")));
        Equal("存档目录：隔离版本的父目录就是那个版本目录",
            "1.20.1-Fabric 0.19.10",
            Path.GetFileName(Path.GetDirectoryName(ModsDirectory.SavesDirFor(modsMcDir, "1.20.1", "fabric"))!));
        Equal("存档目录：没有本地版本时退回游戏目录下的 saves",
            "saves",
            Path.GetFileName(ModsDirectory.SavesDirFor(modsMcDir, "", "")));

        // 列存档：只认目录，文件/"没有这个目录"都不算
        var savesProbe = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "saves-probe");
        try
        {
            Directory.CreateDirectory(Path.Combine(savesProbe, "New World"));
            Directory.CreateDirectory(Path.Combine(savesProbe, "另一个世界"));
            File.WriteAllText(Path.Combine(savesProbe, "不是存档.txt"), "x");
            var worlds = ModsDirectory.ListWorlds(savesProbe);
            Equal("列存档：两个目录都在（文件不算）", "2", worlds.Count.ToString());
            Check("列存档：按名字排序且认得出中文目录",
                worlds.Contains("New World") && worlds.Contains("另一个世界"),
                string.Join(" / ", worlds));
            Equal("列存档：目录不存在时返回空表", "0",
                ModsDirectory.ListWorlds(Path.Combine(savesProbe, "nope")).Count.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(savesProbe))
                {
                    Directory.Delete(savesProbe, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉不影响结论
            }
        }

        // 本地 mod 的启用 / 禁用 / 删除
        CheckLocalModsLifecycle();

        // 从 jar 里读名字 / 版本 / 加载器（Mod 管理列表用）
        CheckModMetadata();
    }

    /// <summary>本地 mod 的增删启停。</summary>
    private static void CheckLocalModsLifecycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "mods");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "sodium.jar"), "jar");
        File.WriteAllText(Path.Combine(dir, "lithium.jar.disabled"), "jar");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a mod");
        Directory.CreateDirectory(Path.Combine(dir, "1.20.1"));   // 按版本分的子目录

        var mods = ModsDirectory.ListMods(dir);
        Equal("只数第一层、只认 jar（.disabled 也算）", "2", mods.Count.ToString());
        Equal("按显示名排序", "lithium|sodium",
            string.Join("|", mods.Select(m => m.DisplayName)));
        Check("禁用状态认得出来",
            mods.First(m => m.DisplayName == "lithium").Enabled == false
            && mods.First(m => m.DisplayName == "sodium").Enabled);

        // 禁用 → 文件改名成 .disabled
        var sodium = mods.First(m => m.DisplayName == "sodium");
        var disabled = ModsDirectory.SetEnabled(sodium.Path, false);
        Check("禁用之后文件名带 .disabled",
            disabled is not null
            && Path.GetFileName(disabled) == "sodium.jar.disabled"
            && File.Exists(disabled));
        Equal("禁用之后列表里还是 2 个（不是消失）", "2", ModsDirectory.ListMods(dir).Count.ToString());

        // 再启用 → 改回来
        var enabled = ModsDirectory.SetEnabled(disabled!, true);
        Check("启用之后后缀去掉了",
            enabled is not null && Path.GetFileName(enabled) == "sodium.jar" && File.Exists(enabled));

        // 幂等：已经是想要的状态就原样返回，不报错
        var again = ModsDirectory.SetEnabled(enabled!, true);
        Equal("已经启用再点启用是幂等的", enabled ?? "<null>", again ?? "<null>");

        Check("删得掉", ModsDirectory.Delete(enabled!));
        Equal("删完只剩 1 个", "1", ModsDirectory.ListMods(dir).Count.ToString());
        Check("删不存在的文件返回 false 而不是抛异常",
            !ModsDirectory.Delete(Path.Combine(dir, "nope.jar")));

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    /// <summary>从 mod jar 里读名字 / 版本 / 加载器 / 作者（ModMetadata）。</summary>
    private static void CheckModMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "meta");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }
        Directory.CreateDirectory(dir);

        // 1×1 的合法 PNG（图标只要字节对就行，自检不解码）
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var fabric = Path.Combine(dir, "sodium-fabric-0.5.8.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   fabric, System.IO.Compression.ZipArchiveMode.Create))
        {
            // icon 写成字符串（fabric 最常见的写法）
            WriteZipEntry(zip, "fabric.mod.json",
                """{"id":"sodium","name":"Sodium","version":"0.5.8","authors":["jellysquid3","IMS212"],"icon":"assets/sodium/icon.png"}""");
            WriteZipBytes(zip, "assets/sodium/icon.png", png);
        }

        // icon 写成 {尺寸: 路径} 的表（fabric 的另一种写法）
        var fabricIconTable = Path.Combine(dir, "fabric-icon-table.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   fabricIconTable, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "fabric.mod.json",
                """{"id":"iconmod","name":"Icon Table","version":"1.0","icon":{"64":"icon64.png"}}""");
            WriteZipBytes(zip, "icon64.png", png);
        }

        // Forge 的 logoFile
        var forgeLogo = Path.Combine(dir, "forge-logo.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   forgeLogo, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "META-INF/mods.toml",
                "[[mods]]\nmodId=\"logomod\"\nversion=\"1.0\"\n"
                + "displayName=\"Logo Mod\"\nlogoFile=\"logo.png\"\n");
            WriteZipBytes(zip, "logo.png", png);
        }

        // 什么元数据都没有，但 jar 根上躺着一张 icon.png → 约定兜底
        var bareIcon = Path.Combine(dir, "bare-icon.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   bareIcon, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipBytes(zip, "icon.png", png);
        }

        var quilt = Path.Combine(dir, "quilt-thing.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   quilt, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "quilt.mod.json",
                """{"quilt_loader":{"id":"quilt_thing","version":"1.2","metadata":{"name":"Quilt Thing","contributors":{"alice":"Author"}}}}""");
        }

        var neo = Path.Combine(dir, "refined-neoforge-2.1.0.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   neo, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "META-INF/neoforge.mods.toml",
                "modLoader=\"javafml\"\n[[mods]]\nmodId=\"refinedstorage\"\n"
                + "version=\"${file.jarVersion}\"\ndisplayName=\"Refined Storage\"\n"
                + "authors=\"raoulvdberge\"\n");
            WriteZipEntry(zip, "META-INF/MANIFEST.MF",
                "Manifest-Version: 1.0\nImplementation-Version: 2.1.0\n");
        }

        // 没有 displayName 的 mods.toml：名字要退回 modId
        var forge = Path.Combine(dir, "jei-forge.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   forge, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "META-INF/mods.toml",
                "[[mods]]\nmodId=\"jei\"\nversion=\"15.2.0.27\"\n");
        }

        // 1.12 那套（而且是禁用态：.jar.disabled 照样要读得出来）
        var legacy = Path.Combine(dir, "appleskin-forge-1.7.2.jar.disabled");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   legacy, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "mcmod.info",
                """[{"modid":"appleskin","name":"AppleSkin","version":"1.7.2","authorList":["squeek502"]}]""");
        }

        // 只剩清单
        var manifestOnly = Path.Combine(dir, "manifest-only.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   manifestOnly, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "META-INF/MANIFEST.MF",
                "Manifest-Version: 1.0\nImplementation-Title: Cool Mod\n"
                + "Implementation-Version: 3.0\n");
        }

        // 什么都没有 → 退回文件名
        var bare = Path.Combine(dir, "some-mod-1.2.3.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(
                   bare, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "readme.txt", "no metadata here");
        }

        // 根本不是 zip → 不能抛
        var broken = Path.Combine(dir, "broken.jar");
        File.WriteAllText(broken, "这不是 zip");

        var f = ModMetadata.Read(fabric);
        Equal("fabric.mod.json 的名字", "Sodium", f.Name);
        Equal("fabric.mod.json 的版本", "0.5.8", f.Version);
        Equal("fabric.mod.json 的加载器", "Fabric", f.Loader);
        Equal("fabric.mod.json 的作者", "jellysquid3, IMS212", f.Authors);
        Check("fabric.mod.json 算读到了", f.FromJar);
        Check("fabric 的 icon 读到了（PNG 头对）",
            f.Icon is { Length: > 8 } && f.Icon[0] == 0x89 && f.Icon[1] == 0x50);
        Check("icon 写成 {尺寸: 路径} 的表也能读",
            ModMetadata.Read(fabricIconTable).Icon is { Length: > 0 });
        Check("Forge 的 logoFile 也能读",
            ModMetadata.Read(forgeLogo).Icon is { Length: > 0 });
        Check("元数据没写图标时按约定找 icon.png",
            ModMetadata.Read(bareIcon).Icon is { Length: > 0 });

        var q = ModMetadata.Read(quilt);
        Equal("quilt_loader 的名字", "Quilt Thing", q.Name);
        Equal("quilt_loader 的加载器", "Quilt", q.Loader);
        Equal("quilt 的 contributors 当作者", "alice", q.Authors);

        var n = ModMetadata.Read(neo);
        Equal("neoforge.mods.toml 的名字", "Refined Storage", n.Name);
        Equal("neoforge.mods.toml 的加载器", "NeoForge", n.Loader);
        Equal("${file.jarVersion} 用清单里的版本顶", "2.1.0", n.Version);
        Equal("neoforge.mods.toml 的作者", "raoulvdberge", n.Authors);

        var g = ModMetadata.Read(forge);
        Equal("mods.toml 没有 displayName 就用 modId", "jei", g.Name);
        Equal("mods.toml 认成 Forge", "Forge", g.Loader);
        Equal("mods.toml 的版本", "15.2.0.27", g.Version);

        var l = ModMetadata.Read(legacy);
        Equal("mcmod.info 的名字", "AppleSkin", l.Name);
        Equal("mcmod.info 的版本", "1.7.2", l.Version);
        Equal("mcmod.info 的作者（authorList）", "squeek502", l.Authors);
        Check("禁用态（.jar.disabled）照样读得出来", l.FromJar);

        var m = ModMetadata.Read(manifestOnly);
        Equal("只有清单时用 Implementation-Title", "Cool Mod", m.Name);
        Equal("只有清单时的版本", "3.0", m.Version);

        var b = ModMetadata.Read(bare);
        Equal("没有元数据就退回文件名", "some-mod-1.2.3", b.Name);
        Check("没有元数据时 FromJar 是 false", !b.FromJar);
        Check("jar 里没有图标时 Icon 是 null（界面画首字母）", b.Icon is null);

        var bad = ModMetadata.Read(broken);
        Equal("坏 jar 不抛异常、退回文件名", "broken", bad.Name);

        Equal("FileStem 先摘 .disabled 再摘 .jar", "appleskin-1.7.2",
            ModMetadata.FileStem(@"C:\x\appleskin-1.7.2.jar.disabled"));

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }

    // 版本设置 + 安装器金标准

    /// <summary>「按版本覆盖参数」和「安装器地址 / 输出节流」两块跟 Python 版逐值比。</summary>
    private static void CheckVersionSettingsAndInstaller(string themeGoldenPath)
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(themeGoldenPath))!;
        var goldenPath = Path.Combine(testsDir, "launch_golden.json");
        if (!File.Exists(goldenPath))
        {
            Check("金标准文件在", false, $"找不到 {goldenPath}");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        var root = doc.RootElement;

        // version_settings
        if (!root.TryGetProperty("version_settings", out var vs))
        {
            Check("金标准里有 version_settings 一节", false, "先生成：python tools\\gen_launch_golden.py");
            return;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "vs");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }
        Directory.CreateDirectory(scratch);

        var settings = new VersionSettings(scratch);
        settings.Update("1.20.1", new PerVersionSettings
        {
            // 前后空格的字符串、字符串形式的数字、空字符串、还有下面这个不认识的值
            JavaPath = "  C:\\java\\bin\\javaw.exe  ",
            MaxMemory = 4096,
            MinMemory = 8192,
            ExtraJvmArgs = "  -XX:+UseG1GC  ",
            ExtraGameArgs = "",     // 空 = 没设，不该存
            Icon = "",              // 空 = 没设，不该存
        });
        settings.Update("1.20.2", new PerVersionSettings());    // 空的 = 恢复成全部跟随默认

        Equal("被覆盖过的版本（空的 update 等于忘掉）",
            string.Join("|", vs.GetProperty("customized").EnumerateArray()
                .Select(x => x.GetString())),
            string.Join("|", settings.CustomizedVersions().OrderBy(x => x, StringComparer.Ordinal)));

        var stored = settings.Get("1.20.1");
        var wantStored = vs.GetProperty("stored_1_20_1");
        // ⚠️ 前后空格要去掉（用户手填的十有八九带空格）
        Equal("存的 java_path 去掉了前后空格",
            wantStored.GetProperty("java_path").GetString()!, stored.JavaPath ?? "<null>");
        Equal("字符串形式的 max_memory 被纠正成整数",
            wantStored.GetProperty("max_memory").GetInt32().ToString(), stored.MaxMemory?.ToString() ?? "<null>");
        Equal("空的项不存", vs.GetProperty("stored_1_20_2").EnumerateObject().Count().ToString(),
            settings.Get("1.20.2").IsEmpty ? "0" : "非空");

        foreach (var (field, want) in vs.GetProperty("is_custom").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            Equal($"is_custom（{field}）", want.ToString(), settings.IsCustom("1.20.1", field).ToString());
        }

        // ⚠️ 最小堆不能大于最大堆：默认 max 2048 + 版本自定义 min 8192
        // 不夹一下 JVM 会直接拒绝启动
        var wantResolved = vs.GetProperty("resolved");
        var resolved = settings.Resolve("1.20.1", new EffectiveSettings(
            JavaPath: "", MinMemory: 512, MaxMemory: 2048));
        Equal("合并：java_path 用版本覆盖的",
            wantResolved.GetProperty("java_path").GetString()!, resolved.JavaPath);
        Equal("合并：max 用版本覆盖的",
            wantResolved.GetProperty("max_memory").GetInt32().ToString(), resolved.MaxMemory.ToString());
        Equal("合并：min > max 时夹回 max（否则 JVM 拒绝启动）",
            wantResolved.GetProperty("min_memory").GetInt32().ToString(), resolved.MinMemory.ToString());

        var wantDefaults = vs.GetProperty("resolved_defaults_only");
        var defaults = settings.Resolve("9.9.9", new EffectiveSettings(
            JavaPath: "/usr/bin/java", MinMemory: 512, MaxMemory: 2048));
        Equal("没覆盖过的版本原样用默认",
            wantDefaults.GetProperty("java_path").GetString()!, defaults.JavaPath);
        Equal("没覆盖过的版本内存也用默认",
            wantDefaults.GetProperty("max_memory").GetInt32().ToString(), defaults.MaxMemory.ToString());

        // 落盘 → 重新读回来，值不丢（原子写 + 解析的一致性）
        var reloaded = new VersionSettings(scratch);
        var after = reloaded.Get("1.20.1");
        Equal("落盘再读回来：java_path 一样", stored.JavaPath ?? "<null>", after.JavaPath ?? "<null>");
        Equal("落盘再读回来：max_memory 一样", stored.MaxMemory?.ToString() ?? "<null>",
            after.MaxMemory?.ToString() ?? "<null>");
        Equal("落盘再读回来：min_memory 一样", stored.MinMemory?.ToString() ?? "<null>",
            after.MinMemory?.ToString() ?? "<null>");

        // loader_setup
        if (!root.TryGetProperty("loader_setup", out var ls))
        {
            Check("金标准里有 loader_setup 一节", false);
            return;
        }

        foreach (var (key, want) in ls.GetProperty("forge_maven").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var parts = key.Split('|');
            Equal($"Forge 官方 maven 地址（{key}）", want,
                LoaderInstaller.ForgeMavenUrl(parts[0], parts[1]));
        }

        var urls = ls.GetProperty("installer_url");
        Equal("Forge 安装器地址（BMCLAPI 的 /forge/download 接口）",
            urls.GetProperty("forge").GetString()!,
            LoaderInstaller.InstallerUrl("1.20.1", "forge", "47.4.0"));
        // ⚠️ 1.20.1 的 NeoForge 在 maven 里是 `net/neoforged/forge/1.20.1-47.1.106/...`
        // —— 产物版本号带着**游戏版本前缀**，直接拿 47.1.106 去问是 404
        Equal("NeoForge 1.20.1 的安装器地址（历史命名 + 带前缀的产物名）",
            urls.GetProperty("neoforge_1_20_1").GetString()!,
            LoaderInstaller.InstallerUrl("1.20.1", "neoforge", "47.1.106"));
        Equal("NeoForge 1.21 的安装器地址（新的 net/neoforged/neoforge）",
            urls.GetProperty("neoforge_1_21").GetString()!,
            LoaderInstaller.InstallerUrl("1.21", "neoforge", "21.0.167"));
        Equal("OptiFine 安装器地址（要用 type / patch 两段）",
            urls.GetProperty("optifine").GetString()!,
            LoaderInstaller.InstallerUrl("1.20.1", "optifine", "HD_U_I5", "HD_U", "I5"));

        foreach (var (key, want) in ls.GetProperty("installer_file_name").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var got = key switch
            {
                "forge" => LoaderInstaller.InstallerFileName("forge", "1.20.1", "47.4.0"),
                "neoforge" => LoaderInstaller.InstallerFileName("neoforge", "1.20.1", "47.1.106"),
                "optifine" => LoaderInstaller.InstallerFileName("optifine", "1.20.1", "HD_U_I5"),
                _ => "<未覆盖>",
            };
            Equal($"安装器文件名（{key}）", want, got);
        }

        foreach (var (key, want) in ls.GetProperty("installer_arguments").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var expected = want.EnumerateArray().Select(x => x.GetString()!).ToList();
            var got = LoaderInstaller.InstallerArguments(key, "/mc");
            Equal($"安装器参数（{key}）", string.Join("|", expected), string.Join("|", got));
        }

        foreach (var (text, want) in ls.GetProperty("looks_like_listing").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetBoolean())))
        {
            Equal($"刷屏判定「{text}」", want.ToString(), InstallerOutput.LooksLikeListing(text).ToString());
        }

        foreach (var (name, want) in ls.GetProperty("failure_hint").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var lines = name switch
            {
                "generic" => new List<string> { "Some real reason here", "There was an error during installation" },
                "all_spam" => new List<string> { "  Patching net/minecraft/A", "  com/x/Y.class", "There was an error during installation" },
                "empty" => new List<string>(),
                _ => new List<string>(),
            };
            // ⚠️ `all_spam` 那条是刻意的：裸路径**不算**"没用的话" ——
            // 真出错时最后一行很可能就是一个路径（找不到的那个文件），
            // 滤掉它等于把唯一的线索扔了
            Equal($"失败提示（{name}）", want, LoaderInstaller.FailureHint(lines));
        }
    }

    // 加载器目录金标准

    private static void CheckLoaderCatalog(string themeGoldenPath)
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(themeGoldenPath))!;
        var goldenPath = Path.Combine(testsDir, "launch_golden.json");
        if (!File.Exists(goldenPath))
        {
            Check("加载器金标准文件在", false, $"找不到 {goldenPath}");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(goldenPath));
        if (!doc.RootElement.TryGetProperty("loaders", out var g))
        {
            Check("金标准里有 loaders 一节", false, "先生成：python tools\\gen_launch_golden.py");
            return;
        }

        // ⚠️ 金标准按子集比：原五家的相对顺序必须保留，支持列表必须包含它们；后补的几家单独验。
        var goldenOrder = g.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToList();
        var ourFilteredOrder = LoaderCatalog.LoaderOrder.Where(goldenOrder.Contains).ToList();
        Equal("金标准那五家的相对顺序没变", string.Join("|", goldenOrder),
            string.Join("|", ourFilteredOrder));

        var goldenSupported = g.GetProperty("supported").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        var droppedSupported = goldenSupported.Where(k => !LoaderCatalog.Supported.Contains(k)).ToList();
        Check($"金标准里能装的 {goldenSupported.Count} 家现在还能装",
            droppedSupported.Count == 0, string.Join("、", droppedSupported));

        foreach (var (key, want) in g.GetProperty("names").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"显示名（{key}）", want, LoaderCatalog.LoaderNames[key]);
        }

        // ⚠️ 接口地址错一个字符就是 404，而"拉不到"在界面上会显示成网络问题，
        // 真正的原因（地址拼错）根本看不出来
        foreach (var (key, want) in g.GetProperty("urls").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"{key} 的接口地址", want, LoaderCatalog.UrlFor(key, "1.20.1"));
        }

        // 解析（同一份输入，两边各跑一遍）
        // ⚠️ InputKey 单独给：Quilt 跟 Fabric 的**回包形状一样**，
        // 金标准里就是拿同一份 fabric 输入跑出来的
        // 。
        var inputs = g.GetProperty("inputs");
        var cases = new (string Key, string InputKey, Func<JsonNode?, List<LoaderVersion>> Parse)[]
        {
            ("forge", "forge", LoaderCatalog.ParseForge),
            ("neoforge", "neoforge", LoaderCatalog.ParseNeoForge),
            ("fabric", "fabric", LoaderCatalog.ParseFabric),
            ("quilt", "fabric", LoaderCatalog.ParseFabric),
            ("optifine", "optifine", LoaderCatalog.ParseOptiFine),
        };

        foreach (var (key, inputKey, parse) in cases)
        {
            var input = JsonNode.Parse(inputs.GetProperty(inputKey).GetRawText());
            var got = parse(input);
            var want = g.GetProperty(key);
            Equal($"{key} 解析出来的条数",
                want.GetArrayLength().ToString(), got.Count.ToString());

            // 逐条比关键字段
            var wantList = want.EnumerateArray().ToList();
            var diffs = new List<string>();
            for (var i = 0; i < Math.Min(wantList.Count, got.Count); i++)
            {
                var w = wantList[i];
                var a = got[i];
                void Cmp(string field, string wv, string av)
                {
                    if (wv != av)
                    {
                        diffs.Add($"#{i}.{field} 期望 {wv} 实际 {av}");
                    }
                }
                Cmp("version", w.GetProperty("version").GetString()!, a.Version);
                Cmp("stable", w.GetProperty("stable").GetBoolean().ToString(), a.Stable.ToString());
                if (w.TryGetProperty("installer_hash", out var ih))
                {
                    Cmp("installer_hash",
                        ih.ValueKind == JsonValueKind.Null ? "<null>" : ih.GetString()!,
                        a.InstallerHash ?? "<null>");
                }
                if (w.TryGetProperty("filename", out var fn))
                {
                    Cmp("filename", fn.GetString()!, a.FileName ?? "<null>");
                }
                if (w.TryGetProperty("type", out var ty))
                {
                    Cmp("type", ty.GetString()!, a.Type ?? "<null>");
                }
                if (w.TryGetProperty("patch", out var pa))
                {
                    Cmp("patch", pa.GetString()!, a.Patch ?? "<null>");
                }
                if (w.TryGetProperty("forge", out var fo))
                {
                    Cmp("forge", fo.GetString()!, a.Forge ?? "<null>");
                }
            }
            Check($"{key} 逐条字段一致", diffs.Count == 0,
                diffs.Count == 0 ? null : string.Join("；", diffs.Take(4)));
        }

        // 回包形状不对必须是空列表，不是异常
        var garbage = g.GetProperty("garbage");
        Equal("回包是数字 → 空列表", garbage.GetProperty("number").GetArrayLength().ToString(),
            LoaderCatalog.ParseNeoForge(JsonNode.Parse("42")).Count.ToString());
        Equal("回包是 null → 空列表", garbage.GetProperty("none").GetArrayLength().ToString(),
            LoaderCatalog.ParseNeoForge(null).Count.ToString());
        Equal("回包是字符串 → 空列表", garbage.GetProperty("string").GetArrayLength().ToString(),
            LoaderCatalog.ParseForge(JsonNode.Parse("\"nope\"")).Count.ToString());
        Equal("回包是对象（非列表）→ 空列表",
            garbage.GetProperty("optifine_dict").GetArrayLength().ToString(),
            LoaderCatalog.ParseOptiFine(JsonNode.Parse("""{"a":1}""")).Count.ToString());
        // 有些镜像会把列表包一层
        Equal("回包被包了一层（{versions:[...]}）→ 照样解得出来",
            garbage.GetProperty("dict_unwrapped").GetArrayLength().ToString(),
            LoaderCatalog.ParseNeoForge(JsonNode.Parse(
                $$"""{"versions":{{inputs.GetProperty("neoforge").GetRawText()}}}""")).Count.ToString());

        // 版本号排序
        // 用金标准里那份**专门的**输入（不是上面 fabric 那份）：
        // `0.19.10 > 0.19.5` 和 `0.19.5 > 0.9.0` 正是字符串比会搞反的两处
        Equal("版本号按分段数字排（0.19.10 > 0.19.5 > 0.9.0）",
            string.Join("|", g.GetProperty("version_key_order").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", LoaderCatalog
                .ParseFabric(JsonNode.Parse(inputs.GetProperty("version_key_order").GetRawText()))
                .Select(v => v.Version)));

        // ⚠️ 文件夹名统一用下划线（PCL2 CE 约定）；比较金标准时只把空格归一成下划线，其余逐字比。
        static string Normalize(string text) => text.Replace(' ', '_');
        foreach (var (name, want) in g.GetProperty("folder_names").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            var got = name switch
            {
                "fabric" => LoaderCatalog.VersionFolderName("1.21.4", "fabric", "0.19.5"),
                "forge" => LoaderCatalog.VersionFolderName("1.12.2", "forge", "14.23.5.2864"),
                "neoforge" => LoaderCatalog.VersionFolderName("1.20.1", "neoforge", "47.1.5"),
                "quilt" => LoaderCatalog.VersionFolderName("1.20.1", "quilt", "0.20.0"),
                "optifine" => LoaderCatalog.VersionFolderName("1.20.1", "optifine", "HD_U_I5"),
                "vanilla" => LoaderCatalog.VersionFolderName("1.20.1", "", ""),
                "no_loader_version" => LoaderCatalog.VersionFolderName("1.20.1", "fabric", ""),
                _ => "<未覆盖>",
            };
            Equal($"版本文件夹名（{name}）", Normalize(want), Normalize(got));
        }

        // 文件夹名反解
        foreach (var (text, want) in g.GetProperty("folder_parse").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var expected = want.EnumerateArray().Select(x => x.GetString()!).ToList();
            var (mc, loader, lv) = LoaderCatalog.ParseVersionFolderName(text);
            Equal($"反解「{text}」",
                string.Join("|", expected),
                string.Join("|", new[] { mc, loader, lv }));
        }

        // ⚠️ 安装器生成的名字是 `1.20.1-forge-47.4.10`，反解出的版本号不能带前导连字符。
        var installerStyle = LoaderCatalog.ParseVersionFolderName("1.20.1-forge-47.4.10");
        Equal("反解安装器起的名字（1.20.1-forge-47.4.10）",
            "1.20.1|forge|47.4.10",
            string.Join("|", new[]
            {
                installerStyle.Minecraft, installerStyle.Loader, installerStyle.LoaderVersion,
            }));
    }

    /// <summary>后补的三家加载器：Legacy Fabric / Cleanroom / LiteLoader。</summary>
    private static void CheckLoaderCatalogExtras()
    {
        // 一、界面那张表跟引擎那张表必须是一套 key
        // 左栏「安装包」那几项、安装选项页的分组、引擎能不能装 —— 三处都读自己的表，
        // 错开过一次（左栏有 LiteLoader、点进去一句"这一栏还没做"），所以钉住。
        var uiKeys = Mosslight.App.Views.DownloadPage.LoaderKeys;
        var engineKeys = LoaderCatalog.LoaderOrder;
        Check($"界面「安装包」的 {uiKeys.Count} 家 == 引擎 LoaderOrder 的 {engineKeys.Length} 家",
            uiKeys.SequenceEqual(engineKeys),
            $"界面：{string.Join("、", uiKeys)} ／ 引擎：{string.Join("、", engineKeys)}");

        var noName = engineKeys.Where(k => !LoaderCatalog.LoaderNames.ContainsKey(k)).ToList();
        Check("每一家都有显示名", noName.Count == 0, string.Join("、", noName));

        var noFolder = engineKeys.Where(k => !LoaderCatalog.FolderNames.ContainsKey(k)).ToList();
        Check("每一家都有版本文件夹名", noFolder.Count == 0, string.Join("、", noFolder));

        // ⚠️ 文件夹名反解要区分 `LegacyFabric` 与 key `legacy_fabric`；LiteLoader 不带版本号，反解出空串是约定。
        foreach (var (mc, key, lv, wantBackVersion) in new[]
                 {
                     ("1.12.2", "legacy_fabric", "0.16.10", "0.16.10"),
                     ("1.8", "liteloader", "1.8", ""),
                     ("1.12.2", "cleanroom", "0.7.8-alpha", "0.7.8-alpha"),
                     ("1.20.1", "fabric", "0.16.10", "0.16.10"),
                 })
        {
            var folder = LoaderCatalog.VersionFolderName(mc, key, lv);
            var back = LoaderCatalog.ParseVersionFolderName(folder);
            Check($"文件夹名来回转：{folder}",
                back.Minecraft == mc && back.Loader == key && back.LoaderVersion == wantBackVersion,
                $"解出来 {back.Minecraft}|{back.Loader}|{back.LoaderVersion}");
        }

        // 二之二、组合安装的版本名（照 PCL 的 GetSelectName）
        Equal("组合名：Fabric + OptiFine（PCL 的顺序：Fabric 在前、OptiFine 最后）",
            "26.3-Fabric_0.20.0-OptiFine_HD_U_J1",
            LoaderCatalog.ComposeVersionName("26.3", new[]
            {
                ("optifine", "HD_U_J1"),
                ("fabric", "0.20.0"),
            }));
        Equal("组合名：LiteLoader 不带版本号",
            "1.8-LiteLoader-OptiFine_HD_U_I5",
            LoaderCatalog.ComposeVersionName("1.8", new[]
            {
                ("liteloader", "1.8"),
                ("optifine", "HD_U_I5"),
            }));
        Equal("组合名：只有原版 = 原版版本号",
            "1.20.1", LoaderCatalog.ComposeVersionName("1.20.1", Array.Empty<(string, string)>()));
        Equal("组合名：NeoForge 单独一家（跟 PCL 截图一致）",
            "26.3-NeoForge_26.3.0.90",
            LoaderCatalog.ComposeVersionName("26.3", new[] { ("neoforge", "26.3.0.90") }));
        Equal("组合名：LabyMod 的 Production 后缀",
            "26.3-LabyMod_47.3_Production",
            LoaderCatalog.ComposeVersionName("26.3", new[] { ("labymod", "47.3_Production") }));

        // Legacy Fabric 的接口地址（BMCLAPI 没有镜像它，只能官方）
        Equal("Legacy Fabric 的列表地址",
            "https://meta.legacyfabric.net/v2/versions/loader/1.12.2",
            LoaderCatalog.UrlFor("legacy_fabric", "1.12.2"));
        Equal("Legacy Fabric 的 profile 地址",
            "https://meta.legacyfabric.net/v2/versions/loader/1.12.2/0.16.10/profile/json",
            RealLoaderService.ProfileUrl("1.12.2", "legacy_fabric", "0.16.10"));

        // 三、LiteLoader：versions.json 的形状
        // 造一份跟真实那份结构一样的数据：1.8 有正式版（artefacts），
        // 1.12.2 只有 snapshots（真实情况就是这样）——后者必须**不列**。
        var liteLoaderJson = JsonNode.Parse("""
            {
              "versions": {
                "1.8": {
                  "repo": {"stream": "RELEASE", "type": "ivy", "url": "http://dl.liteloader.com/versions/"},
                  "artefacts": {
                    "com.mumfrey:liteloader": {
                      "6e9028816027f53957bd8fcdfabae064": {
                        "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                        "libraries": [{"name": "net.minecraft:launchwrapper:1.11"}],
                        "stream": "RELEASE", "file": "liteloader-1.8.jar",
                        "version": "1.8", "md5": "6e90", "timestamp": "1463585254"
                      },
                      "latest": {
                        "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                        "libraries": [{"name": "net.minecraft:launchwrapper:1.11"}],
                        "stream": "RELEASE", "file": "liteloader-1.8.jar",
                        "version": "1.8", "md5": "6e90", "timestamp": "1463585254"
                      }
                    }
                  }
                },
                "1.12.2": {
                  "repo": {"stream": "SNAPSHOT", "type": "m2", "url": "http://repo.mumfrey.com/"},
                  "snapshots": {
                    "com.mumfrey:liteloader": {
                      "latest": {"stream": "SNAPSHOT", "file": "liteloader-1.12.2-SNAPSHOT.jar",
                                 "version": "1.12.2-SNAPSHOT", "md5": "1420"}
                    }
                  }
                }
              }
            }
            """);

        var ll18 = LiteLoaderCatalog.ParseVersions(liteLoaderJson, "1.8");
        Check("LiteLoader：1.8 列出 1 个正式版（latest 跟 md5 那两条是同一份，要去重）",
            ll18.Count == 1 && ll18[0].Version == "1.8" && ll18[0].FileName == "liteloader-1.8.jar"
            && ll18[0].Stable, $"{ll18.Count} 条");

        var ll1122 = LiteLoaderCatalog.ParseVersions(liteLoaderJson, "1.12.2");
        Check("LiteLoader：只有 snapshots 的版本（1.12.2）**一条都不列**（那批要 mixin，仓库已 404）",
            ll1122.Count == 0, $"{ll1122.Count} 条");

        var llNothing = LiteLoaderCatalog.ParseVersions(liteLoaderJson, "1.21.1");
        Check("LiteLoader：没发过的游戏版本 → 0 条（不是抛异常）", llNothing.Count == 0);

        var llArtefact = LiteLoaderCatalog.PickArtefact(liteLoaderJson, "1.8", "1.8");
        Check("LiteLoader：按版本号挑得出那一节数据（安装时走这条）", llArtefact is not null);
        Check("LiteLoader：列表里没有的版本挑不出来（不会出现「界面没有却能装」）",
            LiteLoaderCatalog.PickArtefact(liteLoaderJson, "1.12.2", "1.12.2-SNAPSHOT") is null);

        var llJson = LiteLoaderCatalog.BuildVersionJson("1.8", "1.8-LiteLoader_1.8",
            llArtefact ?? new JsonObject(),
            "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory}");
        var libs = llJson["libraries"] as JsonArray ?? new JsonArray();
        var args = llJson["minecraftArguments"]?.GetValue<string>() ?? string.Empty;
        Check("LiteLoader 版本 JSON：inheritsFrom / jar 都指原版",
            llJson["inheritsFrom"]?.GetValue<string>() == "1.8"
            && llJson["jar"]?.GetValue<string>() == "1.8",
            $"inheritsFrom={llJson["inheritsFrom"]} jar={llJson["jar"]}");
        Check("LiteLoader 版本 JSON：mainClass 是 launchwrapper 的 Launch",
            llJson["mainClass"]?.GetValue<string>() == "net.minecraft.launchwrapper.Launch",
            llJson["mainClass"]?.GetValue<string>());
        Check("LiteLoader 版本 JSON：原版那串参数带上了，末尾加 --tweakClass",
            args.StartsWith("--username ${auth_player_name}", StringComparison.Ordinal)
            && args.EndsWith("--tweakClass com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                StringComparison.Ordinal),
            args);
        // 库 = 数据里自带的那一条（launchwrapper）+ 我们自己追加的 liteloader jar
        Check("LiteLoader 版本 JSON：数据里的库 + liteloader 自己的 jar（共 2 条）",
            libs.Count == 2
            && libs.Any(n => n?["name"]?.GetValue<string>() == "net.minecraft:launchwrapper:1.11")
            && libs.Any(n => n?["name"]?.GetValue<string>() == "com.mumfrey:liteloader:1.8"),
            string.Join("、", libs.Select(n => n?["name"]?.GetValue<string>())));

        // ⚠️ 两条库数据只有 `name` 没有 `url`，必须补主仓库；LiteLoader 的 jar 文件名与 maven 坐标不一致，路径和地址要写死。
        var launchwrapperLib = libs.FirstOrDefault(n =>
            n?["name"]?.GetValue<string>() == "net.minecraft:launchwrapper:1.11");
        Equal("LiteLoader：launchwrapper 补上了 Mojang 的库根",
            "https://libraries.minecraft.net/",
            launchwrapperLib?["url"]?.GetValue<string>() ?? "");

        var liteloaderLib = libs.FirstOrDefault(n =>
            n?["name"]?.GetValue<string>() == "com.mumfrey:liteloader:1.8");
        Equal("LiteLoader 的 jar 地址（按数据里的 version/file 拼）",
            "https://dl.liteloader.com/versions/com/mumfrey/liteloader/1.8/liteloader-1.8.jar",
            liteloaderLib?["downloads"]?["artifact"]?["url"]?.GetValue<string>() ?? "");

        // 1.7.10 那个"版本号 1.7.10_04 / 数据里的 file 写 liteloader-1.7.10.jar"的组合最容易拼错：
        // 服务器上只有 `liteloader-1.7.10_04.jar`（照 file 拼是 404，实测过）
        Equal("LiteLoader 的 jar 路径：目录和文件名都用加载器版本号（不信数据里的 file）",
            "com/mumfrey/liteloader/1.7.10_04/liteloader-1.7.10_04.jar",
            LiteLoaderCatalog.JarRelativePath("1.7.10_04"));

        // 四、LabyMod：manifest 的形状
        // 造一份跟真实那份结构一样的 manifest。⚠️ 里面 `tag` 和 `version` **不一样**
        // （1.8 / 1.8.9）——真实数据就是这样，按 tag 挑会一个都挑不出来。
        var labyManifest = JsonNode.Parse("""
            {
              "labyModVersion": "47.3",
              "commitReference": "1bf7a24b",
              "sha1": "abc",
              "size": 37374999,
              "releaseTime": 1758000000000,
              "assets": {"common": "5758", "fonts": "ff48"},
              "minecraftVersions": [
                {"tag": "26.3", "version": "26.3", "index": 4, "type": "release",
                 "customManifestUrl": "https://example.invalid/production/26.3.json"},
                {"tag": "1.8", "version": "1.8.9", "index": 99, "type": "release",
                 "customManifestUrl": "https://example.invalid/production/1.8.9.json"}
              ]
            }
            """);

        var laby263 = LabyModCatalog.ParseManifest(labyManifest, LabyModCatalog.ProductionChannel, "26.3");
        Check("LabyMod：26.3 挑得出一行（版本号取 manifest 顶上的 labyModVersion）",
            laby263.Count == 1 && laby263[0].Version == "47.3" && laby263[0].Stable,
            $"{laby263.Count} 条");
        Equal("LabyMod：这一行带着频道 + commit（安装那一步要用）",
            "production|1bf7a24b",
            laby263.Count > 0 ? $"{laby263[0].Type}|{laby263[0].Patch}" : "（没有）");

        // ⚠️ 按 `version` 挑（1.8.9 有），按 `tag`（1.8）挑不出来 —— 真实数据里两者不一样
        var laby189 = LabyModCatalog.ParseManifest(labyManifest, LabyModCatalog.ProductionChannel, "1.8.9");
        var laby18 = LabyModCatalog.ParseManifest(labyManifest, LabyModCatalog.ProductionChannel, "1.8");
        Check("LabyMod：按 version 挑（1.8.9 有、按 tag 那个 1.8 没有）",
            laby189.Count == 1 && laby18.Count == 0,
            $"1.8.9 → {laby189.Count} 条，1.8 → {laby18.Count} 条");

        Check("LabyMod：这个游戏版本不在 manifest 里 → 0 条（不是抛异常）",
            LabyModCatalog.ParseManifest(labyManifest, LabyModCatalog.ProductionChannel, "1.20.1").Count == 0);
        Check("LabyMod：snapshot 频道标成测试版（稳定与否看频道，不看条目里的 type）",
            LabyModCatalog.ParseManifest(labyManifest, LabyModCatalog.SnapshotChannel, "26.3")
                is { Count: 1 } snap && !snap[0].Stable);
        Equal("LabyMod：客户端 jar 的地址（37 MB 那个，接安装时要用）",
            "https://releases.r2.labymod.net/api/v1/download/labymod4/production/1bf7a24b.jar",
            LabyModCatalog.ClientJarUrl("production", "1bf7a24b"));
        Equal("LabyMod：资源包地址",
            "https://releases.r2.labymod.net/api/v1/download/assets/labymod4/production/1bf7a24b/common/5758.jar",
            LabyModCatalog.AssetUrl("production", "1bf7a24b", "common", "5758"));

        // 四之二、LabyMod：拼版本 JSON 的配方
        // 三份合成数据：某个游戏版本的 manifest JSON、频道的库清单、频道的 latest.json。
        // 关键在**剔掉跟隔离库重名的 LWJGL**（PCL 的 `CatchLwjglInLib` 那一步）。
        var labyVersionJson = JsonNode.Parse("""
            {
              "id": "1.12.2", "_minecraftVersion": "1.12.2",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "downloads": {"client": {"url": "https://example.invalid/client.jar", "sha1": "aa", "size": 10}},
              "assetIndex": {"id": "1.12", "url": "https://example.invalid/1.12.json"},
              "libraries": [
                {"name": "org.lwjgl.lwjgl:lwjgl:2.9.4", "url": "https://libraries.minecraft.net/"},
                {"name": "org.lwjgl.lwjgl:lwjgl_util:2.9.4", "url": "https://libraries.minecraft.net/"},
                {"name": "com.mojang:guava:21.0", "url": "https://libraries.minecraft.net/"}
              ]
            }
            """);
        var labyLibraries = JsonNode.Parse("""
            {
              "libraries": [
                {"name": "net.minecraft:launchwrapper:4.1.3",
                 "url": "https://releases.r2.labymod.net/libraries/net/minecraft/launchwrapper/4.1.3/launchwrapper-4.1.3.jar",
                 "sha1": "bb", "size": 123},
                {"name": "net.laby:labylib-core:1.2.0",
                 "url": "https://releases.r2.labymod.net/libraries/net/laby/labylib-core/1.2.0/labylib-core-1.2.0.jar",
                 "sha1": "cc", "size": 456}
              ],
              "isolated_libraries": [
                {"name": "lwjgl", "url": "https://example.invalid/lwjgl.jar", "versions": ["1.12.2"]},
                {"name": "lwjgl3", "url": "https://example.invalid/lwjgl3.jar", "versions": ["1.20.1"]}
              ]
            }
            """);
        var labyLatest = JsonNode.Parse("""
            {"commitReference": "1bf7a24b", "sha1": "dd", "size": 37374999, "labyModVersion": "47.3"}
            """);

        var labyBuilt = LabyModCatalog.BuildVersionJson(
            (JsonObject)labyVersionJson!, (JsonObject)labyLibraries!, (JsonObject)labyLatest!,
            LabyModCatalog.ProductionChannel, "1.12.2-LabyMod_47.3_Production");

        var builtNames = (labyBuilt["libraries"] as JsonArray)!
            .OfType<JsonObject>().Select(l => l["name"]?.GetValue<string>() ?? "").ToList();
        Check("LabyMod 拼 JSON：id 换成了版本名", labyBuilt["id"]?.GetValue<string>()
            == "1.12.2-LabyMod_47.3_Production");
        Check("LabyMod 拼 JSON：跟隔离库重名的 LWJGL 被剔掉了（lwjgl），别的原版库留着",
            !builtNames.Contains("org.lwjgl.lwjgl:lwjgl:2.9.4")
            && builtNames.Contains("org.lwjgl.lwjgl:lwjgl_util:2.9.4")
            && builtNames.Contains("com.mojang:guava:21.0"),
            string.Join("、", builtNames));
        Check("LabyMod 拼 JSON：LabyMod 自己的库都追加了（含 fork 过的 launchwrapper）",
            builtNames.Contains("net.minecraft:launchwrapper:4.1.3")
            && builtNames.Contains("net.laby:labylib-core:1.2.0"));
        Check("LabyMod 拼 JSON：最后一条是 37 MB 的客户端 jar",
            builtNames.Count > 0 && builtNames[^1] == "net.labymod:LabyMod:4"
            && (labyBuilt["libraries"] as JsonArray)![^1]?["downloads"]?["artifact"]?["url"]
                ?.GetValue<string>()
                == "https://releases.r2.labymod.net/api/v1/download/labymod4/production/1bf7a24b.jar",
            builtNames.Count > 0 ? builtNames[^1] : "（空）");
        Check("LabyMod 拼 JSON：labymod_data 写全了（频道 / commit / 版本号）",
            labyBuilt["labymod_data"]?["channelType"]?.GetValue<string>() == "production"
            && labyBuilt["labymod_data"]?["commitReference"]?.GetValue<string>() == "1bf7a24b"
            && labyBuilt["labymod_data"]?["version"]?.GetValue<string>() == "47.3",
            labyBuilt["labymod_data"]?.ToJsonString() ?? "（没有）");
        Check("LabyMod 拼 JSON：库的落盘路径按 url 去掉库根（不是按坐标拼）",
            (labyBuilt["libraries"] as JsonArray)!.OfType<JsonObject>()
                .Any(l => l["downloads"]?["artifact"]?["path"]?.GetValue<string>()
                    == "net/laby/labylib-core/1.2.0/labylib-core-1.2.0.jar"));
        Check("LabyMod 拼 JSON：规划得出的任务全都带地址（空地址下载时才会炸）",
            GameInstaller.PlanVersion(labyBuilt, Path.GetTempPath(), _ => true)
                .Tasks().All(t => t.Url.Length > 0));

        // 五、Cleanroom：XML 版本表
        const string metadataXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <groupId>com.cleanroommc</groupId>
              <artifactId>cleanroom</artifactId>
              <versioning>
                <latest>0.7.8-alpha</latest>
                <release>0.7.8-alpha</release>
                <versions>
                  <version>0.4.0</version>
                  <version>0.7.7-alpha</version>
                  <version>0.7.8-alpha</version>
                </versions>
                <lastUpdated>20260915000000</lastUpdated>
              </versioning>
            </metadata>
            """;
        var crRows = CleanroomCatalog.ParseVersions(metadataXml);
        Check("Cleanroom：XML 里的版本全收下来（3 条），新的在前",
            crRows.Count == 3 && crRows[0].Version == "0.7.8-alpha"
            && crRows[2].Version == "0.4.0",
            string.Join("、", crRows.Select(r => r.Version)));
        Check("Cleanroom：-alpha 的标成预发布，0.4.0 是稳定版",
            !crRows[0].Stable && !crRows[1].Stable && crRows[2].Stable);

        Check("Cleanroom：只支持 1.12.2（别的版本连列表都不拉）",
            CleanroomCatalog.Supports("1.12.2")
            && !CleanroomCatalog.Supports("1.20.1")
            && !CleanroomCatalog.Supports(null));

        // 六、Cleanroom：从安装器里抠 version.json
        var tempRoot = Path.Combine(Path.GetTempPath(), "mosslight-cleanroom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 造一个"安装器"：一个 zip，里面只有 version.json（真实那份也在根上）
            var installerPath = Path.Combine(tempRoot, "cleanroom-0.7.8-alpha-installer.jar");
            using (var zip = System.IO.Compression.ZipFile.Open(
                       installerPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "version.json", """
                    {
                      "id": "Cleanroom-0.7.8-alpha",
                      "mainClass": "top.outlands.foundation.boot.Foundation",
                      "minecraftArguments": "--username ${auth_player_name}",
                      "javaVersion": {"majorVersion": 25},
                      "libraries": [
                        {"name": "com.cleanroommc:cleanroom:0.7.8-alpha:universal",
                         "downloads": {"artifact": {"path": "com/cleanroommc/cleanroom/0.7.8-alpha/cleanroom-0.7.8-alpha-universal.jar",
                                                    "url": "", "size": 12725861}}},
                        {"name": "org.ow2.asm:asm:9.7", "url": ""},
                        {"name": "com.google.guava:guava:32.1.2-jre",
                         "url": "https://repo.maven.apache.org/maven2/"}
                      ]
                    }
                    """);
            }

            var vj = CleanroomCatalog.ReadVersionJson(installerPath);
            Equal("Cleanroom：从安装器里读出来的 mainClass",
                "top.outlands.foundation.boot.Foundation",
                vj["mainClass"]?.GetValue<string>() ?? "");

            var fixedCount = CleanroomCatalog.PrepareVersionJson(vj, "0.7.8-alpha", "1.12.2-Cleanroom_0.7.8-alpha");
            Equal("Cleanroom：id 被改成落盘的名字", "1.12.2-Cleanroom_0.7.8-alpha",
                vj["id"]?.GetValue<string>() ?? "");
            Check("Cleanroom：两处空地址都被补上了（artifact.url + 库自己的 url）",
                fixedCount == 2, $"补了 {fixedCount} 处");

            var cleanroomLib = (vj["libraries"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(l => l["name"]?.GetValue<string>()?.StartsWith(
                    "com.cleanroommc:cleanroom:", StringComparison.Ordinal) == true);
            Equal("Cleanroom：universal jar 的地址补成了 maven",
                "https://maven.cleanroommc.com/com/cleanroommc/cleanroom/0.7.8-alpha/cleanroom-0.7.8-alpha-universal.jar",
                cleanroomLib?["downloads"]?["artifact"]?["url"]?.GetValue<string>() ?? "");

            var asmLib = (vj["libraries"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(l => l["name"]?.GetValue<string>() == "org.ow2.asm:asm:9.7");
            Equal("Cleanroom：普通库的空 url 补成了 maven 根",
                "https://maven.cleanroommc.com/",
                asmLib?["url"]?.GetValue<string>() ?? "");

            // `PlanVersion` 能不能把这份 JSON 变成下载任务（顺带证明"没有 downloaded 段的库"也能规划）
            var plan = GameInstaller.PlanVersion(vj, tempRoot, _ => true);
            Check($"Cleanroom：这份 JSON 能规划出 {plan.Tasks().Count()} 个下载任务",
                plan.Tasks().Count() > 0, string.Join("、", plan.Warnings));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }

    private static void CheckLoaderCompat()
    {
        // 一、整行显不显示（PCL 的卡片可见性）
        // 这条是"26.3 上不该出现 LiteLoader / Cleanroom / Legacy Fabric"的判据
        Check("26.3：Forge / NeoForge / Fabric / Quilt / OptiFine / LabyMod 都在",
            new[] { "forge", "neoforge", "fabric", "quilt", "optifine", "labymod" }
                .All(k => LoaderCompat.IsApplicable(k, "26.3")));
        Check("26.3：LiteLoader / Cleanroom / Legacy Fabric 整行不显示",
            !LoaderCompat.IsApplicable("liteloader", "26.3")
            && !LoaderCompat.IsApplicable("cleanroom", "26.3")
            && !LoaderCompat.IsApplicable("legacy_fabric", "26.3"));
        Check("1.8：LiteLoader 在、Fabric / NeoForge / Quilt / Cleanroom / LabyMod 都不在",
            LoaderCompat.IsApplicable("liteloader", "1.8")
            && !LoaderCompat.IsApplicable("fabric", "1.8")
            && !LoaderCompat.IsApplicable("neoforge", "1.8")
            && !LoaderCompat.IsApplicable("quilt", "1.8")
            && !LoaderCompat.IsApplicable("cleanroom", "1.8")
            && !LoaderCompat.IsApplicable("labymod", "1.8"));
        Check("1.12.2：Legacy Fabric + Cleanroom + LiteLoader 都在（三个老版本的牌子）",
            LoaderCompat.IsApplicable("legacy_fabric", "1.12.2")
            && LoaderCompat.IsApplicable("cleanroom", "1.12.2")
            && LoaderCompat.IsApplicable("liteloader", "1.12.2"));
        Check("NeoForge 1.20.1 起（1.20 上不显示）",
            LoaderCompat.IsApplicable("neoforge", "1.20.1")
            && !LoaderCompat.IsApplicable("neoforge", "1.20"));

        // 二、互斥
        Equal("选了 Fabric → Forge 说「与 Fabric 不兼容」",
            "与 {name} 不兼容 | Fabric",
            ReasonText(LoaderCompat.IncompatibilityReason("forge", "fabric", "26.3")));
        Equal("选了 Forge → Fabric 也说同一句（双向）",
            "与 {name} 不兼容 | Forge",
            ReasonText(LoaderCompat.IncompatibilityReason("fabric", "forge", "26.3")));
        Check("没选任何一家时谁都不冲突",
            LoaderCompat.PrimaryLoaders.All(k =>
                LoaderCompat.IncompatibilityReason(k, string.Empty, "26.3") is null));
        Check("选自己不算冲突",
            LoaderCompat.PrimaryLoaders.All(k =>
                LoaderCompat.IncompatibilityReason(k, k, "26.3") is null));

        // 三、OptiFine 那条最绕的规则
        Check("OptiFine + Forge：1.12.2 可以（那会儿没有补丁匹配问题）",
            LoaderCompat.IncompatibilityReason("optifine", "forge", "1.12.2") is null);
        Check("OptiFine + Forge：1.13 ~ 1.14.3 全不兼容（PCL 的原话）",
            LoaderCompat.IncompatibilityReason("optifine", "forge", "1.13") is not null
            && LoaderCompat.IncompatibilityReason("optifine", "forge", "1.14.3") is not null
            && LoaderCompat.IncompatibilityReason("optifine", "forge", "1.14.4") is null);
        Check("OptiFine + NeoForge / LabyMod / Cleanroom：永远不兼容",
            LoaderCompat.IncompatibilityReason("optifine", "neoforge", "26.3") is not null
            && LoaderCompat.IncompatibilityReason("optifine", "labymod", "26.3") is not null
            && LoaderCompat.IncompatibilityReason("optifine", "cleanroom", "1.12.2") is not null);
        Check("OptiFine + Fabric：1.20.4 可以、1.20.5 起不兼容（没有 OptiFabric）",
            LoaderCompat.IncompatibilityReason("optifine", "fabric", "1.20.4") is null
            && LoaderCompat.IncompatibilityReason("optifine", "fabric", "1.20.5") is not null);
        Check("OptiFine + LiteLoader：可以（LiteLoader 只是注入 launchwrapper）",
            LoaderCompat.IncompatibilityReason("optifine", "liteloader", "1.8") is null
            && LoaderCompat.IncompatibilityReason("liteloader", "optifine", "1.8") is null);

        // 四、LiteLoader 的共存规则
        Check("LiteLoader + Forge：可以（PCL 里没有这条互斥）",
            LoaderCompat.IncompatibilityReason("liteloader", "forge", "1.8") is null
            && LoaderCompat.IncompatibilityReason("forge", "liteloader", "1.8") is null);
        Check("LiteLoader + NeoForge / Legacy Fabric / LabyMod / Cleanroom：不兼容",
            LoaderCompat.IncompatibilityReason("liteloader", "neoforge", "1.20.1") is not null
            && LoaderCompat.IncompatibilityReason("liteloader", "legacy_fabric", "1.12.2") is not null
            && LoaderCompat.IncompatibilityReason("liteloader", "labymod", "1.8.9") is not null
            && LoaderCompat.IncompatibilityReason("liteloader", "cleanroom", "1.12.2") is not null);

        // 五、可选组件
        Check("Fabric API：Fabric 上可以、选了别家时提示需要 Fabric",
            LoaderCompat.IncompatibilityReason("fabric_api", "fabric", "26.3") is null
            && ReasonText(LoaderCompat.IncompatibilityReason("fabric_api", "forge", "26.3"))
                == "需要 {name} | Fabric"
            && LoaderCompat.IncompatibilityReason("fabric_api", string.Empty, "26.3") is null);
        Check("Fabric API：Quilt 上也能用（Quilt 自己那套是 QFAPI）",
            LoaderCompat.IncompatibilityReason("fabric_api", "quilt", "26.3") is null);
        Check("QFAPI 只有 Quilt 能用、Legacy Fabric API 只有 Legacy Fabric 能用",
            LoaderCompat.IncompatibilityReason("qfapi", "quilt", "26.3") is null
            && ReasonText(LoaderCompat.IncompatibilityReason("qfapi", "fabric", "26.3"))
                == "需要 {name} | Quilt"
            && LoaderCompat.IncompatibilityReason("legacy_fabric_api", "legacy_fabric", "1.12.2") is null
            && ReasonText(LoaderCompat.IncompatibilityReason("legacy_fabric_api", "fabric", "1.12.2"))
                == "需要 {name} | Legacy Fabric");

        // 六、自动带上
        Check("Fabric 选中 → Fabric API 自动带上（PCL 的 autoSelectedFabricApi）",
            LoaderCompat.AutoBundledWith("fabric_api", "fabric"));
        // ⚠️ Quilt 也自动带：
        // 我们还没接 QFAPI / QSL，而 QFAPI 就是 Fabric API 的 Quilt 移植 ——
        // 带不了 QFAPI 的时候，带 Fabric API 是装完就能玩的唯一选择。
        Check("Quilt 选中也自动带 Fabric API（QFAPI 还没接）",
            LoaderCompat.AutoBundledWith("fabric_api", "quilt"));
        Check("Legacy Fabric 选中 → Legacy Fabric API 自动带上",
            LoaderCompat.AutoBundledWith("legacy_fabric_api", "legacy_fabric"));
        Check("Forge / NeoForge 不自动带任何 API",
            !LoaderCompat.AutoBundledWith("fabric_api", "forge")
            && !LoaderCompat.AutoBundledWith("legacy_fabric_api", "forge"));

        // 七、加载器自己的安装页：一进去落在哪个游戏版本
        // 清单顺序 = 新 → 旧（跟 VersionManifest 里一致，这里只取正式版）
        var releases = new[]
        {
            "26.3", "26.2", "26.1", "1.21.6", "1.21.5", "1.20.5", "1.20.1",
            "1.14.4", "1.14", "1.13.2", "1.12.2", "1.8.9", "1.8", "1.7.10",
        };
        Equal("Fabric 的安装页落在最新正式版（26.3）",
            "26.3", LoaderCompat.DefaultGameVersion("fabric", releases));
        Equal("Forge / NeoForge / Quilt 同理（都跟得上最新版）",
            "26.3 | 26.3 | 26.3",
            string.Join(" | ", new[] { "forge", "neoforge", "quilt" }
                .Select(k => LoaderCompat.DefaultGameVersion(k, releases))));
        Equal("Cleanroom 只有 1.12.2 —— 不这么挑的话页面一进去是空的",
            "1.12.2", LoaderCompat.DefaultGameVersion("cleanroom", releases));
        Equal("LiteLoader 落在它支持的最新一档（1.12.2）",
            "1.12.2", LoaderCompat.DefaultGameVersion("liteloader", releases));
        Equal("Legacy Fabric 落在它的上界（1.13.2）",
            "1.13.2", LoaderCompat.DefaultGameVersion("legacy_fabric", releases));
        Equal("清单是空的时候给空串，不瞎编一个版本",
            string.Empty, LoaderCompat.DefaultGameVersion("fabric", Array.Empty<string>()));
        Equal("一家都不支持的时候退回第一个（界面照实说「没有可用版本」）",
            "1.0", LoaderCompat.DefaultGameVersion("cleanroom", new[] { "1.0" }));
        Check("挑出来的版本一定满足 IsApplicable（否则等于挑了一个装不上的）",
            new[] { "fabric", "forge", "neoforge", "quilt", "cleanroom", "liteloader", "legacy_fabric" }
                .All(k => LoaderCompat.IsApplicable(k, LoaderCompat.DefaultGameVersion(k, releases))));
    }

    /// <summary>把兼容性原因拼成"模板 | 参数"，好在断言的期望值里一眼看出是哪一种。</summary>
    private static string ReasonText(CompatReason? reason)
        => reason is null ? "（允许）" : $"{reason.Template} | {reason.ArgTitle}";

    /// <summary>LabyMod：**真拉一遍** manifest / 版本 JSON / 库清单，拼出那套版本 JSON，再规划一遍任务，并 HEAD 一下客户端 jar。</summary>
    private static async Task CheckRealLabyModPlanAsync()
    {
        const string mc = "1.8.9";   // LabyMod 支持的最老一档，资源最少
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "labymod-plan");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
        Directory.CreateDirectory(scratch);

        var manifest = await GameInstaller.HttpJsonAsync(LabyModCatalog.ProductionManifest)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            Check("（跳过）LabyMod：拉不到 production manifest", true, "网络问题");
            return;
        }

        var rows = LabyModCatalog.ParseManifest(manifest, LabyModCatalog.ProductionChannel, mc);
        Check($"LabyMod 真清单里 {mc} 有 {rows.Count} 个版本", rows.Count > 0);
        if (rows.Count == 0)
        {
            return;
        }

        var versionJson = rows[0].FileName is { Length: > 0 } url
            ? await GameInstaller.HttpJsonAsync(url).ConfigureAwait(false)
            : null;
        Check("LabyMod:版本 JSON 拉得到", versionJson is not null);
        if (versionJson is null)
        {
            return;
        }

        var librariesJson = await GameInstaller.HttpJsonAsync(
            LabyModCatalog.LibrariesUrl(LabyModCatalog.ProductionChannel)).ConfigureAwait(false);
        Check("LabyMod:库清单拉得到", librariesJson is not null);
        if (librariesJson is null)
        {
            return;
        }

        var built = LabyModCatalog.BuildVersionJson(
            versionJson, librariesJson, manifest, LabyModCatalog.ProductionChannel,
            $"{mc}-LabyMod_{rows[0].Version}_Production");

        var names = (built["libraries"] as JsonArray)!.OfType<JsonObject>()
            .Select(l => l["name"]?.GetValue<string>() ?? "").ToList();
        Check($"LabyMod 真数据：拼出 {names.Count} 条库",
            names.Count > 100, $"实际 {names.Count} 条");
        Check("LabyMod 真数据：里面有 fork 过的 launchwrapper（mainClass 靠它解析）",
            names.Any(n => n.StartsWith("net.minecraft:launchwrapper:", StringComparison.Ordinal)),
            string.Join("、", names.Where(n => n.Contains("launchwrapper", StringComparison.Ordinal))));
        Check("LabyMod 真数据：客户端 jar 是最后一条",
            names[^1] == "net.labymod:LabyMod:4", names[^1]);
        // ⚠️ 这条只能查"**原版那份 JSON 里**的隔离库剔没剔"：
        // PCL 的配方是先剔原版那份、再把 LabyMod 自己的 108 条库**全部**追加进来，
        // 那里面本来就有 lwjgl（隔离加载器要用的 jar 也在其中）。
        // 写成"整张表里不能出现 lwjgl"会误报（第一版就是这么写的）。
        var isolatedNames = LabyModCatalog.IsolatedArtifactNames(librariesJson, mc);
        var vanillaLeft = (versionJson["libraries"] as JsonArray)!.OfType<JsonObject>()
            .Select(l => LabyModCatalog.ArtifactName(l["name"]?.GetValue<string>() ?? ""))
            .Where(isolatedNames.Contains)
            .Where(iso => names.Any(n => LabyModCatalog.ArtifactName(n) == iso))
            .ToList();
        Check($"LabyMod 真数据：原版那份里跟隔离库重名的 {isolatedNames.Count} 类 LWJGL 都剔掉了",
            vanillaLeft.Count == 0, string.Join("、", vanillaLeft));

        // 客户端 jar 真能下吗（只发 HEAD，不下 37 MB）
        var jarUrl = (built["libraries"] as JsonArray)![^1]?["downloads"]?["artifact"]?["url"]
            ?.GetValue<string>() ?? "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(LoaderCatalog.UserAgent);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, jarUrl);
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            Check($"LabyMod 真数据：客户端 jar 的地址回 {response.StatusCode}",
                response.IsSuccessStatusCode, jarUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Check("LabyMod 真数据：客户端 jar 的地址取不到", false, ex.Message);
        }

        var plan = GameInstaller.PlanVersion(built, scratch, _ => true);
        Check($"LabyMod 真数据：规划得出 {plan.Tasks().Count()} 个下载任务",
            plan.Tasks().Count() > 100 && plan.ClientJar is not null);
        var empty = plan.Tasks().Where(t => t.Url.Length == 0).Select(t => t.Label).ToList();
        Check("LabyMod 真数据：没有一个任务的地址是空的", empty.Count == 0,
            string.Join("、", empty.Take(5)));

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }



    // 安装器日志（`Logs\installer-*.log`）

    /// <summary>安装器的输出要**落到文件**。</summary>
    private static void CheckInstallerLog()
    {
        // 1) 路径本身：`<启动器目录>\Logs\installer-<key>-<mc>-<时间>.log`
        var path = LoaderInstaller.InstallerLogPath(@"C:\launcher", "neoforge", "1.20.4");
        Check("安装器日志放在 <启动器目录>\\Logs\\ 下",
            path.Replace('/', '\\').Contains(@"launcher\Logs\installer-neoforge-1.20.4-",
                StringComparison.Ordinal),
            path);
        Check("安装器日志的名字带时间戳（两次安装不会互相覆盖）",
            Regex.IsMatch(path, @"installer-neoforge-1\.20\.4-\d{8}-\d{6}\.log$"), path);

        // 2) 真的跑一次（用 cmd.exe 冒充 java，只验日志有没有落地）
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-installer-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var jar = Path.Combine(scratch, "fake-installer.jar");
            File.WriteAllBytes(jar, new byte[] { 0x50, 0x4B });
            var logPath = Path.Combine(scratch, "installer.log");
            var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (!File.Exists(cmdExe))
            {
                Check("（跳过）安装器日志：没有 cmd.exe", true);
                return;
            }

            var result = LoaderInstaller.RunInstaller(
                cmdExe, jar, scratch, "neoforge",
                timeoutSeconds: 20, logPath: logPath);

            Check($"安装器跑完有返回值（退出码 {result.ReturnCode}）", true);
            Check("安装器日志文件写出来了", File.Exists(logPath), logPath);
            if (File.Exists(logPath))
            {
                var text = File.ReadAllText(logPath);
                Check("日志里有我们写的头（时间 / 加载器 / 命令行 / 工作目录）",
                    text.Contains("Mosslight 安装器日志", StringComparison.Ordinal)
                    && text.Contains("# 加载器 ：neoforge", StringComparison.Ordinal)
                    && text.Contains("# 命令   ：", StringComparison.Ordinal)
                    && text.Contains("# 工作目录：", StringComparison.Ordinal),
                    text.Length + " 字符");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }



    // 安装包：只下载安装器（不安装）

    /// <summary>「安装包」那一栏的「下载安装器」：只落盘，不跑 Java、不建版本目录。</summary>
    private static void CheckInstallerDownloadOnly()
    {
        foreach (var key in new[] { "forge", "neoforge", "optifine", "cleanroom", "liteloader", "labymod" })
        {
            Check($"{key} 有安装器文件可下载", LoaderInstaller.HasInstallerFile(key));
        }
        foreach (var key in new[] { "fabric", "quilt", "legacy_fabric" })
        {
            Check($"{key} 没有可单独下载的安装器（安装器版本跟 loader 版本对不上）",
                !LoaderInstaller.HasInstallerFile(key));
        }

        Equal("Forge 安装器文件名带游戏版本和加载器版本",
            "forge-26.3-66.0.13-installer.jar",
            LoaderInstaller.InstallerFileName("forge", "26.3", "66.0.13"));
        Equal("NeoForge 安装器文件名不带游戏版本",
            "neoforge-21.3.100-installer.jar",
            LoaderInstaller.InstallerFileName("neoforge", "26.3", "21.3.100"));
        Equal("OptiFine 安装器文件名（第三段是 patch）",
            "OptiFine-26.3-HD_U_J2-installer.jar",
            LoaderInstaller.InstallerFileName("optifine", "26.3", "HD_U_J2"));

        Equal("LiteLoader 安装器文件名（redist 上那一版）",
            "liteloader-installer-1.7.10-04.jar",
            LoaderInstaller.DownloadInstallerName("liteloader", "1.7.10", "1.7.10_04"));
        Equal("LabyMod 安装器文件名（production）",
            "LabyMod4ProductionInstaller.jar",
            LoaderInstaller.DownloadInstallerName("labymod", "1.8.9", "4.2", "production"));
        Equal("LabyMod 安装器文件名（snapshot）",
            "LabyMod4SnapshotInstaller.jar",
            LoaderInstaller.DownloadInstallerName("labymod", "1.8.9", "4.2", "snapshot"));

        // 没有安装器的那些：调用直接返回 NotSupported，而且**不许**建目录
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-dl-only-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = LoaderInstaller.DownloadInstallerAsync(
                "fabric", "26.3", "0.20.0", scratch).GetAwaiter().GetResult();
            Check("Fabric 只下载：明确回报 NotSupported", result.NotSupported && !result.Ok);
            Check("Fabric 只下载：不在磁盘上留下任何目录", !Directory.Exists(scratch), scratch);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }

    // 装完找目录（安装器自己起的名字）

    /// <summary>安装器装完之后，`versions\` 下到底多了哪个目录。</summary>
    private static void CheckInstalledVersionLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "mosslight-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 造一个版本目录（JSON 里有 mainClass 就够 `VerifyInstalled` 认）
            static void MakeVersion(string versionsDir, string id)
            {
                var dir = Path.Combine(versionsDir, id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, id + ".json"),
                    $$"""
                    {"id":"{{id}}","mainClass":"net.minecraft.client.main.Main",
                     "inheritsFrom":"1.20.4","libraries":[]}
                    """);
            }

            // 情形 A：只有安装器自己起的名字
            var a = Path.Combine(root, "a", "versions");
            Directory.CreateDirectory(a);
            MakeVersion(a, "neoforge-20.4.253");
            MakeVersion(a, "1.20.1-forge-47.4.10");
            MakeVersion(a, "neoforge-20.4.999");   // 别的版本：不能被认成 20.4.253

            Equal("装完找目录：安装器那种 `<加载器>-<版本>`（neoforge-20.4.253）",
                "neoforge-20.4.253",
                LoaderInstaller.FindInstalledVersion(Path.Combine(root, "a"), "1.20.4", "neoforge", "20.4.253")
                ?? "（没找到）");
            Equal("装完找目录：带 mc 前缀的安装器名字（1.20.1-forge-47.4.10）",
                "1.20.1-forge-47.4.10",
                LoaderInstaller.FindInstalledVersion(Path.Combine(root, "a"), "1.20.1", "forge", "47.4.10")
                ?? "（没找到）");

            // ⚠️ 关键：版本号对不上时**不能**认（改名时认错会覆盖别人的版本）
            Equal("装完找目录：版本号不匹配就不认（20.4.111 不会挑中 20.4.253 / 20.4.999）",
                "（没找到）",
                LoaderInstaller.FindInstalledVersion(Path.Combine(root, "a"), "1.20.4", "neoforge", "20.4.111")
                ?? "（没找到）");

            // 情形 B：两种名字都在 → 我们约定的那个优先
            var b = Path.Combine(root, "b", "versions");
            Directory.CreateDirectory(b);
            MakeVersion(b, "neoforge-20.4.253");
            MakeVersion(b, "1.20.4-NeoForge_20.4.253");
            Equal("装完找目录：两种名字都在时，我们自己约定的名字优先",
                "1.20.4-NeoForge_20.4.253",
                LoaderInstaller.FindInstalledVersion(Path.Combine(root, "b"), "1.20.4", "neoforge", "20.4.253")
                ?? "（没找到）");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }



    // 安装器两份清单（install_profile.json + version.json）

    /// <summary>`InstallerLibraryTasks` 必须把安装器里**两份**清单都读上。</summary>
    private static void CheckInstallerLibraryTasks()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-instlib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var jar = Path.Combine(scratch, "fake-installer.jar");
            using (var zip = System.IO.Compression.ZipFile.Open(
                       jar, System.IO.Compression.ZipArchiveMode.Create))
            {
                // install_profile：一条自己的 + 一条与 version.json 重复的
                WriteZipEntry(zip, "install_profile.json", """
                    {"libraries": [
                      {"name": "org.example:patcher:1.0",
                       "downloads": {"artifact": {"path": "org/example/patcher/1.0/patcher-1.0.jar",
                                                   "url": "https://example.invalid/patcher-1.0.jar",
                                                   "sha1": "aa", "size": 100}}},
                      {"name": "shared:dup:1.0",
                       "downloads": {"artifact": {"path": "shared/dup/1.0/dup-1.0.jar",
                                                   "url": "https://example.invalid/dup-1.0.jar",
                                                   "sha1": "bb", "size": 200}}}
                    ]}
                    """);
                // version.json：一条**只在这里**的（就是 bootstraplauncher 那种）+ 同一条重复
                WriteZipEntry(zip, "version.json", """
                    {"libraries": [
                      {"name": "cpw.mods:bootstraplauncher:2.0.2@jar",
                       "downloads": {"artifact": {
                         "path": "cpw/mods/bootstraplauncher/2.0.2/bootstraplauncher-2.0.2.jar",
                         "url": "https://example.invalid/bootstraplauncher-2.0.2.jar",
                         "sha1": "cc", "size": 11116}}},
                      {"name": "shared:dup:1.0",
                       "downloads": {"artifact": {"path": "shared/dup/1.0/dup-1.0.jar",
                                                   "url": "https://example.invalid/dup-1.0.jar",
                                                   "sha1": "bb", "size": 200}}}
                    ]}
                    """);
            }

            var tasks = LoaderInstaller.InstallerLibraryTasks(jar, scratch);
            var labels = tasks.Select(t => t.Label).ToList();
            Check($"安装器两份清单：共收 {tasks.Count} 条（重复的只收一次）",
                tasks.Count == 3, string.Join("、", labels));
            Check("安装器两份清单：install_profile 里那条收进来了", labels.Contains("patcher-1.0.jar"));
            Check("安装器两份清单：**只在 version.json 里**的那条也收进来了（bootstraplauncher）",
                labels.Contains("bootstraplauncher-2.0.2.jar"), string.Join("、", labels));
            Check("安装器两份清单：落点都在 libraries\\ 下",
                tasks.All(t => t.Path.Replace('/', '\\').Contains("libraries\\", StringComparison.Ordinal)));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }



    // 版本要哪个 Java（带 inheritsFrom 的那种）

    /// <summary>NeoForge 那种 `inheritsFrom` 的版本该要哪个 Java；⚠️ NewForge 的 `-p` 那条路只要 Java 版本对不上就**直接起不来**，而且报的是一句"Unrecognized option"，离"Java 选错了"很远。</summary>
    private static void CheckRequiredJavaForInheriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "mosslight-javaver-" + Guid.NewGuid().ToString("N"));
        var mcDir = Path.Combine(root, "mc");
        Directory.CreateDirectory(root);
        try
        {
            // 原版：javaVersion 21
            var vanillaDir = Path.Combine(mcDir, "versions", "1.20.5");
            Directory.CreateDirectory(vanillaDir);
            File.WriteAllText(Path.Combine(vanillaDir, "1.20.5.json"), """
                {"id":"1.20.5","type":"release",
                 "mainClass":"net.minecraft.client.main.Main",
                 "javaVersion":{"component":"java-runtime-delta","majorVersion":21},
                 "libraries":[],"assetIndex":{"id":"17"},"assets":"17"}
                """);

            // NeoForge：无 javaVersion，inheritsFrom 1.20.5
            var neoDir = Path.Combine(mcDir, "versions", "1.20.5-NeoForge_20.5.21-beta");
            Directory.CreateDirectory(neoDir);
            var neoJsonPath = Path.Combine(neoDir, "1.20.5-NeoForge_20.5.21-beta.json");
            File.WriteAllText(neoJsonPath, """
                {"id":"1.20.5-NeoForge_20.5.21-beta","inheritsFrom":"1.20.5",
                 "mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
                 "arguments":{"jvm":["-p","${library_directory}"], "game":[]},
                 "libraries":[]}
                """);

            var settings = LoadIsolatedSettings(root);
            settings.MinecraftDir = mcDir;
            var services = LauncherServices.Production(settings, root);

            // ① 扫描器给了 JSON 路径（真实扫描出来的样子）
            var withPath = new GameVersion(
                Id: "1.20.5-NeoForge_20.5.21-beta",
                Name: "1.20.5-NeoForge_20.5.21-beta",
                MinecraftVersion: "1.20.5",
                Loader: "NeoForge",
                LoaderKey: "neoforge",
                VersionJsonPath: neoJsonPath);
            Equal("NeoForge 那种 inheritsFrom 的版本该要哪个 Java（扫描路径）",
                "21", services.Java.RequiredMajor(withPath).ToString());

            // ② 没有 JSON 路径 → 走"从游戏目录按 id 找"的兜底
            var withoutPath = withPath with { VersionJsonPath = null };
            Equal("同一个版本，没有 VersionJsonPath 时也要 21（兜底路径）",
                "21", services.Java.RequiredMajor(withoutPath).ToString());

            // ③ 原版自己没有 javaVersion 时，仍然退回 8（别把"读不到"当成 21）
            var unknownDir = Path.Combine(mcDir, "versions", "1.12.2");
            Directory.CreateDirectory(unknownDir);
            File.WriteAllText(Path.Combine(unknownDir, "1.12.2.json"),
                """{"id":"1.12.2","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
            var unknown = new GameVersion(
                Id: "1.12.2", Name: "1.12.2", MinecraftVersion: "1.12.2", Loader: "Vanilla",
                VersionJsonPath: Path.Combine(unknownDir, "1.12.2.json"));
            Equal("JSON 里没有 javaVersion 的老版本 → 按 8 算",
                "8", services.Java.RequiredMajor(unknown).ToString());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }



    // 加载器实例自己的 jar（NeoForge 1.20.5+ 的模块冲突）

    /// <summary>`LoaderInstaller.EnsureInstanceJar`：给实例补一份 `.jar`。</summary>
    private static void CheckDownloadSourcePolicy()
    {
        const string libUrl = "https://libraries.minecraft.net/com/example/x.jar";
        const string libMirror = "https://bmclapi2.bangbang93.com/maven/com/example/x.jar";

        static string Join(IEnumerable<(string Url, bool IsMirror)> plan) =>
            string.Join("|", plan.Select(p => p.Url));

        Equal("下载源=自动：镜像在前、官方在后",
            libMirror + "|" + libUrl,
            Join(DownloadManager.PlanUrls(libUrl, Array.Empty<string>(),
                DownloadSourceMode.Auto, mirrorAvailable: true)));

        Equal("下载源=自动（镜像熔断中）：只剩官方",
            libUrl,
            Join(DownloadManager.PlanUrls(libUrl, Array.Empty<string>(),
                DownloadSourceMode.Auto, mirrorAvailable: false)));

        Equal("下载源=只用官方：不碰镜像",
            libUrl,
            Join(DownloadManager.PlanUrls(libUrl, Array.Empty<string>(),
                DownloadSourceMode.Official, mirrorAvailable: true)));

        Equal("下载源=只用 BMCLAPI：只给镜像（没有官方兜底）",
            libMirror,
            Join(DownloadManager.PlanUrls(libUrl, Array.Empty<string>(),
                DownloadSourceMode.Mirror, mirrorAvailable: false)));

        Equal("下载源=双流：顺序兜底只剩官方（镜像那一路是并发跑的）",
            libUrl,
            Join(DownloadManager.PlanUrls(libUrl, Array.Empty<string>(),
                DownloadSourceMode.Dual, mirrorAvailable: true)));

        Equal("下载源=只用 BMCLAPI：没镜像的地址照旧原样",
            "https://example.invalid/x.jar",
            Join(DownloadManager.PlanUrls("https://example.invalid/x.jar", Array.Empty<string>(),
                DownloadSourceMode.Mirror, mirrorAvailable: true)));

        Equal("下载源=自动：备用地址也会映射",
            libMirror + "|" + libUrl + "|" + libMirror.Replace("/x.jar", "/y.jar")
            + "|https://maven.minecraftforge.net/com/example/y.jar",
            Join(DownloadManager.PlanUrls(libUrl,
                new[] { "https://maven.minecraftforge.net/com/example/y.jar" },
                DownloadSourceMode.Auto, mirrorAvailable: true)));

        // 配置字符串 ↔ 策略（写错的值不能把用户的设置变成另一个策略）
        foreach (var (key, want) in new[]
                 {
                     ("auto", DownloadSourceMode.Auto),
                     ("official", DownloadSourceMode.Official),
                     ("mirror", DownloadSourceMode.Mirror),
                     ("dual", DownloadSourceMode.Dual),
                     ("OFFICIAL ", DownloadSourceMode.Official),
                     ("没写过的值", DownloadSourceMode.Auto),
                     ("", DownloadSourceMode.Auto),
                 })
        {
            Equal($"下载源字符串「{key}」", want.ToString(), DownloadSourceModes.Parse(key).ToString());
        }

        foreach (var mode in new[]
                 {
                     DownloadSourceMode.Auto, DownloadSourceMode.Official,
                     DownloadSourceMode.Mirror, DownloadSourceMode.Dual,
                 })
        {
            Equal($"下载源策略 {mode} 的键",
                mode.ToString().ToLowerInvariant(), DownloadSourceModes.ToKey(mode));
        }
    }

    /// <summary>「游戏语言」（设置 → 其他）：选择「自动」时跟启动器语言走、显式选的照写，以及**首次启动才写、已有 options.txt 一个字不动**这条最要紧的约定。</summary>
    private static void CheckGameLocales()
    {
        Equal("游戏语言=自动 + 启动器简体 → zh_cn", "zh_cn", GameLocales.Resolve("auto", "zh_CN"));
        Equal("游戏语言=自动 + 启动器繁体 → zh_tw", "zh_tw", GameLocales.Resolve(null, "zh_TW"));
        Equal("游戏语言=自动 + 启动器英文 → en_us", "en_us", GameLocales.Resolve("", "en_US"));
        Equal("游戏语言=自动 + 启动器俄语 → ru_ru", "ru_ru", GameLocales.Resolve(GameLocales.Auto, "ru_RU"));
        Equal("游戏语言=显式简体（不管启动器语言）", "zh_cn", GameLocales.Resolve("zh_cn", "en_US"));
        Equal("游戏语言=显式繁體（香港）", "zh_hk", GameLocales.Resolve("zh_hk", "en_US"));
        Equal("游戏语言=写坏的值 → 回退到启动器语言", "ja_jp", GameLocales.Resolve("klingon", "ja_JP"));
        Equal("游戏语言的显示名（zh_hk）", "繁體中文（香港）", GameLocales.NativeName("zh_hk"));
        Equal("游戏语言的显示名（认不出来就原样）", "xx_yy", GameLocales.NativeName("xx_yy"));

        var dir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "gamelang");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的（下面 File.Exists 会挡住重复写）
        }

        try
        {
            Check("首次启动：没有 options.txt → 写一份", GameLocales.SeedOptions(dir, "zh_tw"));
            var file = Path.Combine(dir, "options.txt");
            Equal("写进去的就是一行 lang:", "lang:zh_tw\n", File.ReadAllText(file));
            Check("第二次启动不再写（已有 options.txt）", !GameLocales.SeedOptions(dir, "en_us"));
            Equal("已有文件的内容没被覆盖", "lang:zh_tw\n", File.ReadAllText(file));
            Check("空语言 / 空目录 → 安静地不写",
                !GameLocales.SeedOptions(dir, "") && !GameLocales.SeedOptions("", "zh_cn"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无所谓
            }
        }
    }

    private static void CheckEnsureInstanceJar()
    {
        var root = Path.Combine(Path.GetTempPath(), "mosslight-instjar-" + Guid.NewGuid().ToString("N"));
        var mcDir = Path.Combine(root, "mc");
        try
        {
            // 原版 1.20.5 的 jar（内容随便，只要字节能对上）
            var vanillaDir = Path.Combine(mcDir, "versions", "1.20.5");
            Directory.CreateDirectory(vanillaDir);
            File.WriteAllBytes(Path.Combine(vanillaDir, "1.20.5.jar"), new byte[] { 1, 2, 3, 4 });

            // NeoForge 实例：只有 JSON，`jar` 指回父版本
            const string id = "1.20.5-NeoForge_20.5.21-beta";
            var instDir = Path.Combine(mcDir, "versions", id);
            Directory.CreateDirectory(instDir);
            var jsonPath = Path.Combine(instDir, id + ".json");
            File.WriteAllText(jsonPath, """
                {"id":"1.20.5-NeoForge_20.5.21-beta","inheritsFrom":"1.20.5","jar":"1.20.5",
                 "mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher","libraries":[]}
                """);

            Check("实例没有 jar 时补一份（NeoForge 那种）",
                LoaderInstaller.EnsureInstanceJar(mcDir, id, "1.20.5"));
            var instanceJar = Path.Combine(instDir, id + ".jar");
            Check("补出来的 jar 跟原版那个一模一样",
                File.Exists(instanceJar)
                && File.ReadAllBytes(instanceJar).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            var json = JsonNode.Parse(File.ReadAllText(jsonPath));
            Equal("补完 JSON 的 jar 指回自己（不再指父版本）",
                id, json?["jar"]?.GetValue<string>() ?? "（没有）");

            // ⚠️ 已经有 jar 就不能覆盖：OptiFine 的 `<实例名>.jar` 是安装器打的补丁（23 MB）
            File.WriteAllBytes(instanceJar, new byte[] { 9, 9 });
            Check("已经有 jar 时不复制、不覆盖（OptiFine 那种补丁 jar）",
                !LoaderInstaller.EnsureInstanceJar(mcDir, id, "1.20.5")
                && File.ReadAllBytes(instanceJar).SequenceEqual(new byte[] { 9, 9 }));

            // 没有父版本 jar 时也不能炸（只是补不了）
            const string orphan = "1.20.5-Orphan_1.0";
            var orphanDir = Path.Combine(mcDir, "versions", orphan);
            Directory.CreateDirectory(orphanDir);
            File.WriteAllText(Path.Combine(orphanDir, orphan + ".json"),
                $$"""{"id":"{{orphan}}","inheritsFrom":"1.20.5-missing","libraries":[]}""");
            Check("父版本的 jar 不在时安静地补不了（不抛异常）",
                !LoaderInstaller.EnsureInstanceJar(mcDir, orphan));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }
    }

    /// <summary>往 zip 里塞一个文本条目（造那个假的 natives jar 用）。</summary>
    private static void WriteZipEntry(
        System.IO.Compression.ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>往 zip 里塞一段二进制（假的 mod 图标用）。</summary>
    private static void WriteZipBytes(
        System.IO.Compression.ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static string Describe(string name) => name.Length == 0 ? "(空名字)" : name;

    // 参数拆分 / 内存建议

    /// <summary>「版本设置」里用户手填的 JVM / 游戏参数是按空格拆的，而**不能用现成的命令行解析库**：POSIX 模式会把 Windows 路径里的反斜杠当转义符吃掉 （`-Djava.library.path=D:\mc` 变成 `D:mc`），非 POSIX 模式又根本不合并引号。</summary>
    private static void CheckArgSplittingAndMemory()
    {
        Equal("按空格拆", "a|b|c", string.Join("|", LaunchRules.SplitArgs("a b c")));
        Equal("多个连续空格只算一个分隔", "a|b", string.Join("|", LaunchRules.SplitArgs("a   b")));
        Equal("双引号里的空格不拆", "-Dpath=C:\\Program Files|next",
            string.Join("|", LaunchRules.SplitArgs("\"-Dpath=C:\\Program Files\" next")));
        Equal("单引号一样处理", "x y|z", string.Join("|", LaunchRules.SplitArgs("'x y' z")));

        // ⚠️ 这条是整段的重点：反斜杠**不是**转义符，必须原样留下
        Equal("Windows 路径里的反斜杠不能被吃掉", @"-Djava.library.path=D:\mc\bin",
            string.Join("|", LaunchRules.SplitArgs(@"-Djava.library.path=D:\mc\bin")));

        // ⚠️ 未闭合的引号不能抛异常 —— 用户填错一个引号不该让启动直接失败
        Equal("未闭合的引号照常拆出来", "a|b c",
            string.Join("|", LaunchRules.SplitArgs("a \"b c")));
        Equal("空串拆成空列表", "0", LaunchRules.SplitArgs("").Count.ToString());
        Equal("全是空格也拆成空列表", "0", LaunchRules.SplitArgs("   ").Count.ToString());

        // 内存
        // 滑块拖出来的值不会是整数档，所以所有能改内存的地方都要过 SnapMemory
        Equal("内存取整到 512 档（1000 → 1024）", "1024", SystemMemory.SnapMemory(1000).ToString());
        Equal("内存取整到 512 档（3586 → 3584）", "3584", SystemMemory.SnapMemory(3586).ToString());
        Equal("内存不低于下限 512", "512", SystemMemory.SnapMemory(0).ToString());
        Equal("内存也接受负数（夹到下限）", "512", SystemMemory.SnapMemory(-4096).ToString());

        // 4G 机器：给一半，但夹在 1024~2048 之间
        Equal("4G 机器建议 2048", "2048", SystemMemory.RecommendMemory(4096).ToString());
        Equal("2G 机器至少给 1024", "1024", SystemMemory.RecommendMemory(2048).ToString());
        // ⚠️ 不能超过物理内存的 60%：超了就是拿换页换内存，越玩越卡
        var big = SystemMemory.RecommendMemory(65536, modded: true);
        Check($"64G 带模组也不超过物理内存的 60%（建议 {big} MB）", big <= 65536 * 0.6);
        Equal("读不到内存时退回下限 2048", "2048", SystemMemory.RecommendMemory(0).ToString());

        // 读系统内存本身不该抛（读不到就返回全 0，界面按 .Ok 判断）
        var info = SystemMemory.Query();
        Check($"读系统内存不抛异常（总量 {info.TotalMb} MB）", info.TotalMb >= 0 || !info.Ok);
    }

    // 真启动一次

    /// <summary>造一个最小的可启动版本，**真的用 java 起一次进程**，验证三件事；⚠️ 为什么非要有这一节：前面所有检查都是纯计算或读写临时文件 —— "拼出来的命令行对不对"验得很扎实，但 `LaunchResult.Ok` 对用户唯一的含义是"游戏起来了"。</summary>
    private static void CheckRealProcessLaunch()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "launch");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的，不影响
        }

        var baseDir = Path.Combine(scratch, "base");
        var mcDir = Path.Combine(scratch, "mc");
        var versionId = "1.20.1";
        var versionDir = Path.Combine(mcDir, "versions", versionId);

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(versionDir);

        // 找一个能用的 Java
        // 缓存放在固定的临时目录：第一次要几秒，之后是毫秒级
        var javaCacheDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "cfg");
        // ⚠️ 先**删掉缓存**，保证下面第一遍扫描是"冷"的 —— 不然上一轮自检留下的
        // 缓存会让"第一遍"直接走缓存读，往返用例就退化成"读两次缓存"，
        // "写进去的能不能读出来"这件事反而没验到。
        try
        {
            File.Delete(Path.Combine(javaCacheDir, "java_cache.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉也能跑，只是这一节的说服力弱一点
        }

        List<JavaProbe> probes;
        try
        {
            probes = new JavaScanner(javaCacheDir).FindJavas();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            probes = new List<JavaProbe>();
        }

        var java = probes.FirstOrDefault(p => p.Usable);
        if (java is null)
        {
            Check("（跳过）真启动：这台机器没有可用的 Java", true,
                $"扫了 {probes.Count} 个候选，没一个能用");
            return;
        }

        // ⚠️ 必须验第二次扫描：缓存首次为空，存 / 取结构不一致只有第二扫才会暴露成“0 个可用 Java”。
        var second = new JavaScanner(javaCacheDir).FindJavas();
        Check($"缓存往返：第二遍扫描仍然找出同样多的可用 Java（{probes.Count(p => p.Usable)} 个）",
            second.Count(p => p.Usable) == probes.Count(p => p.Usable),
            $"第一遍 {probes.Count(p => p.Usable)} 个，第二遍 {second.Count(p => p.Usable)} 个");
        Check("缓存往返：第二遍读回来的主版本号没丢",
            second.Where(p => p.Usable).Select(p => p.Major).OrderBy(x => x)
                  .SequenceEqual(probes.Where(p => p.Usable).Select(p => p.Major).OrderBy(x => x)),
            "第二遍：" + string.Join(",", second.Where(p => p.Usable).Select(p => p.Major)));

        // 造一个最小可启动版本
        // ⚠️ natives **故意不预先建目录**，而且挂一个真的原生库 jar 上去：
        // 这样启动流程会真的走一遍 <see cref="NativesExtractor"/>，
        // "解压 natives"这件事才算被执行过（下面会验 dll 真的落出来了）。
        const string nativeLibRel = "com/example/natives/1.0/natives-1.0-natives-windows.jar";
        var nativeJar = Path.Combine(mcDir, "libraries",
            nativeLibRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(nativeJar)!);
        using (var zip = System.IO.Compression.ZipFile.Open(
                   nativeJar, System.IO.Compression.ZipArchiveMode.Create))
        {
            // 这个**必须被排除**（`extract.exclude` 默认就是 META-INF/）
            WriteZipEntry(zip, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n");
            // 这个**必须被解出来**
            WriteZipEntry(zip, "lwjgl.dll", "not a real dll, just bytes");
        }

        var vj = new JsonObject
        {
            ["id"] = versionId,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["releaseTime"] = "2023-06-12T13:25:51+00:00",
            // 用探测到的那个 Java 的大版本，保证 PickJava 能精确命中
            ["javaVersion"] = new JsonObject { ["majorVersion"] = java.Major },
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "com.example:natives:1.0",
                    ["natives"] = new JsonObject { ["windows"] = "natives-windows" },
                    ["extract"] = new JsonObject
                    {
                        ["exclude"] = new JsonArray("META-INF/"),
                    },
                    ["downloads"] = new JsonObject
                    {
                        ["classifiers"] = new JsonObject
                        {
                            ["natives-windows"] = new JsonObject { ["path"] = nativeLibRel },
                        },
                    },
                },
            },
            ["arguments"] = new JsonObject
            {
                ["jvm"] = new JsonArray("-cp", "${classpath}"),
                ["game"] = new JsonArray("--username", "${auth_player_name}",
                                         "--version", "${version_name}"),
            },
        };
        File.WriteAllText(Path.Combine(versionDir, versionId + ".json"),
            vj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(versionDir, versionId + ".jar"), new byte[] { 1, 2, 3 });

        // 真服务
        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        Check("真引擎：IsPrototype 全为 false（界面那句原型提示会自己消失）",
            !services.AnyPrototype);

        var versions = services.Games.RefreshAsync().GetAwaiter().GetResult();
        var version = versions.FirstOrDefault(v => v.Id == versionId);
        Check($"临时 .minecraft 里扫到了 {versionId}", version is not null,
            $"扫到 {versions.Count} 个");
        if (version is null)
        {
            return;
        }
        Check("扫出来的版本认得完整（有客户端 jar）", version.Complete);

        var account = services.Accounts.AddOffline("SelfCheck");
        Check("离线账户建出来了，UUID 是 v3 形式",
            account.Uuid.Length == 36 && account.Uuid[14] == '3');

        // 订事件
        var output = new List<string>();
        var exiting = new TaskCompletionSource<int>();
        services.Launch.GameOutput += (_, line) => { lock (output) { output.Add(line); } };
        services.Launch.GameExited += (_, code) => exiting.TrySetResult(code);

        // 启动
        var result = services.Launch.LaunchAsync(new LaunchRequest(
            Version: version,
            Account: account,
            Java: new JavaRuntime(java.Path, java.Version, java.Architecture, true,
                                  java.Major, java.Vendor),
            MaxMemoryMb: 512,
            MinecraftDir: mcDir,
            MinMemoryMb: 512,
            JavaPath: java.Path,
            VersionDir: versionDir)).GetAwaiter().GetResult();

        Check("真启动：进程起来了（Ok + 有 pid）",
            result.Ok && result.ProcessId is > 0,
            result.Message);
        Check("真启动：命令行记下来了（出问题只能靠它查）",
            !string.IsNullOrEmpty(result.CommandLine), result.CommandLine);
        Check("真启动：命令行里带着版本号和 -Xmx",
            result.CommandLine?.Contains(versionId) == true
            && result.CommandLine?.Contains("-Xmx512m") == true);

        // natives 真的解出来了
        var nativesDir = Path.Combine(versionDir, $"{versionId}-natives");
        Check("解压 natives：目录建出来了", Directory.Exists(nativesDir));
        Check("解压 natives：dll 落到了 natives 目录里",
            File.Exists(Path.Combine(nativesDir, "lwjgl.dll")));
        // ⚠️ `extract.exclude`（默认 META-INF/）必须生效 —— 解出来会干扰签名校验
        Check("解压 natives：META-INF 按 extract.exclude 被排除了",
            !Directory.Exists(Path.Combine(nativesDir, "META-INF")));
        // 记标记是为了"重复点安装不该反复解压"
        Check("解压 natives：留了 .extracted 标记（下次不用重解）",
            File.Exists(Path.Combine(nativesDir,
                "natives-1.0-natives-windows.jar.extracted")));

        // 再来一次：这次目录已经在、标记也在，应该**跳过**解压（幂等）
        var secondRun = NativesExtractor.EnsureNatives(
            VersionJsonLoader.Load(mcDir, versionId), mcDir, versionDir, versionId);
        Check("解压 natives：第二次跑是幂等的（不重复解压）",
            secondRun.Extracted == 0 && secondRun.AlreadyDone > 0,
            $"extracted={secondRun.Extracted} already={secondRun.AlreadyDone}");

        // 等退出
        // 这个 mainClass 不存在，java 会立刻报错退出 —— 正好证明
        // "stderr 收得上 + 非零退出码传得回来"
        var exited = exiting.Task.Wait(TimeSpan.FromSeconds(30));
        Check("真启动：退出事件在 30 秒内触发了", exited);
        Check("真启动：拿到的退出码非零（java 找不到那个主类）",
            exited && exiting.Task.Result != 0,
            exited ? $"退出码 {exiting.Task.Result}" : "没等到退出");

        // 输出是异步收的，给它一点时间落下来
        Thread.Sleep(400);
        int lineCount;
        lock (output)
        {
            lineCount = output.Count;
        }
        Check($"真启动：收得到游戏进程的输出（{lineCount} 行）", lineCount > 0,
            lineCount > 0 ? null : "一行都没收到 —— 重定向或事件没接上");
        Check("真启动：退出之后 IsRunning 变回 false", !services.Launch.IsRunning);

        // 收尾
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉无所谓
        }
    }

    // 联网冒烟（--net）

    /// <summary>对着**真实的** Mojang 清单把下载链路走一遍；⚠️ 这里**不会**装整个版本（那是几百 MB）。</summary>
    private static async Task CheckNetworkSmoke()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck");
        Directory.CreateDirectory(configDir);

        // 1. 清单
        var manifest = new VersionManifest(configDir);
        var (data, info) = await manifest.LoadAsync(force: true);

        if (data is null)
        {
            Check("拉到版本清单", false, $"拿不到（{info.Error}）—— 没网就跳过这一节");
            return;
        }
        Check($"拉到版本清单（来源 {info.Source}）", true);

        var ids = VersionManifest.AllVersionIds(data);
        Check($"清单里有版本（{ids.Count} 个）", ids.Count > 100);

        // 2. 分组
        var grouped = VersionManifest.Group(data);
        Check("最新正式版认得出来",
            grouped.LatestRelease is not null &&
            ids.Contains(grouped.LatestRelease.Id),
            grouped.LatestRelease?.Id ?? "<null>");
        Check("最新预览版认得出来", grouped.LatestSnapshot is not null);

        var april = grouped.Groups.First(g => g.Name == "愚人节版").Items;
        Check($"愚人节版单独成组（{april.Count} 个）", april.Count > 0);
        Check("愚人节版没有混进预览版",
            !grouped.Groups.First(g => g.Name == "预览版").Items.Any(e => april.Any(a => a.Id == e.Id)));
        // 20w14∞ 在清单里叫 20w14infinite，显示名才是带 ∞ 的那个
        var infinite = april.FirstOrDefault(e => e.Id == "20w14infinite");
        Check("20w14infinite 的显示名是 20w14∞", infinite?.DisplayName == "20w14∞",
            infinite?.DisplayName ?? "<不在这个组里>");

        // 3. 版本 JSON + 资源索引 + 计划
        var target = ids.Contains("1.20.1") ? "1.20.1" : grouped.LatestRelease?.Id;
        var entry = (data["versions"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .FirstOrDefault(v => v["id"]?.GetValue<string>() == target);
        var url = entry?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            Check($"清单里有 {target} 的下载地址", false);
            return;
        }

        var vj = await GameInstaller.HttpJsonAsync(url);
        Check($"取到 {target} 的版本 JSON", vj is not null);
        Check("它看起来确实是启动器要的那份（有 mainClass / libraries）",
            GameInstaller.LooksLikeLauncherJson(vj));

        if (vj is null)
        {
            return;
        }

        var tempMc = Path.Combine(configDir, "mc");
        var index = await GameInstaller.LoadAssetIndexAsync(tempMc, vj);
        Check("取到资源索引（网络上那份）", index is not null);

        var plan = GameInstaller.PlanVersion(vj, tempMc, assetIndex: index);
        var tasks = plan.Tasks().ToList();
        Check($"{target} 的计划里有客户端 jar", plan.ClientJar is not null);
        Check($"{target} 的计划里有库（{plan.Libraries.Count} 个）", plan.Libraries.Count > 10);
        Check($"{target} 的计划里有资源索引", plan.AssetIndex is not null);
        Check($"{target} 的计划里有资源文件（{plan.Assets.Count} 个）", plan.Assets.Count > 100);
        // ⚠️ natives 必须在任务表里 —— 漏了它老版本永远解不出 lwjgl 的 dll
        Check("全部任务包含 natives 那一批", tasks.Count == plan.Libraries.Count + plan.Natives.Count
            + plan.Assets.Count + (plan.ClientJar is null ? 0 : 1)
            + (plan.AssetIndex is null ? 0 : 1) + (plan.Logging is null ? 0 : 1));
        Check($"计划总大小算得出来（{DownloadManager.HumanSize(plan.TotalBytes())}）", plan.TotalBytes() > 0);

        // 镜像映射：官方的库地址要能换到 BMCLAPI
        Equal("库地址走镜像",
            "https://bmclapi2.bangbang93.com/maven/com/example/x.jar",
            DownloadManager.MirrorUrl("https://libraries.minecraft.net/com/example/x.jar"));
        Equal("资源地址走镜像",
            "https://bmclapi2.bangbang93.com/assets/ab/abcdef",
            DownloadManager.MirrorUrl("https://resources.download.minecraft.net/ab/abcdef"));
        Equal("关掉镜像就原样返回",
            "https://libraries.minecraft.net/com/example/x.jar",
            DownloadManager.MirrorUrl("https://libraries.minecraft.net/com/example/x.jar", enabled: false));
        Equal("不在映射表里的地址原样返回",
            "https://example.invalid/x.jar",
            DownloadManager.MirrorUrl("https://example.invalid/x.jar"));


        // 4. 真下一个文件
        // 挑最小的那个：log4j 配置（约 1.6 KB）或第一条资源文件
        var probe = plan.Logging ?? plan.Assets.OrderBy(t => t.Size).FirstOrDefault();
        if (probe is null)
        {
            Check("找得到一个可以真下的最小文件", false);
            return;
        }

        var dest = Path.Combine(configDir, "probe", Path.GetFileName(probe.Path));
        var manager = new DownloadManager(maxWorkers: 2);
        var task = manager.Add(probe.Url, dest, probe.Sha1, probe.Size, probe.Label, probe.Kind);
        manager.Start();
        manager.WaitAll(TimeSpan.FromSeconds(60));

        Check($"真下了一个文件（{probe.Label}，{DownloadManager.HumanSize(task.DoneBytes)}）",
            task.State == DownloadManager.StateDone,
            $"{task.State} {task.Error}");
        Check("下下来的文件 sha1 对得上",
            probe.Sha1.Length == 0
            || string.Equals(DownloadManager.Sha1File(dest), probe.Sha1, StringComparison.OrdinalIgnoreCase));

        // 再跑一遍：本地已有且校验通过 → 必须**跳过**而不是重下
        var manager2 = new DownloadManager(maxWorkers: 2);
        var again = manager2.Add(probe.Url, dest, probe.Sha1, probe.Size, probe.Label, probe.Kind);
        manager2.Start();
        manager2.WaitAll(TimeSpan.FromSeconds(30));
        Check("第二次跑会跳过（增量下载的基础）", again.Skipped);

        // 5. 五家加载器的接口都真的通
        // 这一节只能靠联网验：五家的回包形状各不相同（BMCLAPI 两种、
        // Fabric/Quilt 一种、OptiFine 又是另一种），离线 fixture 验的是
        // **解析**，验不了"地址还活着、形状还没变"。
        var rows = await LoaderCatalog.LoaderRowsAsync("1.20.1");
        Equal("五家加载器都列出来了", "5", rows.Count.ToString());
        Equal("顺序照 PCL（常用的在前）", "forge|neoforge|fabric|quilt|optifine",
            string.Join("|", rows.Select(r => r.Key)));

        foreach (var row in rows)
        {
            // ⚠️ Error 和"没数据"是两回事：接口挂了要说"拉不到"，
            // 而不是让用户以为我们没做这个加载器
            Check($"{row.Name} 接口通（{row.Count} 个版本，最新 {row.Latest}）",
                row.State == LoaderState.Ok && row.Count > 0,
                row.State == LoaderState.Error ? $"拉不到：{row.Error}" : $"state={row.State} count={row.Count}");
        }

        // ⚠️ BMCLAPI 的 `version` 可能是纯构建号，也可能带游戏版本前缀；排序取最后一个 `-` 之后的部分。
        var forgeRow = rows.First(r => r.Key == "forge");
        Check($"Forge 最新版是个能排序的版本号（{forgeRow.Latest}）",
            forgeRow.Latest.Length > 0 && forgeRow.Latest.Any(char.IsDigit),
            forgeRow.Latest);
        Check("Forge 版本列表是按构建号从新到旧排的",
            forgeRow.Versions.Count >= 2
            && LoaderCatalog.CompareVersionKeys(
                LoaderCatalog.VersionKey(forgeRow.Versions[0].Version),
                LoaderCatalog.VersionKey(forgeRow.Versions[^1].Version)) >= 0,
            $"{forgeRow.Versions.FirstOrDefault()?.Version} → {forgeRow.Versions.LastOrDefault()?.Version}");
        // 安装器那一票得挑得出来（`files[]` 里 category=installer 且 format=jar）
        Check("Forge 的安装器条目认得出来（有 installer_hash）",
            forgeRow.Versions.Any(v => !string.IsNullOrEmpty(v.InstallerHash)));

        // ⚠️ 只验 HEAD，不真下 8.8 MB 安装器。
        // 只验 HEAD —— 8.8 MB 的安装器不用真下，URL 对了就行。
        if (forgeRow.Latest.Length > 0)
        {
            var installerUrl = LoaderInstaller.InstallerUrl("1.20.1", "forge", forgeRow.Latest);
            Check($"Forge 安装器地址拼得对（HEAD 一下）", await UrlIsReachableAsync(installerUrl),
                installerUrl);
            // 备用地址（官方 maven）也要能通 —— 主地址 403 时全靠它
            var mavenUrl = LoaderInstaller.ForgeMavenUrl("1.20.1", forgeRow.Latest);
            Check("Forge 官方 maven 的备用地址也通", await UrlIsReachableAsync(mavenUrl), mavenUrl);
        }

        // Fabric 的 profile（不用 Java 那条路）地址也要能通
        var fabricProfileUrl = RealLoaderService.ProfileUrl("1.20.1", "fabric",
            rows.First(r => r.Key == "fabric").Versions.First(v => v.Stable).Version);
        Check("Fabric 的 profile 地址通", await UrlIsReachableAsync(fabricProfileUrl), fabricProfileUrl);

        // ⚠️ OptiFine 的预览版必须被滤掉 —— 不滤 latest 会变成 preview_xxx
        var optifineRow = rows.First(r => r.Key == "optifine");
        Check($"OptiFine 没把预览版当最新（{optifineRow.Latest}）",
            !optifineRow.Latest.Contains("pre", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(optifineRow.Latest));

        // Fabric 的 stable 标记要真的读出来（界面上"哪个是稳定版"全靠它）
        var fabricRow = rows.First(r => r.Key == "fabric");
        Check("Fabric 的版本带着 stable 标记",
            fabricRow.Versions.Any(v => v.Stable) && fabricRow.Versions.Any(v => !v.Stable));

        // 6. Modrinth 也通
        var mods = await ModrinthApi.SearchProjectsAsync(query: "sodium", mcVersion: "1.20.1",
            loader: "fabric", limit: 3);
        var hits = mods["hits"] as JsonArray ?? new JsonArray();
        Check($"Modrinth 搜得到东西（{hits.Count} 条）", hits.Count > 0);
        if (hits.Count > 0 && hits[0] is JsonObject first)
        {
            Check("搜索结果带着 slug / title",
                !string.IsNullOrEmpty(first["slug"]?.GetValue<string>())
                && !string.IsNullOrEmpty(first["title"]?.GetValue<string>()));
        }

        // 版本列表 + 筛选（Fabric API 就是靠这条装的）
        var apiVersions = await ModrinthApi.GetProjectVersionsAsync("fabric-api");
        Check($"Modrinth 拿得到 fabric-api 的版本（{apiVersions.Count} 个）", apiVersions.Count > 0);
        var filtered = ModrinthApi.FilterVersions(apiVersions, "1.20.1", "fabric");
        Check($"筛得出 1.20.1 + fabric 的那几条（{filtered.Count} 条）", filtered.Count > 0);
        if (filtered.Count > 0)
        {
            var modTask = ModrinthApi.TaskFromVersion(filtered[0], "/tmp/mods");
            Check($"筛出来的版本能变成下载任务（{modTask.Label}）",
                modTask.Label.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                && modTask.Url.StartsWith("https://", StringComparison.Ordinal));
        }

        // 拿不到的项目（404）要返回 null / 空表，**不抛异常**
        Check("不存在的项目返回 null 而不是抛异常",
            await ModrinthApi.GetProjectAsync("this-project-does-not-exist-xyz") is null);

        // ⚠️ 版本表只收正式版且新的在前；分类必须按项目类型分开，拿 mod 分类筛资源包会静默 0 条。
        var releases = await ModrinthApi.ReleaseVersionsAsync();
        Check($"拿得到正式版列表（{releases.Count} 个）", releases.Count > 20);
        Check("正式版列表里有老版本 1.20.1（说明没被截断）", releases.Contains("1.20.1"));
        Check("正式版列表里没有快照 / 预发布",
            !releases.Any(v => v.Contains('-') || v.Contains("snapshot", StringComparison.OrdinalIgnoreCase)));
        if (releases.Count > 1)
        {
            var newestKey = LoaderCatalog.VersionKey(releases[0]);
            var oldestKey = LoaderCatalog.VersionKey(releases[^1]);
            Check($"正式版列表新的在前（{releases[0]} → {releases[^1]}）",
                LoaderCatalog.CompareVersionKeys(newestKey, oldestKey) > 0);
        }

        var modCategories = await ModrinthApi.CategoriesAsync("mod");
        Check($"拿得到 mod 的分类（{modCategories.Count} 个）", modCategories.Count > 5);
        Check("mod 的分类里有 adventure / worldgen",
            modCategories.Contains("adventure") && modCategories.Contains("worldgen"));
        // ⚠️ 这两个是 `header == "features"` 那一组（资源包的 audio / gui），
        // 混进"分类"下拉里会让人以为分辨率和"冒险"是同一层的东西
        Check("分类里没有混进 features 那一组（gui / audio）",
            !modCategories.Contains("gui") && !modCategories.Contains("audio"));

        var packCategories = await ModrinthApi.CategoriesAsync("resourcepack");
        Check($"拿得到资源包的分类（{packCategories.Count} 个）", packCategories.Count > 0);
        // ⚠️ 这条是"静默 0 条"那个坑的门禁：资源包根本没有 worldgen 这个分类
        Check("资源包的分类跟 mod 的**不共用**（里面没有 worldgen）",
            !packCategories.Contains("worldgen"));

        // 数据包在 Modrinth 上一个分类都没有 —— 界面据此把"分类"整格收起来
        var dataCategories = await ModrinthApi.CategoriesAsync("datapack");
        Check($"数据包没有分类（{dataCategories.Count} 个）", dataCategories.Count == 0);

        // 真装一次 Fabric（仅 --net）：这是加载器链路中唯一未覆盖的端到端路径。
        await CheckRealFabricInstallAsync();
    }

    /// <summary>真装一次 Fabric：下 profile → 下库 → 落盘 → 再扫一次版本列表确认看得见；⚠️ 全程在一个**临时游戏目录**里做，绝不碰用户真实的那份。</summary>
    private static async Task CheckRealFabricInstallAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "fabric-install");
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用现成的
        }

        var baseDir = Path.Combine(scratch, "base");
        var mcDir = Path.Combine(scratch, "mc");
        var vanillaDir = Path.Combine(mcDir, "versions", "1.20.1");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(vanillaDir);

        // 原版的最小桩：加载器 profile 靠 inheritsFrom 找它，内容不重要
        File.WriteAllText(Path.Combine(vanillaDir, "1.20.1.json"), """
            {"id":"1.20.1","type":"release",
             "mainClass":"net.minecraft.client.main.Main",
             "libraries":[],"assetIndex":{"id":"5"}}
            """);
        File.WriteAllBytes(Path.Combine(vanillaDir, "1.20.1.jar"), new byte[] { 1, 2, 3 });

        var settings = AppSettings.Load(baseDir);
        settings.MinecraftDir = mcDir;
        var services = LauncherServices.Production(settings, baseDir);

        // 挑一个稳定的 Fabric 版本（列表刚在上一节拉过，这里重新拉一次拿最新的 stable）
        var fabricRow = (await LoaderCatalog.LoaderRowsAsync("1.20.1"))
            .First(r => r.Key == "fabric");
        var loaderVersion = fabricRow.Versions.FirstOrDefault(v => v.Stable)?.Version
                            ?? fabricRow.Latest;
        if (loaderVersion.Length == 0)
        {
            Check("（跳过）真装 Fabric：拉不到版本列表", true, "网络问题");
            return;
        }

        var result = await services.Loaders.InstallAsync(new LoaderInstallRequest(
            MinecraftVersion: "1.20.1",
            LoaderKey: "fabric",
            LoaderVersion: loaderVersion,
            MinecraftDir: mcDir));

        Check($"真装 Fabric {loaderVersion} 成功", result.Ok, result.Message);

        var versionId = result.VersionId ?? string.Empty;
        Check($"版本目录名照 PCL 约定拼（{versionId}）",
            versionId.StartsWith("1.20.1-Fabric ", StringComparison.Ordinal), versionId);

        var versionJson = Path.Combine(mcDir, "versions", versionId, versionId + ".json");
        Check("版本 JSON 落盘了", File.Exists(versionJson));

        if (File.Exists(versionJson))
        {
            var data = JsonIO.ParseBytes(File.ReadAllBytes(versionJson)) as JsonObject;
            // ⚠️ `inheritsFrom` 是这一条链上最要紧的字段：少了它 classpath 里
            // 就没有 Minecraft 的类，Fabric 会报 "couldn't locate the game"
            Equal("版本 JSON 里写了 inheritsFrom = 原版 id", "1.20.1",
                data?["inheritsFrom"]?.GetValue<string>() ?? "<没有>");
            Check("版本 JSON 里有主类（Fabric 的 KnotClient）",
                !string.IsNullOrEmpty(data?["mainClass"]?.GetValue<string>()));
            Check("版本 JSON 里有库列表",
                (data?["libraries"] as JsonArray)?.Count > 0);
        }

        // 库真的下来了（fabric-loader 那一份必然在）
        var libsRoot = Path.Combine(mcDir, "libraries", "net", "fabricmc");
        Check("Fabric 的库真的下下来了", Directory.Exists(libsRoot)
            && Directory.EnumerateFiles(libsRoot, "*.jar", SearchOption.AllDirectories).Any());

        var scanned = await services.Games.RefreshAsync();
        Check("扫版本列表能看见刚装的 Fabric 版本",
            scanned.Any(v => v.Id == versionId || v.DirName == versionId),
            "扫到：" + string.Join("、", scanned.Select(v => v.Id)));

        var scannedVersion = scanned.FirstOrDefault(v => v.Id == versionId || v.DirName == versionId);
        if (scannedVersion is not null)
        {
            Equal("扫出来的加载器 key 是 fabric", "fabric",
                scannedVersion.LoaderKey ?? "<null>");
            // 模组版本自己没有 jar 是正常的（jar 在父版本里），所以要算"完整"
            Check("它会算成可启动（能继承父版本的 jar）", scannedVersion.Complete);
        }

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无所谓
        }
    }
}
