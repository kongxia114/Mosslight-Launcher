namespace Mosslight.Core.Services;

using Mosslight.Core.Services.Engine;

// ============================================================================
//  假实现（原型阶段）
//
//  界面能画出来、能量出来，但**点下去不会真的发生什么**。
//  每个都挂了 `IsPrototype = true`，界面据此显示"原型数据"提示 ——
//  这是刻意的：一个看起来能启动游戏、实际什么都不干的按钮，比一个
//  明说"还没接上"的按钮糟糕得多。
//
//  数据照着 1.0.0 的截图凑的（1.20.5 / Java 8 / 2048 MB / 10 个 Java），
//  这样两个版本的界面能逐像素对比。
// ============================================================================

/// <summary>版本列表的假实现。</summary>
public sealed class StubGameService : IGameService
{
    private static readonly GameVersion[] PrototypeVersions =
    {
        new("1.20.5", "1.20.5", "1.20.5", "Vanilla",
            new DateTimeOffset(2026, 8, 12, 21, 40, 0, TimeSpan.FromHours(8))),
        new("1.20.1-forge", "1.20.1 Forge", "1.20.1", "Forge",
            new DateTimeOffset(2026, 7, 30, 19, 5, 0, TimeSpan.FromHours(8))),
        new("1.19.2-fabric", "1.19.2 Fabric", "1.19.2", "Fabric",
            new DateTimeOffset(2026, 6, 18, 14, 22, 0, TimeSpan.FromHours(8))),
        new("1.21.4-neoforge", "1.21.4 NeoForge", "1.21.4", "NeoForge"),
        new("1.18.2-optifine", "1.18.2 OptiFine", "1.18.2", "OptiFine"),
        new("1.16.5", "1.16.5", "1.16.5", "Vanilla"),
        new("1.12.2-forge", "1.12.2 Forge", "1.12.2", "Forge"),
    };

    public bool IsPrototype => true;

    public IReadOnlyList<GameVersion> Installed => PrototypeVersions;

    /// <summary>假数据没有"读不了的版本"这回事。</summary>
    public IReadOnlyList<string> ScanErrors => Array.Empty<string>();

    public string MinecraftDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public Task<IReadOnlyList<GameVersion>> RefreshAsync(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<GameVersion>>(PrototypeVersions);
}

/// <summary>Java 探测的假实现。凑了 10 个，好让"检测到 10 个可用的 Java"那句话是真的。</summary>
public sealed class StubJavaService : IJavaService
{
    private static readonly JavaRuntime[] PrototypeRuntimes =
    {
        new(@"C:\Program Files\Java\jre-1.8\bin\javaw.exe", "8", "x64", IsDefault: true),
        new(@"C:\Program Files\Java\jdk-17\bin\javaw.exe", "17", "x64"),
        new(@"C:\Program Files\Java\jdk-21\bin\javaw.exe", "21", "x64"),
        new(@"C:\Program Files\Eclipse Adoptium\jdk-8.0.402.6-hotspot\bin\javaw.exe", "8", "x64"),
        new(@"C:\Program Files\Eclipse Adoptium\jdk-17.0.10.7-hotspot\bin\javaw.exe", "17", "x64"),
        new(@"C:\Program Files\Microsoft\jdk-11.0.22.7-hotspot\bin\javaw.exe", "11", "x64"),
        new(@"C:\Program Files\Microsoft\jdk-17.0.9.8-hotspot\bin\javaw.exe", "17", "x64"),
        new(@"C:\Program Files\BellSoft\LibericaJDK-21\bin\javaw.exe", "21", "x64"),
        new(@"C:\Program Files (x86)\Java\jre1.8.0_401\bin\javaw.exe", "8", "x86"),
        new(@"D:\Mosslight\runtime\jre-legacy\bin\javaw.exe", "8", "x64"),
    };

    public bool IsPrototype => true;

    public IReadOnlyList<JavaRuntime> Detected => PrototypeRuntimes;

    /// <summary>
    /// 挑 Java 的大致规则（跟 1.0.0 一致）：1.17 以下要 8，1.17~1.20.4 要 17，
    /// 1.20.5 往上要 21。挑不到就退回默认那个 —— 真实现里这里还有一堆兜底。
    /// </summary>
    public JavaRuntime? PickFor(GameVersion version)
    {
        var wanted = WantedMajor(version.MinecraftVersion);
        return PrototypeRuntimes.FirstOrDefault(j => j.Version == wanted && j.Architecture == "x64")
               ?? PrototypeRuntimes.FirstOrDefault(j => j.IsDefault);
    }

    /// <summary>
    /// 这个 MC 版本至少要哪个大版本的 Java。
    ///
    /// ⚠️ 分界线是 **1.20.5**（不是 1.21）：Minecraft 从 1.20.5 开始就要 Java 21 了，
    /// 光看次版本号（20 ≤ 20 → 17）会挑错，进游戏直接崩在启动器之外。
    /// </summary>
    public static string WantedMajor(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var minor)) return "8";
        if (minor <= 16) return "8";
        if (minor >= 21) return "21";
        if (minor < 20) return "17";

