using System.Diagnostics;
using System.Text;
using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  启动游戏的真实现
//
//  这一层把前面几块拼起来：
//      VersionScanner / VersionJsonLoader  → 版本目录 + 合并好的 JSON
//      LaunchPlanBuilder                   → 完整的 java 参数列表
//      NativesExtractor                    → 解压原生库
//      JavaScanner                         → 用哪个 Java
//      然后 Process.Start，把 stdout/stderr 收上来
//
//  跟 1.0.0 的分工完全一致：**拼命令是纯函数**（LaunchPlanBuilder），
//  这个类只管"起进程、收日志、报阶段"。
// ============================================================================

/// <summary>真实的启动服务。</summary>
public sealed class RealLaunchService : ILaunchService
{
    /// <summary>
    /// 会走 profile 安装、并且能从父版本补一份 `<实例名>.jar` 的加载器。
    ///
    /// ⚠️ 只有这三家：它们的客户端 jar 就是原版那几个字节（Knot 在运行时改字节码）。
    ///    Cleanroom / OptiFine 的 jar 可能是**打过补丁的**，拿原版去补会把补丁顶掉。
    /// </summary>
    private static readonly string[] ProfileLoaderKeys = { "fabric", "quilt", "legacy_fabric" };

    private readonly AppSettings _settings;
    private readonly RealGameService _games;
    private readonly RealJavaService _java;
    private readonly VersionSettings _versionSettings;

    private Process? _process;
    private readonly object _lock = new();

    public RealLaunchService(
        AppSettings settings,
        RealGameService games,
        RealJavaService java,
        VersionSettings versionSettings)
    {
        _settings = settings;
        _games = games;
        _java = java;
        _versionSettings = versionSettings;
    }

    public bool IsPrototype => false;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>游戏退出（参数：退出码）。界面用它恢复按钮、写日志。</summary>
    public event EventHandler<int>? GameExited;

    /// <summary>游戏进程输出的一行（stdout + stderr 合流）。</summary>
    public event EventHandler<string>? GameOutput;

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-reallaunchservice-cs</summary>
    private EffectiveSettings LauncherDefaults() => new(
        JavaPath: _settings.GetString("java_path") ?? string.Empty,
        MinMemory: _settings.MinMemory,
        MaxMemory: _settings.MaxMemory,
        // 启动器没有"全局 JVM 参数"这一项，所以默认就是空
        ExtraJvmArgs: string.Empty,
        ExtraGameArgs: _settings.GetString("game_args") ?? string.Empty,
        CustomInfo: string.Empty,
        ServerAddress: string.Empty,
        Icon: string.Empty);

    /// <summary>
    /// 把这次启动**最终会用的参数**算出来（合上「版本设置」那一层覆盖），
    /// 但**不起进程**。
    ///
    /// ⚠️ 合的是"版本覆盖 &gt; 启动器默认"两层，跟 1.0.0 的
    ///    `version_settings.resolve(version_id, launcher_defaults())` 一致。
    ///    界面拿它回填「版本设置」对话框，也拿它显示"实际会用多少内存"。
    /// </summary>
    public LaunchRequest ResolveRequest(LaunchRequest request)
    {
        var versionId = string.IsNullOrEmpty(request.Version.Id)
            ? request.Version.DirName
            : request.Version.Id;

        var effective = _versionSettings.Resolve(versionId, LauncherDefaults());

        // 请求里**显式给了**的项优先（那是界面当下就想要的值），
        // 没给的才落到"版本覆盖 → 启动器默认"这条链上
        return request with
        {
            MaxMemoryMb = request.MaxMemoryMb > 0 ? request.MaxMemoryMb : effective.MaxMemory,
            MinMemoryMb = request.MinMemoryMb > 0
                ? Math.Min(request.MinMemoryMb, effective.MaxMemory)
                : effective.MinMemory,
            ExtraJvmArgs = !string.IsNullOrEmpty(request.ExtraJvmArgs)
                ? request.ExtraJvmArgs
                : effective.ExtraJvmArgs,
            ExtraGameArgs = !string.IsNullOrEmpty(request.ExtraGameArgs)
                ? request.ExtraGameArgs
                : effective.ExtraGameArgs,
            CustomInfo = !string.IsNullOrEmpty(request.CustomInfo)
                ? request.CustomInfo
                : effective.CustomInfo,
            ServerAddress = !string.IsNullOrEmpty(request.ServerAddress)
                ? request.ServerAddress
                : effective.ServerAddress,
            // 版本设置里指定了 Java 就用它（这条**排在 request.Java 前面**，
            // 见 ResolveJavaPath 的优先级说明）
            JavaPath = !string.IsNullOrEmpty(request.JavaPath)
                ? request.JavaPath
                : effective.JavaPath,
        };
    }

