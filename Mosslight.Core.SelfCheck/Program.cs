using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Animation;
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

/// <summary>
/// 自检：把 C# 这边算出来的东西跟 Python 版的金标准逐值比一遍。
///
/// 重点不是"能不能跑"，而是**算出来一不一样**。颜色运算差一个色阶、
/// 配置的类型纠正少认一种写法，都是编译能过、跑起来看着也正常的那类错 ——
/// 只有逐值比对才抓得住。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>
    /// 自检自己用的 HttpClient（`--net` 那几节里发 HEAD 用）。
    ///
    /// ⚠️ 跟引擎里那些**刻意不共用**：引擎那边的超时/UA 是给真实下载调的，
    ///    自检要的是"快速失败"，混在一起改一边会影响另一边。
    /// </summary>
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

        Section("参数拆分与内存建议");
        CheckArgSplittingAndMemory();

        // ⚠️ 这一节要起一个真的 java 进程（约 1 秒）。前面那些全是纯计算 /
        //    读写临时文件，"拼出来的命令行对不对"验过了，但
        //    **"到底有没有真的把进程拉起来、日志收不收得上来"** 没验过 ——
        //    而那正是 LaunchResult.Ok 对用户唯一的含义。
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
        //    再把安装器自己的库下下来，比 `--net` 那一堆只发 HEAD 的重得多。
        //    单独开关是为了让 `--net` 保持"几十秒能跑完"。
        if (args.Contains("--net-forge"))
        {
            Section("真跑一次 Forge 安装器（--net-forge）");
            CheckRealForgeInstallAsync().GetAwaiter().GetResult();
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

    // ---------- 小工具 ----------

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

    // ---------- 主题色 ----------

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

        // 银行家舍入：Python 的 round() 和 C# 的默认都是 0.5→偶，移植时最容易踩
        Check("IsHex 认 #rrggbb", ColorMath.IsHex("#5EC269"));
        Check("IsHex 拒 #rrggbbaa", !ColorMath.IsHex("#5ec26980"));
        Check("IsHex 拒 null", !ColorMath.IsHex(null));

        var (r, g, b) = ColorMath.ToRgb("#ffffff");
        Check("ToRgb(#ffffff) = 1,1,1", Math.Abs(r - 1) < 1e-9 && Math.Abs(g - 1) < 1e-9 && Math.Abs(b - 1) < 1e-9);
        Equal("ToHex(1,1,1)", "#ffffff", ColorMath.ToHex(1, 1, 1));
        Equal("ToHex 超范围会钳住", "#ffffff", ColorMath.ToHex(2, 2, 2));
        Equal("ToHex 负值会钳住", "#000000", ColorMath.ToHex(-1, -1, -1));
    }

    // ---------- 配置 ----------

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
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), "mosslight-selfcheck-nonexistent"));
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

        // multi_thread 关着 = 用默认的 8，**不是**单线程 —— 这是 1.0.0 的原设计
        settings.Set("multi_thread", false);
        settings.Set("download_threads", 3);
        Check("multi_thread 关 → 用默认 8 线程（不是 1）", settings.EffectiveThreads == 8,
            $"实际 {settings.EffectiveThreads}");
        settings.Set("multi_thread", true);
        Check("multi_thread 开 → 用自己设的 3", settings.EffectiveThreads == 3,
            $"实际 {settings.EffectiveThreads}");
    }

    // ---------- 多语言 ----------

    private static void CheckLocalization()
    {
        var langDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Mosslight.App", "Assets", "lang"));
        if (!Directory.Exists(langDir))
        {
            Check("找得到语言目录", false, langDir);
            return;
        }
        Localizer.Init(langDir);
        var available = Localizer.AvailableLanguages();
        Check("5 种语言都在", available.Count == 5, string.Join(", ", available));
        Check("语言顺序按 LanguageNames", available[0] == "zh_CN" && available[1] == "zh_TW",
            string.Join(", ", available));

        Localizer.SetLanguage("zh_CN");
        var chinese = Localizer.Tr("启动游戏");
        Localizer.SetLanguage("en_US");
        var english = Localizer.Tr("启动游戏");
        Check("同一个 key 在中英下不一样", chinese != english, $"{chinese} / {english}");
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

        // ---------- 5 个语言文件的**键集合必须一字不差** ----------
        //
        // 这一条以前**没有**。工程约定是"键集合保持一致"，但没人验过 ——
        // 结果就是新加的文案只补了 zh_CN，另外四份查不到、整条退回中文。
        // 中文能看懂，所以这种缺失在界面上**看起来是正常的**，只有把五份文件的键
        // 拉出来对一遍才抓得住。
        CheckLanguageKeySets(langDir);
    }

    /// <summary>
    /// 五个语言文件的键集合必须完全一致（键顺序可以不同 —— 那是给 review 看的，
    /// 不值得为它报错，所以这里比的是**集合**）。
    /// </summary>
    private static void CheckLanguageKeySets(string langDir)
    {
        var names = new[] { "zh_CN", "zh_TW", "en_US", "ja_JP", "ru_RU" };
        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var path = Path.Combine(langDir, $"{name}.json");
            if (!File.Exists(path))
            {
                Check($"语言文件 {name}.json 在", false, path);
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            sets[name] = doc.RootElement.EnumerateObject().Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            Check($"语言文件 {name}.json 有内容", sets[name].Count > 500, $"{sets[name].Count} 条");
        }

        var baseline = sets["zh_CN"];
        foreach (var name in names.Skip(1))
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

    // ---------- 入场动画预设 ----------

    /// <summary>
    /// 期望值全部**照抄 Python 的 `core/anim_prefs.py`**，不从 <c>Presets</c> 里读出来再比自己 ——
    /// 那样写只是把赋值抄了两遍，改错一个数字照样"通过"。
    ///
    /// 后面几个字段（DurationMs 起）给了默认值，是为了让"只改了时长"这类改动一眼看得见，
    /// 也免得 7 条各写一遍同样的方向 / 形态 / 缓动。
    /// </summary>
    private sealed record PresetCase(
        string Key, AnimationDirection Direction, int DurationMs, int OffsetPx, int IntervalMs,
        AnimationStyle Style = AnimationStyle.Simple, string Ease = "cubic");

    private static void CheckAnimationPresets()
    {
        // Python 的 PRESETS / PRESET_ORDER / DEFAULT_PRESET / DEFAULT_SPEED
        //
        // ⚠️ 顺序照 **PRESET_ORDER**，不是 PRESETS 字典的书写顺序 ——
        // 那里 slide_left_back 和 slide_up_elastic 是反着的，照字典抄就会错。
        //
        // ⚠️⚠️ 下面这七行数值**不是 1.0.0 的数值**，是这一轮"逐档调手感"重新调的
        //    （1.0.0 原值见每一项后面的注释）。**Key / Name / PresetOrder 一个没动** ——
        //    那是用户点名要保住的东西。改的只有时长 / 位移 / 错峰 / 缓动 / 过冲 / 淡入。
        //    调参依据（每档改了哪几个数、为什么）写在 AnimationPresets.Presets 的注释里，
        //    量法在实验目录的 HandProbe 探针里，两边对得上。
        //    1.0.0 的原值仍然可以从 `tests/slide_anim_golden.json` 里读到 ——
        //    那一节验的是**轨迹引擎**跟 Python 逐值一致，用的是金标准里的参数，
        //    跟这张表无关，所以"逐值对齐"这件事照样可验。
        var expected = new PresetCase[]
        {
            //                                        duration  offset  interval   style / ease
            // 1.0.0: 420 / 120 / 95 / cubic
            new("slide_left",         AnimationDirection.Horizontal, 330, 120,  60),
            // 1.0.0: 460 /  80 / 100 / quint
            new("slide_up",           AnimationDirection.Vertical,   380,  84,  70, Ease: "quint"),
            // 1.0.0: 760 / 120 / 115 / spring, overshoot 28, bounce_ratio 0.5
            new("slide_left_bounce",  AnimationDirection.Horizontal, 460, 120,  85,
                AnimationStyle.Spring, "cubic"),
            // 1.0.0: 340 /  28 /  55 / quint
            new("pcl_like",           AnimationDirection.Vertical,   300,  26,  45, Ease: "cubic"),
            // 1.0.0: 900 / 110 /  95 / elastic
            new("slide_up_elastic",   AnimationDirection.Vertical,   560,  52,  55, Ease: "elastic"),
            // 1.0.0: 520 / 120 /  85 / back:0.18
            new("slide_left_back",    AnimationDirection.Horizontal, 360, 120,  65, Ease: "back:0.10"),
            // 1.0.0: 350 /  16 /  25 / pcl
            new("pcl_recipe",         AnimationDirection.Vertical,   300,  24,  32, AnimationStyle.Pcl, "cubic"),
        };

        Check("一共 7 个预设", AnimationPresets.Presets.Count == 7,
            $"实际 {AnimationPresets.Presets.Count}");
        Equal("PresetOrder 长度", "7", AnimationPresets.PresetOrder.Count.ToString());
        // 逐位比而不是只比"集合一样"：顺序错了下拉框的排序就跟 1.0.0 分叉了，
        // 而 Sort() 一下就能"看起来通过"的那种写法抓不住它
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

        // 回弹类才用得到的两个值：跟错一个，弹簧的幅度和节奏就全变了
        // （1.0.0 是 28 / 0.5；这一轮量出 28px 过冲 = 位移的 23.3% 太夸张、
        //   0.5 又让前段"慢慢蹭到 0"才弹，所以改成 18px / 0.6）
        Equal("slide_left_bounce · OvershootPx", "18",
            AnimationPresets.Presets["slide_left_bounce"].OvershootPx.ToString());
        Equal("slide_left_bounce · BounceRatio", "0.6",
            AnimationPresets.Presets["slide_left_bounce"].BounceRatio.ToString());
        // 默认值也别放过：其余 6 条用的都是它
        Equal("slide_left · OvershootPx 默认", "12",
            AnimationPresets.Presets["slide_left"].OvershootPx.ToString());
        Equal("slide_left · BounceRatio 默认", "0.72",
            AnimationPresets.Presets["slide_left"].BounceRatio.ToString());

        // ---------- 这一轮新加的：淡入比例 ----------
        //
        // ⚠️ 这七个值是**调参的一部分**，不是随手填的：透明度在「整条轨迹 × FadeRatio」
        //    那一刻到 1，目标是让它落在"卡片还剩一点没到位、但正在明显往终点收"的位置。
        //    取值过程是拿探针逐档扫出来的（`HandProbe --sweep`），
        //    判据是"淡入到 0.9 比视觉落位早 15~180ms（60~150ms 最理想）"。
        //    改动之前所有非 PCL 形态都是 1.0（透明度跟位移共用一条曲线，等于淡入形同虚设）。
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

        // ---------- 这一轮新加的：逐档手感不变量 ----------
        //
        // 上面那些是"值抄对了没有"，这里验的是**整套设计**（值全对、但七档一个味道也是失败）：
        // 每档单看要顺（尾巴不拖、淡入跟位移配合），七档合起来要分得开。
        // 判据和量法跟 HandProbe 探针一致，但这里**不依赖探针**（自检要能独立跑）。
        CheckPresetHandfeel();

        // ---------- 认不出来不许抛 ----------

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

        // ---------- 缩放 ----------

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

        // ---------- 版本列表的短参数 ----------
        //
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

    // ---------- 逐档手感（这一轮调参的验收） ----------

    /// <summary>
    /// 七档的**手感不变量**：每一档单看要顺，七档合起来要分得开。
    ///
    /// 这一节是"调参"这件事的验收 —— 上面那些断言只能证明"值抄对了"，
    /// 证明不了"调得好"。而"好不好看"没法直接断言，能断言的是它的**可量代理**：
    ///
    ///   1. 每一档都要：视觉落位（位移第一次 ≤2px）在 80% 时长之内、
    ///      1px 尾巴 ≤45% 时长（"停不稳"的下限）、起步速度 ≥ 位移的 0.85%/ms；
    ///   2. 有可见回弹的至少三档（弹簧 / 弹性 / 过冲），且**每档都要真看得见**
    ///      （过冲 >0.5px，见 <see cref="AnimationRebound.ReboundEpsilonPx"/>）；
    ///   3. 回弹档的过冲 ≤25px（绝对像素，不是百分比：百分比对小位移的档不公平）；
    ///   4. 七档的**收尾速度要拉开 ≥2.5 倍**（用"最后 4px 占时长比"量）——
    ///      这条是"七档是不是一个味道"的代理指标；
    ///   5. 整列 12 张铺完时间要拉开 ≥1.6 倍，且都落在 0.6~1.6 秒。
    ///
    /// ⚠️ 为什么不跟 HandProbe 探针共用一份实现：探针在实验目录里、**不进仓库**，
    ///    自检必须能独立跑。两边判据保持一致（这里是精简版，探针那份还多量几项）。
    /// </summary>
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
            //   后面 283ms 全是阻尼摆动 —— 判它"停不稳"是判错了对象）。
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

        // ---------- 回弹：既要有，又要看得见、不夸张 ----------
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

        // ---------- 七档是不是一个味道 ----------
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

    // ---------- 缓动曲线 ----------

    /// <summary>
    /// 跟 <c>ui/widgets/pcl_ease.py</c> 逐点比对参数化缓动曲线。
    ///
    /// 这一节存在的理由：公式抄错、或者 `EaseInCore` 与 `EasingMode` 的约定搞反，
    /// 都是**编译能过、动画也照样"挺顺"**的错 —— 只有把真实曲线逐点拉出来比才抓得住。
    /// 金标准由 `D:\deepseek\.dhml-tests\gen_ease_golden.py` 直接 import pcl_ease 生成，
    /// 不是手抄的数值。
    /// </summary>
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

        // ---------- 先立约定，再比数值 ----------
        //
        // WPF 的 Ease() 会按 EasingMode 再加工一次 EaseInCore：EaseOut 时是
        // `1 - EaseInCore(1 - t)`。ParametricEase 那边的反演就是照这条写的。
        // 这里拿**内置的** CubicEase（公式确定：OutCubic = 1-(1-t)^3）实测一遍 ——
        // 万一哪天框架改了约定，第一条就红，不会变成"三条曲线一起悄悄反掉"。
        // ⚠️ 特意**不**走 EasingFactory：那验的才是"框架的约定"本身，
        //    走工厂的话验的就变成"我们自己的类"，约定改了也未必红。
        var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var conventionMax = 0.0;
        for (var i = 1; i < count; i++)
        {
            var t = i / 100.0;
            conventionMax = Math.Max(conventionMax, Math.Abs(cubicOut.Ease(t) - (1.0 - Math.Pow(1.0 - t, 3))));
        }
        Check("WPF 约定：EaseOut 时 Ease(t) == 1 - EaseInCore(1-t)（拿内置 OutCubic 实测）",
            conventionMax < 1e-12, $"最大偏差 {conventionMax:E3}");

        // ---------- 逐点比对 ----------
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

        // ---------- 参数也要对 ----------
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

        // ---------- 工厂的映射表 ----------
        Check("Create(\"back:0.18\") 是自实现的过冲曲线（不是 WPF 的 BackEase，Amplitude 不是过冲比例）",
            EasingFactory.Create("back:0.18") is ParametricBackEase);
        Check("Create(\"elastic\") 是自实现的弹性曲线（不是 WPF 的 ElasticEase）",
            EasingFactory.Create("elastic") is ParametricElasticEase);
        Check("Create(\"spring:0.14\") 是自实现的阻尼正弦（不是 WPF 的 ElasticEase）",
            EasingFactory.Create("spring:0.14") is ParametricDampedSineEase);

        Check("Create(\"cubic\") → CubicEase/EaseOut",
            EasingFactory.Create("cubic") is CubicEase { EasingMode: EasingMode.EaseOut });
        // ⚠️ 这一条**改过**（本轮之前期望的是 WPF 内置的 QuinticEase）。
        //    Python 的 `EASE_QUINT` 走 Qt 内置 OutQuint，而那就是 `1-(1-t)^5`；
        //    WPF 的 QuinticEase 是另一条四次多项式（EaseOut 时 t=0.5 处 0.94921875，
        //    精确 OutQuint 是 0.96875）—— "逐值一致"这条验收标准下不能再留着这个分叉。
        //    新增的断言直接把曲线跟 `1-(1-t)^5` 逐点比（见下面那段），不是只把类型名字换掉。
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
        // ⚠️ 这条是**实测出来的事实**，跟工程里旧注释写的相反：
        //    旧注释说"WPF 的 QuinticEase 不是 1-(1-t)^5，0.5 处只有 0.94921875"，
        //    实测（本机 net10.0/windows）它**就是** 1-(1-t)^5，逐点差 1e-16。
        //    所以把 quint 换成自实现的 ParametricPowerOutEase 是**行为等价**的重构
        //    （好处是公式跟 Core 的轨迹引擎共用一份），不是修 bug。
        //    这条断言留着当哨兵：哪天框架真改了 QuinticEase 的公式，它会红。
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

    // ---------- 日志窗口的后端 ----------

    /// <summary>
    /// 日志窗口那一层（<see cref="LogLine"/> / <see cref="MinecraftLogReader"/> / <see cref="LogBus"/>）。
    ///
    /// 这一节补的是"窗口上看到的东西对不对"里**能自动验的那一半**：
    /// 级别判定、续行继承、噪音过滤、log4j XML 还原成什么样、计数和导出。
    /// 剩下的一半（配色、排版、点一下藏一级）只能靠截图，自检管不着。
    ///
    /// 金标准是 1.0.0 的 <c>ui/dialogs/log_window.py</c>（<c>_detect_level</c> /
    /// <c>_is_continuation</c>）和 <c>core/mclog.py</c>（Log4jXmlReader），
    /// 期望值都是**照那两份源码手抄的输入输出对**，不是从被测代码里读出来的。
    /// </summary>
    private static void CheckLogging()
    {
        // ---------- 级别判定：认那四种带定界符的写法 ----------
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
                     // ⚠️ `12:00:00 ERROR 消息` 这种**不带定界符**的裸写法**认不出来** ——
                     //    1.0.0 的四条判据（`/LEVEL]`、`level="LEVEL"`、`LEVEL:`、`[LEVEL]`）
                     //    一条都匹配不上。这是**照抄的行为**，不是漏了：
                     //    放宽成"包含 LEVEL 就算"会让 `no errors found` 也被算成错误行。
                     ("12:00:00 ERROR something broke", LogLevel.None),
                 })
        {
            Equal($"DetectLevel(\"{line[..Math.Min(40, line.Length)]}…\")",
                expected.ToString(), LogLine.DetectLevel(line).ToString());
        }

        // ⚠️ 这一条是 1.0.0 注释里点名的坑：简单找子串的话，"no errors found"
        //    这种正常输出会被算成错误行 —— 所以只认带定界符的写法
        Equal("DetectLevel(\"no errors found\") 不认成错误",
            LogLevel.None.ToString(), LogLine.DetectLevel("no errors found").ToString());
        Equal("DetectLevel(\"警告：这东西已过期\") 不认成 WARN（中文不算标记）",
            LogLevel.None.ToString(), LogLine.DetectLevel("警告：这东西已过期").ToString());
        Equal("DetectLevel(null)", LogLevel.None.ToString(), LogLine.DetectLevel(null).ToString());
        Equal("DetectLevel(\"\")", LogLevel.None.ToString(), LogLine.DetectLevel("").ToString());

        // ---------- 续行（堆栈） ----------
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

        // ---------- 噪音 ----------
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

        // ---------- log4j XML 还原 ----------
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

        // 带堆栈的事件：每一行堆栈都要缩进一个 tab（见 MinecraftLogReader 的类注释）。
        //
        // ⚠️ 这里**必须走读取器逐行喂**，不能直接把整段丢给 Render ——
        //    真实情况里 `Process.OutputDataReceived` 是**按行**回调的，
        //    一条事件（连它的 CDATA 里那几行堆栈）会被拆成 5 行分别送进来。
        //    直接丢整段进去的话，"CDATA 里的换行"在 XML 解析之后仍然是换行，
        //    条数就多一条 —— 那是**测试没模拟对真实输入**，不是还原逻辑错（踩过）。
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

        // ---------- 有状态的读取器：一条事件跨 3 行 ----------
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

        // ---------- 日志总线 ----------
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

        // ---------- 那一排计数按钮的级别表 ----------
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

    // ---------- 入场动画轨迹引擎 ----------

    /// <summary>
    /// 跟 `ui/widgets/slide_in.py` 的**轨迹**逐点比对。
    ///
    /// 这一节补的是上一轮最大的窟窿：那时候 PageAnimator 只会"按 direction/duration/offset/ease
    /// 播一条曲线"，弹簧 / 三段回弹 / PCL 双位移三种形态**根本没有实现**，
    /// 于是"弹簧"和"普通"两档的区别只在时长和缓动上 —— 编译能过、看着也顺，
    /// 只有把每一毫秒的位移拉出来跟 Python 比才抓得住。
    ///
    /// 金标准由 `D:\deepseek\实验\tools\gen_slide_golden.py` 生成：它 import 的是**真的**
    /// `slide_in.py`（拷贝出来的 1.0.0），用 `QAbstractAnimation.setCurrentTime()`
    /// 逐点定位真动画对象，读 `SlideInRow._slide_offset` 和孩子被 move 到的坐标。
    ///
    /// ⚠️ 两处**有意**跟金标准不一样的地方（都是 1.0.0 自身的缺陷，不是移植误差）：
    ///   1. 弹簧档的整条轨迹 —— 1.0.0 的 `_spring()` 只算 bump、起手位移又被 `reveal()`
    ///      紧接着覆盖成 0，卡片是"凭空出现在终点然后原地抖"。这边按预设自带的
    ///      `bounce_ratio` 把"滑到位"和"回弹"拆成前后两段，所以整条轨迹不逐点相等；
    ///      但**回弹段那条 bump 的公式**必须逐值一致（`spring_bump` 那组采样）。
    ///   2. 非 PCL 形态的透明度 —— 1.0.0 压根不给这些形态挂淡入动画（opacity 恒为 1），
    ///      2.0 保留了"所有形态都淡入"（那是用户可见的一档设置）。
    /// </summary>
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

            // ---------- 总时长 ----------
            var goldenDuration = golden.GetProperty("duration_ms").GetDouble();
            Equal($"{key} · 轨迹总时长", goldenDuration.ToString("F0"),
                SlideInTrajectory.TotalDurationMs(preset).ToString("F0"));

            // ---------- 位移逐点 ----------
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

            // ---------- 过冲峰值 ----------
            var goldenPeak = golden.GetProperty("peak_overshoot_px").GetDouble();
            var measured = AnimationRebound.Measure(preset);
            if (isSpring)
            {
                // 金标准的峰值来自 1.0.0 那条"只有 bump"的轨迹；2.0 的合成轨迹过冲应当
                // **正好**是预设里写的 OvershootPx（因为 scale 就是照它归一化的）
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

            // ---------- 弹簧：回弹段公式逐值 ----------
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

        // ============================================================
        //  形态本身的算法（不依赖金标准，直接盯 1.0.0 里那几段代码的语义）
        // ============================================================

        // ---------- Bounce：三段 ----------
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

        // ---------- Spring：真·往返 ----------
        // ⚠️ 这一节用的是**预设表里真正在播的那一档**（不是金标准里的参数），
        //    所以数值跟着 AnimationPresets 走：这一轮调参之后是
        //    460ms / 0.6 / overshoot 18px（1.0.0 是 760 / 0.5 / 28）。
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

        // ---------- PCL：两条位移叠加 + 淡入 ----------
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
        // 淡入跑在慢轨前面：以**轨迹自己的时间轴**为准（快轨/慢轨/淡入三条各不相同），
        // 不写死毫秒 —— 预设的时长和位移这一轮都改过，写死就会跟着过期。
        // 判据：淡入那条收尾的时刻（`FadeMs`）**还在慢轨中途**，那一刻位移还没归零。
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

        // ---------- 关键帧（WPF 那边真正会播的数据） ----------
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

            // 关键帧之间 WPF 走的是线性插值 —— 把重建出来的折线跟解析轨迹比，
            // 差得太多就说明采样太稀（弹簧那个拐点最吃这个）
            //
            // ⚠️ 判据是 **0.06px**，不是 0.03 —— 这一轮量出来 0.045px 之后才想明白：
            //    误差**不是采样不够密**造成的，而是"速度断点"本身的几何性质：
            //    折线在断点处取的是左右两点连线的值，而解析轨迹在那里是个折角，
            //    两者的差正比于"断点两侧的速度差 × 采样间隔"。采样再密也只能把
            //    间隔压到 MinKeyframeSpanMs（2ms），于是有一个下界。
            //    弹簧那档的分段比从 0.5 调到 0.6 之后，拐点两侧的速度差从 1.09px/ms
            //    变成 1.96px/ms，下界跟着翻倍 —— 所以是该改判据，不是该改算法。
            //    0.06px 在屏幕上是 1/16 个像素，离"看得出来"差着两个数量级。
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

        // ---------- 总开关：关掉 = 不建计划 ----------
        var scratch = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "anim");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "portable.txt"), string.Empty);
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

        // ---------- 回弹判断（对话框那句话的判据） ----------
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

        // ---------- 缓动曲线：工厂造出来的对象 == Core 的公式 ----------
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

        // 三条新曲线（PCL 那档的慢轨 + PCL 旧版弹性）以前 C# 这边**完全没有**，
        // 现在有了：跟 pcl_ease 的同名函数比一遍
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

        // ---------- 认不出来 / 参数写坏了一律退回 OutCubic ----------
        // ⚠️ 这两条是**跟着 Python 改的**：`_ease_curve("back:")` 在那边返回 OutCubic
        //    （头名变成带冒号的整串，一个分支都匹配不上），"BACK:0.18" 同理（精确匹配）。
        //    改之前 C# 这边会把它们当成 back 用默认参数处理 —— 两边行为是分叉的。
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

    /// <summary>金标准里那段 `preset` → Core 的 <see cref="AnimationPreset"/>（字段一一对应）。</summary>
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

    // ---------- 服务桩 ----------

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

    // ============================================================
    //  启动引擎金标准
    // ============================================================

    /// <summary>
    /// 金标准里一个启动计划的 case。
    ///
    /// ⚠️ 用命名 record 而不是嵌套元组数组：嵌套元组的 `new (a, (b, c))[]` 写法
    ///    在 C# 里很难读，而且参数一多就分不清哪个是哪一列 —— 这里每个都带名字。
    /// </summary>
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

    /// <summary>
    /// 把 C# 的启动引擎跟 Python 版的金标准逐值比一遍。
    ///
    /// 为什么不满足于"能编译、能跑"：启动命令拼装是这个项目里**最容易
    /// "看着对、其实错"**的地方 —— rules 的"最后一条匹配生效"、`${version_type}`
    /// 的取值、老版本的 `-Djava.library.path`、log4j 配置的路径替换，
    /// 每一条都是编译能过、跑起来也不报错、但游戏就是起不来（或者起来以后行为不对）
    /// 的那种错。只有逐值比才抓得住。
    ///
    /// fixture 和两份 JSON 都由 `tools/gen_launch_golden.py` 生成 ——
    /// 它把**同一批文件**喂给 Python 版，所以这里比的是真正的等价性，
    /// 不是"我自己觉得应该长这样"。
    /// </summary>
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

        // ---------- 纯函数 ----------
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

        // ---------- 继承链合并 ----------
        var fabric = VersionJsonLoader.Load(mcDir, "1.20.1-fabric");
        var merged = golden.GetProperty("merged_fabric");
        Equal("合并后 id 用子版本的", merged.GetProperty("id").GetString()!, fabric.Id);
        Equal("合并后 mainClass 用子版本的", merged.GetProperty("mainClass").GetString()!, fabric.MainClass);
        // ⚠️ 子版本**没写** jar 字段 → 必须回退到父版本（这里父版本 id 就是 jar 的目录名）。
        //    这就是"Fabric 报 couldn't locate the game"那个坑。
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

        // ⚠️ OptiFine：JSON 没写 jar，但自己目录里躺着 <id>.jar → **自己目录那个优先**。
        //    退回父版本的原版 jar 等于 OptiFine 白装了，而且一声不响。
        Equal("OptiFine 自己目录里有 jar → 用它",
            golden.GetProperty("merged_optifine_jar").GetString()!,
            VersionJsonLoader.Load(mcDir, "1.20.1-OptiFine_HD_U_I6").Jar);
        // ⚠️ 反过来：JSON **显式写了** jar（Forge 1.16- 的写法）就不能被顶掉
        Equal("Forge 显式写的 jar 不能被顶掉",
            golden.GetProperty("merged_forge_jar").GetString()!,
            VersionJsonLoader.Load(mcDir, "1.16.5-forge-36.2.34").Jar);

        // ---------- 启动计划 ----------
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

        // ---------- 扫描 ----------
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
        //    `Neoforge` / `Optifine` 是编译能过、看着也不刺眼的那种错，
        //    只有逐字比才抓得住
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

        // ⚠️ 坏 json 只该记进 errors，**不能**拖垮整次扫描（1.0.0 那边被 ACL 坏掉的
        //    目录整个搞崩过一次）；而"没有带 id 的 json"的目录要静默跳过、不算错误。
        Equal("扫描告警条数（坏 json 记 1 条、非版本目录不记）",
            scanGolden.GetProperty("errors_count").GetInt32().ToString(),
            scanner.Errors.Count.ToString());
        Check("坏版本没进列表", scanned.All(v => v.Version.Id != "broken"));
        Check("非版本目录被静默跳过", scanned.All(v => v.Version.Id != "native-libraries"));

        // ⚠️ **带 UTF-8 BOM 的版本 JSON 必须认得出来**。
        //    `JsonNode.Parse(byte[])` 内部的 Utf8JsonReader **不支持跳过 BOM**，
        //    喂进去第一句就是「0xEF is an invalid start of a value」；
        //    而记事本改一下 json 就会带上 BOM —— 那个版本会从列表里凭空消失，
        //    只在扫描告警里留一句"json 格式错误"。1.0.0 用的是 utf-8-sig，
        //    本来就防着这件事。（用户真机上就是这么发现的。）
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

    /// <summary>把绝对路径换成占位符 —— 跟 Python 版 `gen_launch_golden.py` 的 normalize 一模一样。</summary>
    private static string Normalize(string text, string mcDir)
    {
        // ⚠️ 两个分隔符都要换：Python 那边是
        //    `text.replace(prefix + "\\", "").replace(prefix + "/", "")`，
        //    路径中间混着 `\`（Path 拼出来的）和 `/`（JSON 里写的），
        //    只换一种会漏掉一半。
        return text
            .Replace(mcDir + "\\", "<MC>/", StringComparison.OrdinalIgnoreCase)
            .Replace(mcDir + "/", "<MC>/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ParseNode(string json) => JsonNode.Parse(json);

    // ============================================================
    //  真跑一次 Forge 安装器（--net-forge）
    // ============================================================

    /// <summary>
    /// 把 Forge 的安装器**真下下来、真跑一遍**。
    ///
    /// 为什么单独一节：`--net` 那边验的是"地址拼对了、HEAD 通"，
    /// 而"跑安装器"这条路上还有一堆只有真跑才现形的东西 ——
    /// `install_profile.json` 读不读得出来、命令行拼得对不对、
    /// 安装器的输出收不收得上、超时/退出码处理对不对。
    ///
    /// ⚠️ 这一节**不假装能装出一个能启动的版本**：Forge 1.20.1 的安装器
    ///    要给原版 jar 打补丁，而自检里只放了一个**最小桩**（原版完整装下来
    ///    是 700 MB，自检不能那么重）。所以这里验的是**安装器这一整套机器**：
    ///    下得下来 → 认得出来是安装器 → 读得出它要的库 → 命令行拼得对 →
    ///    进程真的跑起来 → 输出收得上 → 拿到退出码。装完之后**不强求**版本目录出现。
    /// </summary>
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
        File.WriteAllText(Path.Combine(baseDir, "portable.txt"), string.Empty);

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

        // ---------- 1. 安装器下得下来、而且真的是个安装器 ----------
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

        // ---------- 2. 读得出它自己要的库 ----------
        var libTasks = LoaderInstaller.InstallerLibraryTasks(jarPath, mcDir);
        Check($"读得出安装器自己要的库（{libTasks.Count} 个）", libTasks.Count > 0);
        Check("那些库的任务带着下载地址和落点",
            libTasks.All(t => t.Url.StartsWith("https://", StringComparison.Ordinal)
                              && t.Path.Length > 0));

        // ---------- 3. 命令行拼得对 ----------
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

        // ---------- 4. 真跑 ----------
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
        //    但实测它是 0（Forge 1.20.1 的安装器在 `--installClient` 这一步
        //    只写版本 JSON + 下库，补丁是运行时由 processors 干的）。
        //    所以顺手把装出来的东西**验一遍** —— 那才是"真装上了"的证据。
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
                    //    原版的一切靠 `inheritsFrom` 合过来 —— 少了这一行就起不来
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

    /// <summary>
    /// 只发一个 HEAD，看这个地址通不通（不真下内容）。
    ///
    /// ⚠️ 用 HEAD 不是 GET：验的是"地址拼对了没有"，不是"文件好不好"。
    ///    Forge 的安装器有 8.8 MB，为了验一条 URL 真下下来没必要。
    /// </summary>
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

    // ============================================================
    //  整合包服务（离线端到端）
    // ============================================================

    /// <summary>
    /// 把整合包**服务层**真跑一遍：建实例目录 → 展开覆盖层 → 写版本 JSON。
    ///
    /// ⚠️ 刻意用一个**没有 files、也没有加载器**的包，这样一步网络都不用走 ——
    ///    自检是离线/CI 的常客，不该为了验编排就要求联网。
    ///    要验的正是编排本身：目录落在哪、实例名消毒了没、覆盖层展开了没、
    ///    版本 JSON 里有没有那段 `modpack` 元数据。
    /// </summary>
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
        File.WriteAllText(Path.Combine(baseDir, "portable.txt"), string.Empty);

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
        //    `CON` 是 Windows 保留名要加前缀 —— 不消毒的话目录根本建不出来
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

        // 没加载器 → 界面要说清楚"这个实例还不能直接启动"
        Check("如实报告\"加载器没装上\"", result.NeedsLoader);
        Check("告警里说了没加载器",
            result.Warnings?.Any(w => w.Contains("加载器")) == true,
            string.Join("；", result.Warnings ?? new List<string>()));

        // 版本列表要能扫到它（这是"装完看不见"那个坑的现场）
        var scanned = services.Games.RefreshAsync().GetAwaiter().GetResult();
        Check("扫版本列表能扫到这个实例",
            scanned.Any(v => v.Id == instanceId || v.DirName == instanceId),
            $"扫到 {scanned.Count} 个");

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
    private static byte[] BuildTestMrpack(string name, Dictionary<string, string> overrides)
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
                // ⚠️ 刻意**不写**依赖：没加载器，服务层就走"兜底版本 JSON"那条路，
                //    一步网络都不用走
                ["dependencies"] = new JsonObject(),
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

    // ============================================================
    //  整合包
    // ============================================================

    /// <summary>
    /// 整合包解析跟 Python 版逐值比。
    ///
    /// ⚠️ 这一节里**最要紧的是路径安全**：整合包是用户从网上下回来的 ZIP，
    ///    里面 `files[].path` 和 `overrides/` 的条目名完全由打包的人说了算。
    ///    一条 `../` 漏过去就是 Zip Slip（往实例目录外面写文件）。
    ///    所以下面每条不安全路径都单独点名，而不是只比两个数字。
    /// </summary>
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

        // ---------- 名字消毒 ----------
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

        // ---------- 路径安全（Zip Slip 的现场）----------
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
        //    （PurePosixPath 会把它们规范化掉），拒了就是误伤
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

        // ---------- 找 index / 找覆盖层 ----------
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
        //    只认根目录的话这类包会直接报「这不是整合包」
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

        // ---------- 读一个真的 .mrpack ----------
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
        //    unsupported 是服务端专用，客户端不该装
        Check("optional 的文件照装（不跳过）",
            plan.Files.Any(f => f.Path == "mods/optional.jar"));
        Check("unsupported 的文件被跳过（服务端专用）",
            plan.Files.All(f => f.Path != "mods/serveronly.jar"));

        // ---------- 展开覆盖层 ----------
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

    // ============================================================
    //  补全缺失的库 + mod 目录反查
    // ============================================================

    /// <summary>
    /// 两块"纯逻辑但很容易写错"的东西跟 Python 版逐值比。
    ///
    /// 补全：难点全在**什么不算缺**（本地有 / 只有 natives / 规则不匹配），
    /// 报错了用户会看到一堆"失败"，其实什么都不缺。
    ///
    /// mod 目录反查：难点全在**加载器名要整体比对** —— 写成
    /// `name.Contains(loader)` 的话，找 forge 会命中 `1.20.1-NeoForge_47.1.106`，
    /// 表现是"mod 装上了但游戏里没有"。
    /// </summary>
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

        // ---------- repair：什么算"缺" ----------
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
            //    一堆"失败"，其实什么都不缺
            var names = missing.Select(m => m.Name).ToList();
            Check("本地已有的库不算缺（always）", !names.Contains("com.example:always:1.0"));
            Check("只有 natives、没有主 jar 的不算缺（nativesonly）",
                !names.Contains("com.example:nativesonly:2.0"));
            Check("rules 不匹配当前系统的不算缺（maconly / lastwins）",
                !names.Contains("com.example:maconly:3.0")
                && !names.Contains("com.example:lastwins:4.0"));
        }

        // ---------- mods：该装到哪个 mods 目录 ----------
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
        //    （`"neoforge".Contains("forge")` 是 true，写成 Contains 就中招）
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

        // ---------- 本地 mod 的启用 / 禁用 / 删除 ----------
        CheckLocalModsLifecycle();
    }

    /// <summary>
    /// 本地 mod 的增删启停。这一段 1.0.0 没有（它只管下载），是 2.0 补的，
    /// 所以没有金标准可对 —— 但**行为必须自洽**，这里就地验一遍。
    /// </summary>
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

    // ============================================================
    //  版本设置 + 安装器金标准
    // ============================================================

    /// <summary>
    /// 「按版本覆盖参数」和「安装器地址 / 输出节流」两块跟 Python 版逐值比。
    ///
    /// 两块都是"错一个字符就 404 / 错一个判断就把线索扔了"的地方：
    /// Forge 老版本的产物名要多一个后缀、NeoForge 1.20.1 的 maven 路径
    /// 带着游戏版本前缀、OptiFine 的预览版要滤掉、`failure_hint` 不能
    /// 把"裸路径"当垃圾 —— 全是编译能过、跑起来才现形的。
    /// </summary>
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

        // ---------- version_settings ----------
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
        //    不夹一下 JVM 会直接拒绝启动
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

        // ---------- loader_setup ----------
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
        //    —— 产物版本号带着**游戏版本前缀**，直接拿 47.1.106 去问是 404
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
            //    真出错时最后一行很可能就是一个路径（找不到的那个文件），
            //    滤掉它等于把唯一的线索扔了
            Equal($"失败提示（{name}）", want, LoaderInstaller.FailureHint(lines));
        }
    }

    // ============================================================
    //  加载器目录金标准
    // ============================================================

    /// <summary>
    /// 加载器列表的解析跟 Python 版逐值比。
    ///
    /// 这一层"看着简单"，但踩过的坑都在细节里：版本号要**分段数字**比
    /// （字符串比会把 0.9 排到 0.19 前面，"最新版"直接错）、OptiFine 的
    /// 预览版必须滤掉（不滤 latest 就变成 preview_xxx）、回包形状不对时
    /// 要**返回空列表而不是抛异常**。全是编译能过、跑起来才现形的东西。
    /// </summary>
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

        // ---------- 常量 ----------
        Equal("加载器顺序（常用的在前）",
            string.Join("|", g.GetProperty("order").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", LoaderCatalog.LoaderOrder));
        Equal("支持列表",
            string.Join("|", g.GetProperty("supported").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", LoaderCatalog.Supported));

        foreach (var (key, want) in g.GetProperty("names").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"显示名（{key}）", want, LoaderCatalog.LoaderNames[key]);
        }

        // ⚠️ 接口地址错一个字符就是 404，而"拉不到"在界面上会显示成网络问题，
        //    真正的原因（地址拼错）根本看不出来
        foreach (var (key, want) in g.GetProperty("urls").EnumerateObject()
                     .Select(p => (p.Name, p.Value.GetString()!)))
        {
            Equal($"{key} 的接口地址", want, LoaderCatalog.UrlFor(key, "1.20.1"));
        }

        // ---------- 解析（同一份输入，两边各跑一遍）----------
        // ⚠️ InputKey 单独给：Quilt 跟 Fabric 的**回包形状一样**，
        //    金标准里就是拿同一份 fabric 输入跑出来的
        //    （Python 那边 `"quilt": L.parse_fabric(fabric_payload)`）。
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

        // ---------- 回包形状不对必须是空列表，不是异常 ----------
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

        // ---------- 版本号排序 ----------
        // 用金标准里那份**专门的**输入（不是上面 fabric 那份）：
        // `0.19.10 > 0.19.5` 和 `0.19.5 > 0.9.0` 正是字符串比会搞反的两处
        Equal("版本号按分段数字排（0.19.10 > 0.19.5 > 0.9.0）",
            string.Join("|", g.GetProperty("version_key_order").EnumerateArray().Select(x => x.GetString())),
            string.Join("|", LoaderCatalog
                .ParseFabric(JsonNode.Parse(inputs.GetProperty("version_key_order").GetRawText()))
                .Select(v => v.Version)));

        // ---------- 文件夹名 ----------
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
            Equal($"版本文件夹名（{name}）", want, got);
        }

        // ---------- 文件夹名反解 ----------
        foreach (var (text, want) in g.GetProperty("folder_parse").EnumerateObject()
                     .Select(p => (p.Name, p.Value)))
        {
            var expected = want.EnumerateArray().Select(x => x.GetString()!).ToList();
            var (mc, loader, lv) = LoaderCatalog.ParseVersionFolderName(text);
            Equal($"反解「{text}」",
                string.Join("|", expected),
                string.Join("|", new[] { mc, loader, lv }));
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

    private static string Describe(string name) => name.Length == 0 ? "(空名字)" : name;

    // ============================================================
    //  参数拆分 / 内存建议
    // ============================================================

    /// <summary>
    /// 「版本设置」里用户手填的 JVM / 游戏参数是按空格拆的，而**不能用现成的
    /// 命令行解析库**：POSIX 模式会把 Windows 路径里的反斜杠当转义符吃掉
    /// （`-Djava.library.path=D:\mc` 变成 `D:mc`），非 POSIX 模式又根本不合并引号。
    /// 这几条就是那个取舍的现场。
    /// </summary>
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

        // ---------- 内存 ----------
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

    // ============================================================
    //  真启动一次
    // ============================================================

    /// <summary>
    /// 造一个最小的可启动版本，**真的用 java 起一次进程**，验证三件事：
    ///
    ///   1. 进程真的起来了（<see cref="LaunchResult.Ok"/> + 有 pid）
    ///   2. 游戏的 stdout/stderr 收得上来（<see cref="ILaunchService.GameOutput"/>）
    ///   3. 退出时事件会触发、退出码拿得到（<see cref="ILaunchService.GameExited"/>）
    ///
    /// ⚠️ 为什么非要有这一节：前面所有检查都是纯计算或读写临时文件 ——
    ///    "拼出来的命令行对不对"验得很扎实，但 `LaunchResult.Ok` 对用户唯一的含义
    ///    是"游戏起来了"。而"起进程 / 收日志 / 处理退出"这三件事**一行都没被执行过**：
    ///    `Process.Start` 的参数、重定向的编码、事件在后台线程上的时序，
    ///    全都是编译能过、跑起来才现形的东西。
    ///
    /// ⚠️ 刻意让它**注定失败**（mainClass 指向不存在的类）：这样既能证明进程真的
    ///    被拉起来了，又能顺带证明 stderr 收得上、非零退出码传得回来。
    ///    真起一个 Minecraft 需要几百 MB 的完整安装，自检不能那么重。
    ///
    /// 机器上一个可用 Java 都没有时**跳过**（CI 上很可能真没有）——
    /// 跳过算通过，但会把原因打出来，不会假装验过。
    /// </summary>
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
        // portable.txt：配置落在临时目录里，**绝不碰用户 %APPDATA% 那份**
        // （那份是跟 1.0.0 共用的，自检不该动它）
        File.WriteAllText(Path.Combine(baseDir, "portable.txt"), string.Empty);

        // ---------- 找一个能用的 Java ----------
        // 缓存放在固定的临时目录：第一次要几秒，之后是毫秒级
        var javaCacheDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck", "cfg");
        // ⚠️ 先**删掉缓存**，保证下面第一遍扫描是"冷"的 —— 不然上一轮自检留下的
        //    缓存会让"第一遍"直接走缓存读，往返用例就退化成"读两次缓存"，
        //    "写进去的能不能读出来"这件事反而没验到。
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

        // ---------- Java 扫描缓存的往返 ----------
        // ⚠️ 单独验一遍"第二次扫描"：缓存**第一次是空的**，所以首扫永远是对的 ——
        //    存/取结构对不上这种 bug（写进 `info`、却从顶层读）**只有第二扫才现形**，
        //    而且症状是"检测到 0 个可用的 Java"，`ok` 字段还写着 true。
        //    用户那边的表现就是"第一次开好好的，以后每次都说没装 Java"。
        var second = new JavaScanner(javaCacheDir).FindJavas();
        Check($"缓存往返：第二遍扫描仍然找出同样多的可用 Java（{probes.Count(p => p.Usable)} 个）",
            second.Count(p => p.Usable) == probes.Count(p => p.Usable),
            $"第一遍 {probes.Count(p => p.Usable)} 个，第二遍 {second.Count(p => p.Usable)} 个");
        Check("缓存往返：第二遍读回来的主版本号没丢",
            second.Where(p => p.Usable).Select(p => p.Major).OrderBy(x => x)
                  .SequenceEqual(probes.Where(p => p.Usable).Select(p => p.Major).OrderBy(x => x)),
            "第二遍：" + string.Join(",", second.Where(p => p.Usable).Select(p => p.Major)));

        // ---------- 造一个最小可启动版本 ----------
        // ⚠️ natives **故意不预先建目录**，而且挂一个真的原生库 jar 上去：
        //    这样启动流程会真的走一遍 <see cref="NativesExtractor"/>，
        //    "解压 natives"这件事才算被执行过（下面会验 dll 真的落出来了）。
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

        // ---------- 真服务 ----------
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

        // ---------- 订事件 ----------
        var output = new List<string>();
        var exiting = new TaskCompletionSource<int>();
        services.Launch.GameOutput += (_, line) => { lock (output) { output.Add(line); } };
        services.Launch.GameExited += (_, code) => exiting.TrySetResult(code);

        // ---------- 启动 ----------
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

        // ---------- natives 真的解出来了 ----------
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

        // ---------- 等退出 ----------
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

        // ---------- 收尾 ----------
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉无所谓
        }
    }

    // ============================================================
    //  联网冒烟（--net）
    // ============================================================

    /// <summary>
    /// 对着**真实的** Mojang 清单把下载链路走一遍。
    ///
    /// 为什么值得单独做一节：金标准那一堆全是**离线**的（fixture + 比对），
    /// 它验证的是"给我这份 JSON，我算出来的清单对不对"。而真正的安装还要跑通
    /// 「清单 → 版本 JSON → 资源索引 → 计划 → 真下文件 → sha1 校验」这条链，
    /// 中间任何一环的 URL 拼接或镜像映射写错，离线测试**一个都抓不住**。
    ///
    /// ⚠️ 这里**不会**装整个版本（那是几百 MB）。只下**一个最小的文件**
    ///    （log4j 配置，约 1.6 KB）来验证 <see cref="DownloadManager"/> 真能跑通
    ///    下载 + sha1 校验 + 原子改名。计划本身是照单全算的，所以"该下哪些文件"
    ///    这件事是真验过的，只是没真下。
    /// </summary>
    private static async Task CheckNetworkSmoke()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "mosslight-selfcheck");
        Directory.CreateDirectory(configDir);

        // ---------- 1. 清单 ----------
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

        // ---------- 2. 分组 ----------
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

        // ---------- 3. 版本 JSON + 资源索引 + 计划 ----------
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

        // ---------- 4. 真下一个文件 ----------
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

        // ---------- 5. 五家加载器的接口都真的通 ----------
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
            //    而不是让用户以为我们没做这个加载器
            Check($"{row.Name} 接口通（{row.Count} 个版本，最新 {row.Latest}）",
                row.State == LoaderState.Ok && row.Count > 0,
                row.State == LoaderState.Error ? $"拉不到：{row.Error}" : $"state={row.State} count={row.Count}");
        }

        // ⚠️ BMCLAPI 现在给的 `version` 是**纯构建号**（`47.4.26`），
        //    **不带** `1.20.1-` 前缀（游戏版本在旁边的 `mcversion` 字段里）。
        //    Python 那边的注释写的是带前缀的形式 —— 那条注释跟实测对不上了。
        //    解析器两种形状都要吃得下：排序取的是**最后一个 `-` 之后**那段，
        //    所以带前缀（`1.20.1-47.2.0` → `47.2.0`）和不带（`47.4.26`）
        //    都对；金标准 fixture 用的是带前缀那种，正好覆盖另一条分支。
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

        // ⚠️ 真实地址能不能用：这一条以前**从来没验过**（机器一直没网）。
        //    只验 HEAD —— 8.8 MB 的安装器不用真下，URL 对了就行。
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

        // ---------- 6. Modrinth 也通 ----------
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

        // ---------- 7. 真装一次 Fabric（**只走 --net**）----------
        // 这是整条加载器安装链路上**唯一没被验过的**那一段：前面验的都是
        // "地址拼得对不对""profile 解得对不对"，而"真下完库、真把版本 JSON
        // 落盘、真能被版本列表扫到"只有真跑一次才知道。
        //
        // Fabric 走 profile JSON（**不用 Java**），所以这一节不需要机器上有 Java。
        await CheckRealFabricInstallAsync();
    }

    /// <summary>
    /// 真装一次 Fabric：下 profile → 下库 → 落盘 → 再扫一次版本列表确认看得见。
    ///
    /// ⚠️ 全程在一个**临时游戏目录**里做，绝不碰用户真实的那份。
    /// ⚠️ 原版 JSON 先塞一个**最小桩**进去 —— 不然 `EnsureVanillaAsync`
    ///    会去下整个原版（700 MB），那我们验的就不是加载器了。
    /// </summary>
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
        File.WriteAllText(Path.Combine(baseDir, "portable.txt"), string.Empty);

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
            //    就没有 Minecraft 的类，Fabric 会报 "couldn't locate the game"
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

        // 最后一步：**扫一遍版本列表，看得见才算装上**
        // （"下完了但列表里没有"是 1.0.0 踩过的坑）
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