        // 1.20.x：只有 1.20.5 及以上要 21
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return patch >= 5 ? "21" : "17";
    }

    public Task<IReadOnlyList<JavaRuntime>> ScanAsync(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<JavaRuntime>>(PrototypeRuntimes);

    /// <summary>假数据没有版本 JSON 可读，按名字里的版本号猜一个。</summary>
    public int RequiredMajor(GameVersion version)
        => int.TryParse(WantedMajor(version.MinecraftVersion), out var major) ? major : 8;
}

/// <summary>账户的假实现。</summary>
public sealed class StubAccountService : IAccountService
{
    private readonly List<Account> _accounts = new();
    private Account? _current;

    public IReadOnlyList<Account> All => _accounts;

    public Account? Current => _current;

    public Account AddOffline(string name)
    {
        var account = new Account(name, AccountKind.Offline, Uuid: Guid.NewGuid().ToString("N"));
        _accounts.Add(account);
        _current = account;
        return account;
    }

    public void Select(Account? account) => _current = account;

    public void Remove(string name)
    {
        _accounts.RemoveAll(a => a.Name == name);
        if (_current?.Name == name)
        {
            _current = _accounts.FirstOrDefault();
        }
    }

    /// <summary>假实现没有落盘，重读等于什么都不做。</summary>
    public void Reload()
    {
    }
}

/// <summary>启动的假实现 —— 明确地"不干活"，并且说清楚为什么。</summary>
public sealed class StubLaunchService : ILaunchService
{
    public bool IsPrototype => true;

    /// <summary>假实现永远"没在跑"。</summary>
    public bool IsRunning => false;

    /// <summary>假实现不会起进程，所以这两个事件永远不会触发。</summary>
    public event EventHandler<int>? GameExited;

    public event EventHandler<string>? GameOutput;

    /// <summary>假实现没有"版本设置"那一层，原样返回。</summary>
    public LaunchRequest ResolveRequest(LaunchRequest request) => request;

    /// <summary>假实现没有进程可停。</summary>
    public void Stop()
    {
    }

    public async Task<LaunchResult> LaunchAsync(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        // 显式引用一下，免得"事件从未使用"的警告把真正的问题淹掉
        _ = GameExited;
        _ = GameOutput;

        // 故意走一遍阶段回调：界面的进度显示要能验证，不然等真引擎接上才发现画不出来
        var stages = new[]
        {
            new LaunchProgress(LaunchStage.Preparing, "正在准备…", 0.1),
            new LaunchProgress(LaunchStage.CheckingFiles, "正在检查文件…", 0.4),
        };
        foreach (var stage in stages)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(stage);
            await Task.Delay(120, token).ConfigureAwait(false);
        }

        progress?.Report(new LaunchProgress(LaunchStage.Failed, "原型阶段还没接引擎", 0.0));
        return new LaunchResult(
            Ok: false,
            Message: "这是 2.0 的界面原型：启动引擎还没搬过来，暂时不能真的启动游戏。");
    }
}

/// <summary>下载的假实现。</summary>
public sealed class StubDownloadService : IDownloadService
{
    public bool IsPrototype => true;

    public IReadOnlyList<string> AvailableMinecraftVersions => new[]
    {
        "1.21.4", "1.21.3", "1.21.1", "1.21", "1.20.6", "1.20.5", "1.20.4",
        "1.20.1", "1.19.4", "1.19.2", "1.18.2", "1.16.5", "1.12.2", "1.7.10",
    };

    public Task<IReadOnlyList<string>> RefreshVersionListAsync(CancellationToken token = default)
        => Task.FromResult(AvailableMinecraftVersions);

    /// <summary>假实现**不下载任何东西**，并且明确说出来 —— 一个"看起来在装、实际没装"的按钮最糟。</summary>
    public Task<InstallResult> InstallVanillaAsync(
        string minecraftVersion,
        string minecraftDir,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        progress?.Report(new LaunchProgress(LaunchStage.Failed, "原型阶段还没接下载引擎", 0.0));
        return Task.FromResult(new InstallResult(
            Ok: false,
            Message: "这是 2.0 的界面原型：下载引擎还没搬过来，暂时不能安装版本。"));
    }
}

/// <summary>加载器的假实现（给自检和"全是假数据"那条路用）。</summary>
public sealed class StubLoaderService : ILoaderService
{
    public bool IsPrototype => true;

    /// <summary>假数据里五家都"还没做" —— 界面据此显示三态里的那一条。</summary>
    public Task<IReadOnlyList<LoaderRow>> CatalogAsync(
        string minecraftVersion, CancellationToken token = default)
    {
        IReadOnlyList<LoaderRow> rows = LoaderCatalog.LoaderOrder
            .Select(key => new LoaderRow(
                key,
                LoaderCatalog.LoaderNames.TryGetValue(key, out var n) ? n : key,
                LoaderState.NotImplemented, 0, string.Empty,
                Array.Empty<LoaderVersion>(), string.Empty))
            .ToList();
        return Task.FromResult(rows);
    }

    public bool CanInstall(string loaderKey) => false;

    public Task<LoaderInstallResult> InstallAsync(
        LoaderInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        progress?.Report(new LaunchProgress(LaunchStage.Failed, "原型阶段还没接加载器安装", 0.0));
        return Task.FromResult(new LoaderInstallResult(
            Ok: false,
            Message: "这是 2.0 的界面原型：加载器安装还没搬过来。",
            NotImplemented: true));
    }
}