    public async Task<LaunchResult> LaunchAsync(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        var warnings = new List<string>();
        var missing = new List<string>();

        try
        {
            return await Task.Run(
                () => Launch(request, progress, warnings, missing, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new LaunchProgress(LaunchStage.Failed, Localizer.Tr("已取消"), 0));
            return new LaunchResult(false, Localizer.Tr("已取消"));
        }
        catch (Exception ex)
        {
            var message = Localizer.Tr("启动失败：{err}", ("err", ex.Message));
            progress?.Report(new LaunchProgress(LaunchStage.Failed, message, 0));
            return new LaunchResult(false, message, Warnings: warnings, MissingLibraries: missing);
        }
    }

    private LaunchResult Launch(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress,
        List<string> warnings,
        List<string> missing,
        CancellationToken token)
    {
        void Report(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => progress?.Report(new LaunchProgress(stage, message, fraction, indeterminate));

        // ---------- 0. 合上「版本设置」那一层覆盖 ----------
        // ⚠️ 必须在最前面：内存、JVM 参数、自动进服、自定义信息都可能被
        //    `versions.json` 里针对这个版本的设置改掉，拼命令行之前就得定下来。
        request = ResolveRequest(request);

        // ---------- 1. 解析版本 ----------
        Report(LaunchStage.Preparing, Localizer.Tr("正在准备 {name}…", ("name", request.Version.Name)), 0.05);

        var mcDir = string.IsNullOrWhiteSpace(request.MinecraftDir)
            ? _games.ResolveMinecraftDir()
            : request.MinecraftDir;

        var versionDir = !string.IsNullOrEmpty(request.VersionDir)
            ? request.VersionDir!
            : (!string.IsNullOrEmpty(request.Version.Path)
                ? request.Version.Path
                : VersionJsonLoader.VersionDir(mcDir, request.Version.Id));

        token.ThrowIfCancellationRequested();

        var vj = VersionJsonLoader.Load(mcDir, request.Version.Id);

        // ---------- 1.5 首次启动：把「游戏语言」写进 options.txt ----------
        // 只在**这个版本还没有 options.txt** 时写一次（= 第一次启动）。已经有就不碰：
        // 用户可能已经在游戏里改过语言，启动器不许覆盖（设置 → 其他 → 游戏语言）。
        // `GameVersion.GameDir` 是扫描器判出来的"游戏实际用的目录"（版本隔离时是版本目录，
        // 否则是 .minecraft），跟启动时 `--gameDir` 一致；没扫出来就退回版本目录。
        var gameDir = !string.IsNullOrEmpty(request.Version.GameDir)
            ? request.Version.GameDir!
            : versionDir;
        var gameLocale = GameLocales.Resolve(_settings.GameLanguage, Localizer.CurrentLanguage);
        if (GameLocales.SeedOptions(gameDir, gameLocale))
        {
            Report(LaunchStage.Preparing,
                Localizer.Tr("首次启动：把游戏语言设成 {lang}",
                    ("lang", GameLocales.NativeName(gameLocale))), 0.12);
        }

        // ---------- 2. 挑 Java ----------
        var javaPath = ResolveJavaPath(request, vj);
        if (javaPath is null)
        {
            var need = JavaScanner.RequiredMajor(vj);
            var message = Localizer.Tr("没找到 Java {n}，请先安装", ("n", need));
            Report(LaunchStage.Failed, message, 0);
            return new LaunchResult(false, message, Warnings: warnings, MissingLibraries: missing);
        }

        // ---------- 3. 拼启动计划 ----------
        Report(LaunchStage.CheckingFiles, Localizer.Tr("正在检查文件…"), 0.2);

        // ⚠️ 只在这三家上做。Forge/NeoForge 走安装器时已经补过；Cleanroom / OptiFine 的
        // ⚠️ `EnsureInstanceJar` 自己判"已经有 jar 就一个字节都不动"，所以一个实例只会在
        // 设计说明见 docs/design-notes.md#mosslight-core-services-reallaunchservice-cs
        if (request.Version.LoaderKey is { Length: > 0 } loaderKey
            && Array.Exists(ProfileLoaderKeys, k => string.Equals(k, loaderKey, StringComparison.OrdinalIgnoreCase))
            && LoaderInstaller.EnsureInstanceJar(mcDir, request.Version.Id))
        {
            Report(LaunchStage.CheckingFiles,
                Localizer.Tr("把原版客户端 jar 复制成实例自己的（{id}.jar）",
                    ("id", request.Version.Id)), 0.22);
        }

        var planRequest = new LaunchPlanRequest
        {
            MinecraftDir = mcDir,
            VersionId = request.Version.Id,
            VersionDir = versionDir,
            JavaPath = javaPath,
            Username = request.Account?.Name ?? "Player",
            Uuid = request.Account?.Uuid ?? string.Empty,
            AccessToken = request.Account?.Kind == AccountKind.Offline
                ? "0"
                : request.AccessToken,
            UserType = request.Account?.Kind switch
            {
                AccountKind.Microsoft => "msa",
                AccountKind.ThirdParty => "mojang",
                _ => "legacy",
            },
            MinMemoryMb = request.MinMemoryMb,
            MaxMemoryMb = request.MaxMemoryMb,
            Width = request.Width,
            Height = request.Height,
            ExtraJvmArgs = request.ExtraJvmArgs,
            ExtraGameArgs = request.ExtraGameArgs,
            ServerAddress = request.ServerAddress,
            VersionType = request.CustomInfo,
            LoaderLabel = request.Version.Loader == "Vanilla" ? string.Empty : request.Version.Loader,
            Isolated = request.Version.Isolated,
            Locale = Localizer.CurrentLanguage,
            LauncherName = AppInfo.AppName,
            LauncherVersion = AppInfo.AppVersion,
            Version = vj,
        };

        var plan = LaunchPlanBuilder.Build(planRequest);
        warnings.AddRange(plan.Warnings);
        missing.AddRange(plan.MissingLibraries);

        // 缺的库要说出来 —— 但**不拦启动**（1.0.0 也是这个取舍：
        // 有些库本来就只在服务端需要，拦下来反而把能玩的版本挡住了）
        if (plan.MissingLibraries.Count > 0)
        {
            Report(LaunchStage.CheckingFiles,
                Localizer.Tr("有 {n} 个库不在本地（不拦启动，缺东西的话游戏会自己报）",
                    ("n", plan.MissingLibraries.Count)), 0.25);
        }

        token.ThrowIfCancellationRequested();

        // ---------- 4. 解压 natives ----------
        if (plan.NeedsNatives)
        {
            Report(LaunchStage.ExtractingNatives, Localizer.Tr("正在解压原生库…"), 0.35);
            var result = NativesExtractor.EnsureNatives(
                vj, mcDir, versionDir, request.Version.Id,
                log: text => warnings.Add(text));

            if (result.Missing.Count > 0)
            {
                warnings.Add(Localizer.Tr("有 {n} 个原生库没解出来", ("n", result.Missing.Count)));
            }
        }

        token.ThrowIfCancellationRequested();

        // ---------- 5. 起进程 ----------
        Report(LaunchStage.Launching, Localizer.Tr("正在启动游戏…"), 0.8);

        var commandLine = plan.Describe();
        var processId = StartProcess(plan, mcDir, warnings);

        if (processId is null)
        {
            var message = Localizer.Tr("游戏进程没能起来 —— 检查一下 Java 路径：{path}", ("path", javaPath));
            Report(LaunchStage.Failed, message, 0);
            return new LaunchResult(false, message, CommandLine: commandLine,
                Warnings: warnings, MissingLibraries: missing);
        }

        Report(LaunchStage.Running, Localizer.Tr("游戏已启动"), 1.0);
        return new LaunchResult(
            Ok: true,
            Message: Localizer.Tr("游戏已启动（进程 {pid}）", ("pid", processId.Value)),
            ProcessId: processId,
            CommandLine: commandLine,
            MissingLibraries: missing,
            Warnings: warnings);
    }

    /// <summary>
    /// 这次用哪个 java.exe。
    ///
    /// 优先级（跟 1.0.0 的 pick_java 一致）：
    ///   1. 请求里带的（界面已经挑好了）
    ///   2. 服务自己按版本要求挑一遍
    ///   3. 配置里指定的
    /// 挑不出来返回 null —— **不要**回退到 `java` 让它去 PATH 里撞，
    /// 那会把"没装 Java"变成一个看不懂的 Win32 异常。
    /// </summary>
    private string? ResolveJavaPath(LaunchRequest request, VersionJson vj)
    {
        if (!string.IsNullOrWhiteSpace(request.JavaPath) && File.Exists(request.JavaPath))
        {
            return request.JavaPath;
        }

        if (request.Java is not null && request.Java.Usable && File.Exists(request.Java.Path))
        {
            return request.Java.Path;
        }

        // ⚠️ 界面是**异步**扫 Java 的（每个候选都要起一次 `java -version`，实测 5 秒多），
        //    用户完全可能在扫完之前就点「启动游戏」。这时候 Detected 还是空的，
        //    直接挑会得到"没找到 Java"—— 一个纯粹的假报错。
        //    所以这里补一次**同步**扫描：我们本来就在 Task.Run 的线程上，
        //    阻塞它不会卡住界面。
        if (!_java.HasScanned)
        {
            try
            {
                _java.ScanAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or OperationCanceledException)
            {
                // 扫不动就按"没找到"往下走，下面会给出说明
            }
        }

        var required = JavaScanner.RequiredMajor(vj);
        var (picked, _) = JavaScanner.PickJava(
            _java.Detected.Select(r => new JavaProbe(
                r.Path, r.Version, r.Major, r.Vendor, r.Architecture, r.Error)),
            required,
            _settings.GetString("java_path") ?? string.Empty);

        return picked?.Path;
    }

    /// <summary>
    /// 起游戏进程。
    ///
    /// 返回进程 id；起不来返回 null（错误信息已经记进 warnings）。
    /// </summary>
    private int? StartProcess(LaunchPlan plan, string mcDir, List<string> warnings)
    {
        // 工作目录**必须是版本目录**：游戏用它当 `--gameDir` 的相对基准，
        // 而且版本隔离的存档就写在里面。用 exe 目录的话存档会跑到启动器那边去。
        var workingDirectory = plan.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            workingDirectory = mcDir;
        }

        var psi = new ProcessStartInfo
        {
            FileName = plan.Java,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Args[0] 是 java.exe 自己，从 1 开始才是参数
        for (var i = 1; i < plan.Args.Count; i++)
        {
            psi.ArgumentList.Add(plan.Args[i]);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                GameOutput?.Invoke(this, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                GameOutput?.Invoke(this, e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }
            GameExited?.Invoke(this, code);

            lock (_lock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }
            process.Dispose();
        };

        try
        {
            if (!process.Start())
            {
                warnings.Add(Localizer.Tr("Process.Start 返回了 false"));
                process.Dispose();
                return null;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _process = process;
            }
            return process.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or PlatformNotSupportedException or IOException)
        {
            warnings.Add(Localizer.Tr("起进程失败：{err}", ("err", ex.Message)));
            process.Dispose();
            return null;
        }
    }

    /// <summary>把还在跑的游戏进程杀掉（退出启动器 / 用户点停止时用）。</summary>
    public void Stop()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
        }
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException)
        {
            // 已经退出了 / 杀不掉，都无所谓
        }
    }
}
