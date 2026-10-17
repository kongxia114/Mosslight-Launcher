using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  下载引擎
//  （移植自 1.0.0 的 core/download.py）
//
//  这是从两个实验合并来的：
//  - **安装编排 / 单文件下载 / 镜像映射**：TEST-3.0 的 core/download.py
//  - **每字节进度 / 滑动窗口速度 / 取消**：Downloading mod test 的 core/download.py
//
//  ## 为什么进度必须按字节
//
//  3.0 那边只报 `(done, total, failed)` 文件数 —— 下 client.jar（几十 MB）时
//  进度条会一动不动，用户以为卡死了。现在每个任务自己累计已收字节，
//  管理器汇总出"总进度 + 合计速度"。
//
//  ## 为什么用普通线程池而不是 UI 框架的线程设施
//
//  Python 那边是"销毁一个还在跑的 QThread 会让 Qt 直接终止进程"，所以用
//  普通线程 + 线程池，线程的生死归自己管。C# 这边同理用 `Task` + `SemaphoreSlim`，
//  完全不知道调用方是谁 —— 界面只是定时取 <see cref="Snapshot"/>。
//
//  ## 源：镜像优先、官方兜底
//
//  所有下载地址先过 <see cref="MirrorUrl"/> 换成 BMCLAPI，失败再回原始地址。
//  ⚠️ 每条映射都该实测过再往里加 —— mirror 猜错不会崩，但会白等一次超时。
// ============================================================================

/// <summary>下载任务的状态。界面按它上色 / 分类，所以别在别处再手写一份字符串。</summary>
public static class DownloadState
{
    public const string Waiting = "等待";
    public const string Running = "下载中";
    public const string Done = "完成";
    public const string Failed = "失败";
    public const string Cancelled = "取消";
}

/// <summary>用户点了取消（内部用，不让它冒到界面上当错误）。</summary>
internal sealed class DownloadCancelledException : Exception
{
    public DownloadCancelledException() : base("已取消")
    {
    }
}

/// <summary>一个待下载的文件：状态 / 已收字节 / 速度 / 错误。</summary>
public sealed class DownloadTask
{
    /// <summary>算速度用的滑动窗口（秒）。</summary>
    private const double SpeedWindow = 3.0;

    private readonly List<(double At, long Bytes)> _samples = new();
    private CancellationTokenSource _cancel = new();

    private double? _startedAt;

    public DownloadTask(
        string url,
        string dest,
        string sha1 = "",
        long size = 0,
        string label = "",
        string kind = "",
        IEnumerable<string>? altUrls = null)
    {
        Url = url;
        // ⚠️ **备用地址**（可选）：主地址挂了就试这些。用来给"只有一个源的
        //    整包文件"兜底 —— 典型是加载器的安装器 jar：BMCLAPI 的
        //    `/forge/download?...` 是**它自己的接口**（没法用 MirrorPairs 映射到
        //    官方 maven），它一 403 整个加载器就装不了（真跑 Forge 时撞上：
        //    68 个文件里就它挂了，装上不了）。给了备用地址之后走
        //    「镜像 → 官方 → 备用…」一路试下去。
        AltUrls = (altUrls ?? Enumerable.Empty<string>())
            .Where(u => !string.IsNullOrEmpty(u) && u != url)
            .ToList();
        Dest = dest;
        Sha1 = (sha1 ?? string.Empty).ToLowerInvariant();
        Size = size;
        Label = string.IsNullOrEmpty(label) ? Path.GetFileName(dest) : label;
        Kind = kind ?? string.Empty;
        Total = size;
    }

    public string Url { get; }

    public IReadOnlyList<string> AltUrls { get; }

    public string Dest { get; }

    public string Sha1 { get; }

    public long Size { get; }

    public string Label { get; }

    /// <summary>界面上显示成 `[库] xxx.jar` 那种前缀。</summary>
    public string Kind { get; }

    public long DoneBytes { get; internal set; }

    /// <summary>服务器给了 Content-Length 就用它。</summary>
    public long Total { get; internal set; }

    public double Speed { get; internal set; }

    public string Error { get; internal set; } = string.Empty;

    public string State { get; internal set; } = DownloadState.Waiting;

    /// <summary>本地已有且校验通过。</summary>
    public bool Skipped { get; internal set; }

    internal CancellationToken Token => _cancel.Token;

    public bool Active => State == DownloadState.Running;

    public bool Finished => State is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled;

    /// <summary>0.0~1.0；总大小未知时返回 null（界面用不确定进度条）。</summary>
    public double? Percent => Total > 0 ? Math.Min(1.0, DoneBytes / (double)Total) : null;

    public void Cancel() => _cancel.Cancel();

    /// <summary>
    /// 重置成"可以再跑一遍"的状态（<see cref="DownloadManager.RetryFailed"/> 用）。
    ///
    /// ⚠️ **必须换一个新的 CancellationTokenSource**：CTS 一旦被取消就永久处于
    ///    取消状态，复用的话重试的任务会立刻又"取消"掉 —— 表现是点了"重试"
    ///    什么都没发生。Python 那边用的是可反复 clear() 的 Event，所以没这个问题，
    ///    这里要显式换对象。
    /// </summary>
    internal void PrepareRetry()
    {
        var old = _cancel;
        _cancel = new CancellationTokenSource();
        old.Dispose();

        State = DownloadState.Waiting;
        Error = string.Empty;
        DoneBytes = 0;
        Speed = 0;
        Skipped = false;
        _samples.Clear();
        _startedAt = null;
    }

    public string StatusText()
    {
        if (Skipped)
        {
            return "已存在";
        }
        if (State == DownloadState.Running)
        {
            var pct = Percent is { } p ? $"{p * 100:F0}%" : "?";
            return $"{pct}  {DownloadManager.HumanSize(DoneBytes)}  {DownloadManager.HumanSpeed(Speed)}";
        }
        if (State == DownloadState.Done)
        {
            return $"完成  {DownloadManager.HumanSize(DoneBytes)}";
        }
        return Error.Length > 0 ? Error : State;
    }

    /// <summary>
    /// 收了一块：累计并且更新速度（最近 3 秒的滑动窗口）。
    ///
    /// ⚠️ 窗口还没建立起来时（刚开始那一两下）用"总字节 / 总耗时"兜底，
    ///    否则前一两秒速度一直显示"—"，看着像没在下。
    ///    （Python 那边 `_started_at` 以前根本没赋值过，每次拿到 now，
    ///      于是 elapsed=0 → 这里恒返回 0.0。这里按**修好之后**的行为写。）
    /// </summary>
    internal void NoteBytes(long amount)
    {
        DoneBytes += amount;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _startedAt ??= now;

        _samples.Add((now, DoneBytes));
        var cutoff = now - SpeedWindow;
        while (_samples.Count > 2 && _samples[0].At < cutoff)
        {
            _samples.RemoveAt(0);
        }

        var span = _samples[^1].At - _samples[0].At;
        if (span >= 0.25)
        {
            Speed = (_samples[^1].Bytes - _samples[0].Bytes) / span;
        }
        else
        {
            var elapsed = now - _startedAt.Value;
            Speed = elapsed > 0.05 ? DoneBytes / elapsed : 0.0;
        }
    }

    internal void MarkStarted()
    {
        _startedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}

/// <summary>一次快照，给界面定时取用。</summary>
public sealed record DownloadSnapshot(
    IReadOnlyList<DownloadTask> Tasks,
    int Done,
    int Failed,
    int Active,
    int Count,
    long Bytes,
    long Total,
    double? Percent,
    double Speed,
    bool Finished,
    /// <summary>镜像正在熔断冷却中（界面可以借此说一句"镜像不通，先走官方源"）。</summary>
    bool MirrorOff);

/// <summary>
/// 一队下载任务，用线程池并发跑。
///
/// ⚠️ 所有对外方法（<see cref="Snapshot"/> / <see cref="CancelAll"/>）都只读或用
/// 线程安全的原语，真正改状态的是工作线程 —— 界面定时取快照就够了。
/// </summary>
public sealed class DownloadManager
{
    public const string StateWaiting = DownloadState.Waiting;
    public const string StateRunning = DownloadState.Running;
    public const string StateDone = DownloadState.Done;
    public const string StateFailed = DownloadState.Failed;
    public const string StateCancelled = DownloadState.Cancelled;

    private const int Chunk = 64 * 1024;

    /// <summary>连接超时 / 每读一块的超时（秒）。</summary>
    private const int ConnectTimeout = 15;

    private const int ReadTimeout = 30;

    /// <summary>镜像熔断：连续失败几次就摘掉一段时间，冷却结束再放回来试。</summary>
    private const int MirrorTripAfter = 3;

    private const double MirrorCooldown = 60.0;

    /// <summary>同一个文件最多再试几次、每次之前等多久（线性退避）。</summary>
    private const int RetryTimes = 2;

    private const double RetryBackoff = 0.5;

    public const string UserAgent = "Mosslight/0.5 (+https://github.com/kongxia114/Mosslight-Launcher)";

    /// <summary>
    /// （官方前缀, 镜像前缀）。顺序有意义：长的、更具体的放前面。
    ///
    /// 实测记录：libraries / resources 那两条测过；piston-* 没实测，
    /// 失败会自动回退到官方，所以先留着。
    /// </summary>
    private static readonly (string Official, string Mirror)[] MirrorPairs =
    {
        ("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets"),
        ("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.neoforged.net", "https://bmclapi2.bangbang93.com/maven"),
    };

    /// <summary>
    /// ⚠️ `HttpClient` 必须是**全进程一个**：每个请求 new 一个会把 socket 耗尽
    ///    （TIME_WAIT 堆积），表现是"下着下着就再也连不上了"。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Python 那边区分连接超时（15s）和读超时（30s）。.NET 的 HttpClient.Timeout
        // 是**整个请求**的超时，分不了这么细 —— 取两者之间，再靠 CancellationToken
        // 做取消。这是有意的近似，不是疏忽。
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(ReadTimeout) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private readonly int _maxWorkers;
    private readonly bool _useMirror;
    private readonly Action<int>? _onDone;
    private readonly object _lock = new();
    private readonly List<DownloadTask> _tasks = new();

    /// <summary>目标路径 → 任务，用来去重（见 <see cref="Add"/>）。</summary>
    private readonly Dictionary<string, DownloadTask> _destIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经提交过的任务（按引用）—— 见 <see cref="Start"/> 的说明。</summary>
    private readonly HashSet<DownloadTask> _submitted = new(ReferenceEqualityComparer.Instance);

    /// <summary>总速度用的滑动窗口：[(时刻, 已下字节合计)]。见 <see cref="AggregateSpeed"/>。</summary>
    private readonly Queue<(double At, long Bytes)> _speedSamples = new();

    private int _mirrorFails;
    private double _mirrorOffUntil;

    public DownloadManager(int maxWorkers = 16, bool useMirror = true, Action<int>? onDone = null)
    {
        _maxWorkers = Math.Max(1, maxWorkers);
        _useMirror = useMirror;
        _onDone = onDone;
    }

    // ---------- 加任务 ----------

    /// <summary>
    /// 加一个任务；**同一个目标文件只留一个任务**。
    ///
    /// ⚠️ 去重不是洁癖，是必须的：版本 JSON 里同一个库真的会列两遍
    ///    （rd-132211 的 net.java.jinput:jinput-platform:2.0.5 就是两次），
    ///    两个任务写的是**同一个 .tmp**、最后又各自改名到同一个目标，
    ///    结果就是 Windows 上「文件正被另一个程序使用」，
    ///    或者两个线程往同一个 tmp 里交错写、内容悄悄坏掉。
    /// </summary>
    public DownloadTask Add(
        string url,
        string dest,
        string sha1 = "",
        long size = 0,
        string label = "",
        string kind = "",
        IEnumerable<string>? altUrls = null)
    {
        var key = Path.GetFullPath(dest);
        lock (_lock)
        {
            if (_destIndex.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var task = new DownloadTask(url, dest, sha1, size, label, kind, altUrls);
            _tasks.Add(task);
            _destIndex[key] = task;
            return task;
        }
    }

    // ---------- 跑 ----------

    /// <summary>
    /// 开始下载**所有还没提交过的任务**。
    ///
    /// ⚠️ 这里**不能**写成"已经开过就直接 return"。那样的话"一批任务"这个假设
    ///    被写死了：第一次 Start 之后再 <see cref="Add"/> 进来的新任务
    ///    **永远不会被提交**，界面上的表现是它们全都停在「等待」、
    ///    进度条再也不动，而且**不报任何错**。
    ///    （实测踩到：.mrpack 自己一批、里面的文件又一批，第二批就卡死在「等待」。）
    ///    靠 `_submitted` 记住哪些提交过，所以反复调 Start 是安全的。
    /// </summary>
    public void Start()
    {
        List<DownloadTask> pending;
        lock (_lock)
        {
            pending = _tasks.Where(t => !_submitted.Contains(t)).ToList();
            foreach (var task in pending)
            {
                _submitted.Add(task);
            }
        }
        if (pending.Count == 0)
        {
            return;
        }
        Launch(pending);
    }

    /// <summary>
    /// 把**失败的那些**任务重置后再跑一遍；返回重试了几个。
    ///
    /// 为什么不整份清单重跑一遍：几千个文件重新校验 sha1 很慢，
    /// 而用户要的只是把那几个失败的下回来。
    /// 单个文件内部本来就还有 RetryTimes 次重试，这个是"全都试完还是失败"
    /// 之后用户手动再来一轮（比如刚才网络确实断了，现在恢复）。
    /// </summary>
    public int RetryFailed()
    {
        List<DownloadTask> todo;
        lock (_lock)
        {
            todo = _tasks.Where(t => t.State == DownloadState.Failed).ToList();
            foreach (var task in todo)
            {
                // ⚠️ 取消标记没法复用（CTS 一旦取消就永久取消），所以重试要建新的。
                //    这也是 Python 那边用可复用的 Event、而这里要换一个对象的原因。
                task.PrepareRetry();
                // ⚠️ 从"已提交"里摘掉：Launch 就是按这个集合跳过已提交任务的，
                //    不摘的话重试的任务会被当成"早就提交过"而**一个都不重跑**。
                _submitted.Remove(task);
            }
            // 速度窗口清掉：不清的话下一次快照会把"上次那批字节"也算进变化量，
            // 冒出一个假的速度峰值
            _speedSamples.Clear();
        }

        if (todo.Count == 0)
        {
            return 0;
        }

        foreach (var task in todo)
        {
            _submitted.Add(task);
        }
        Launch(todo);
        return todo.Count;
    }

    private void Launch(List<DownloadTask> tasks)
    {
        _ = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(_maxWorkers);
            var running = new List<Task>(tasks.Count);

            foreach (var task in tasks)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                running.Add(Task.Run(() =>
                {
                    try
                    {
                        RunOne(task);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                // 单个任务的异常已经在 RunOne 里吞掉了，这里只是兜底
            }

            var failed = 0;
            lock (_lock)
            {
                failed = _tasks.Count(t => t.State == DownloadState.Failed);
            }
            try
            {
                _onDone?.Invoke(failed);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 回调出错不该带走整个下载
            }
        });
    }

    private void RunOne(DownloadTask task)
    {
        task.MarkStarted();
        try
        {
            RunTask(task);
        }
        catch (DownloadCancelledException)
        {
            task.State = DownloadState.Cancelled;
            QuietUnlink(TmpOf(task.Dest));
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadState.Cancelled;
            QuietUnlink(TmpOf(task.Dest));
        }
        catch (Exception ex)
        {
            // 单个失败不影响别的
            task.State = DownloadState.Failed;
            task.Error = ShortError(ex);
            QuietUnlink(TmpOf(task.Dest));
        }
    }

    private void RunTask(DownloadTask task)
    {
        var dest = task.Dest;

        // 本地已有且校验通过 → 直接跳过（增量下载的基础）
        if (File.Exists(dest))
        {
            if (task.Sha1.Length == 0)
            {
                Skip(task, dest);
                return;
            }
            if (string.Equals(Sha1File(dest, task.Token), task.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                Skip(task, dest);
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);

        // 失败重试。最常见的失败根本不是"这个文件下不到"，而是网络抖动：
        // 读超时、连接被重置、服务器 5xx —— 隔一会儿再试一次基本就过了。
        // 404 例外：那是源上本来就没有，换源有用、重试没用，直接放弃。
        Exception? last = null;
        for (var attempt = 0; attempt <= RetryTimes; attempt++)
        {
            task.Token.ThrowIfCancellationRequested();
            last = TryUrls(task);
            if (last is null)
            {
                return;                             // 成功
            }
            task.Token.ThrowIfCancellationRequested();
            if (IsNotFound(last))
            {
                break;
            }
            if (attempt < RetryTimes)
            {
                Backoff(RetryBackoff * (attempt + 1), task);
            }
        }

        throw last ?? new IOException("没有可用的下载源");
    }

    private static void Skip(DownloadTask task, string dest)
    {
        task.Skipped = true;
        task.DoneBytes = new FileInfo(dest).Length;
        task.Total = task.Total > 0 ? task.Total : task.DoneBytes;
        task.State = DownloadState.Done;
    }

    /// <summary>按「镜像 → 官方 → 备用地址」试**一轮**；成功返回 null，全挂了返回最后一个异常。</summary>
    private Exception? TryUrls(DownloadTask task)
    {
        var mirrored = MirrorUrl(task.Url, MirrorUsable);
        var urls = new List<string> { mirrored };
        if (mirrored != task.Url)
        {
            urls.Add(task.Url);
        }
        var firstIsMirror = mirrored != task.Url;

        // 备用地址也过一遍镜像映射（它可能是个官方地址，镜像能用就走镜像）
        foreach (var alt in task.AltUrls)
        {
            var altMirrored = MirrorUrl(alt, MirrorUsable);
            urls.Add(altMirrored);
            if (altMirrored != alt)
            {
                urls.Add(alt);
            }
        }

        Exception? last = null;
        for (var idx = 0; idx < urls.Count; idx++)
        {
            task.Token.ThrowIfCancellationRequested();
            var thisIsMirror = idx == 0 && firstIsMirror;

            try
            {
                Fetch(task, urls[idx]);
                if (thisIsMirror)
                {
                    NoteMirror(true);
                }
                return null;
            }
            catch (DownloadCancelledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                // 404 说明这个源没有这个文件 → 立刻换下一个源。
                // 这不算镜像的错，不该记进熔断计数。
                if (IsNotFound(ex))
                {
                    continue;
                }
                if (thisIsMirror)
                {
                    NoteMirror(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                last = ex;
                if (thisIsMirror)
                {
                    NoteMirror(false);
                }
            }
        }
        return last;
    }

    // ---------- 镜像熔断 ----------

    private bool MirrorUsable => _useMirror && Now() >= Volatile.Read(ref _mirrorOffUntil);

    /// <summary>
    /// 记一次镜像的成功/失败；连续失败够了就熔断一段时间。
    ///
    /// ⚠️ 关键：熔断的是"再试镜像"这件事，不是下载本身 ——
    ///    失败的文件照样会走官方源下下来，用户不会因此少文件。
    ///
    /// 为什么需要它：BMCLAPI 抽风时（实测过一次清单接口读超时），如果每个文件都
    /// "先试镜像、等超时、再回退官方"，764 个文件能拖到几十分钟，
    /// 用户看到的就是"进度条卡住不动"。
    /// </summary>
    private void NoteMirror(bool ok)
    {
        lock (_lock)
        {
            if (ok)
            {
                _mirrorFails = 0;
                return;
            }
            _mirrorFails++;
            if (_mirrorFails >= MirrorTripAfter)
            {
                _mirrorFails = 0;
                _mirrorOffUntil = Now() + MirrorCooldown;
            }
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // ---------- 真正收数据 ----------

    private static void Fetch(DownloadTask task, string url)
    {
        var tmp = TmpOf(task.Dest);
        task.State = DownloadState.Running;
        task.DoneBytes = 0;

        try
        {
            using var response = Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, task.Token)
                .GetAwaiter().GetResult();

            // 404 要能分辨出来（换源有用、重试没用），所以这里自己抛而不是 EnsureSuccess
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"服务器返回 {(int)response.StatusCode}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            var length = response.Content.Headers.ContentLength;
            if (length is > 0)
            {
                task.Total = length.Value;
            }

            using (var source = response.Content.ReadAsStream(task.Token))
            using (var target = File.Create(tmp))
            {
                var buffer = new byte[Chunk];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    task.Token.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    task.NoteBytes(read);
                }
            }
        }
        catch (OperationCanceledException) when (task.Token.IsCancellationRequested)
        {
            throw new DownloadCancelledException();
        }

        if (task.Sha1.Length > 0)
        {
            var actual = Sha1File(tmp, task.Token);
            if (!string.Equals(actual, task.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                QuietUnlink(tmp);
                throw new InvalidDataException("SHA1 不符（下载到的东西不对）");
            }
        }

        // 原子改名：不会留半个文件在目标位置上
        File.Move(tmp, task.Dest, overwrite: true);
        task.State = DownloadState.Done;
    }

    // ---------- 给界面看 ----------

    /// <summary>
    /// 界面每 200ms 取一次。
    ///
    /// ⚠️ 严格说它现在**不是只读**了：为了算总速度，这里会往 `_speedSamples`
    ///    里记一笔（加了锁，很快）。之所以这么做，是因为"速度"天然需要两次采样求差，
    ///    而界面正好是唯一的定时采样者。
    /// </summary>
    public DownloadSnapshot Snapshot()
    {
        List<DownloadTask> tasks;
        lock (_lock)
        {
            tasks = _tasks.ToList();
        }

        long got = 0, total = 0, streamed = 0;
        int done = 0, failed = 0, active = 0;
        foreach (var task in tasks)
        {
            got += task.DoneBytes;
            total += task.Total;
            // 速度只统计**真的从网上下来的**字节：已存在的文件（skipped）在开始那一下
            // 会一次性把整个文件大小记进 DoneBytes，算进去会在安装刚开头
            // 冒出一个几百 MB/s 的假峰值。
            if (!task.Skipped)
            {
                streamed += task.DoneBytes;
            }
            if (task.Finished)
            {
                done++;
            }
            if (task.State == DownloadState.Failed)
            {
                failed++;
            }
            if (task.Active)
            {
                active++;
            }
        }

        var snapshot = new DownloadSnapshot(
            Tasks: tasks,
            Done: done,
            Failed: failed,
            Active: active,
            Count: tasks.Count,
            Bytes: got,
            Total: total,
            Percent: total > 0 ? got / (double)total : null,
            Speed: AggregateSpeed(streamed, tasks),
            Finished: tasks.Count > 0 && done == tasks.Count,
            MirrorOff: _useMirror && Now() < Volatile.Read(ref _mirrorOffUntil));

        // 顺手广播一份给下载窗口（见 DownloadMonitor 的类注释：服务层不暴露 manager，
        // 窗口靠这一份快照画"每个文件那一行"）。
        DownloadMonitor.Publish(this, snapshot);

        return snapshot;
    }

    /// <summary>
    /// 总速度：用"所有任务已下字节合计"在滑动窗口里的变化来算。
    ///
    /// 为什么不是把每个任务的速度加起来（原来就是那么算的）：
    /// 单个任务的速度是"它自己最近几秒的窗口"，而小文件从开始到结束
    /// 常常不到 0.25 秒 —— 这种任务**整个生命周期里速度都是 0**。
    /// 而 Minecraft 的下载里绝大多数就是几十 KB 的小文件（资源文件尤其），
    /// 于是一批小文件同时下的时候"总速度"就是 0，界面显示成"—"，
    /// 看着像断了 —— 其实字节一直在下。这就是"速度断断续续、
    /// 有时候直接不显示"的根因。
    ///
    /// 改成看**总量**之后：只要字节在流就不可能突然变 0；
    /// 真的不动了（下完了 / 卡住了）才归零，那才是这个数字该有的含义。
    /// </summary>
    private double AggregateSpeed(long streamed, List<DownloadTask> tasks)
    {
        var now = Now();
        double firstAt;
        long firstBytes;

        lock (_lock)
        {
            _speedSamples.Enqueue((now, streamed));
            var cutoff = now - 3.0;
            while (_speedSamples.Count > 2 && _speedSamples.Peek().At < cutoff)
            {
                _speedSamples.Dequeue();
            }
            (firstAt, firstBytes) = _speedSamples.Peek();
        }

        var span = now - firstAt;
        if (span >= 0.25)
        {
            return Math.Max(0, (streamed - firstBytes) / span);
        }
        // 窗口还没建立（刚开始下）：退回"各任务速度相加"，总比显示 0 强
        return tasks.Where(t => t.Active).Sum(t => t.Speed);
    }

    public void CancelAll()
    {
        List<DownloadTask> tasks;
        lock (_lock)
        {
            tasks = _tasks.ToList();
        }
        foreach (var task in tasks)
        {
            if (!task.Finished)
            {
                task.Cancel();
            }
        }
    }

    /// <summary>
    /// 等所有任务收尾；超时返回 false。
    ///
    /// ⚠️ 关窗口 / 退出前**一定要调**：线程还在跑就被销毁，轻则文件写坏，
    ///    重则进程退出时被强杀。C# 这边最坏情况是进程退出时线程被掐掉，
    ///    不会像 Qt 那样带上整个框架一起崩，但留着半个文件一样难受。
    /// </summary>
    public bool WaitAll(TimeSpan timeout)
    {
        var deadline = Now() + timeout.TotalSeconds;
        while (Now() < deadline)
        {
            lock (_lock)
            {
                if (_tasks.All(t => t.Finished))
                {
                    return true;
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }

    // ---------- 小工具 ----------

    /// <summary>
    /// 把官方地址换成镜像地址；换不了 / 关掉镜像就原样返回。
    /// </summary>
    public static string MirrorUrl(string url, bool enabled = true)
    {
        if (string.IsNullOrEmpty(url) || !enabled)
        {
            return url;
        }
        foreach (var (official, mirror) in MirrorPairs)
        {
            if (url.StartsWith(official, StringComparison.OrdinalIgnoreCase))
            {
                return mirror + url[official.Length..];
            }
        }
        return url;
    }

    /// <summary>`1234567` → `"1.18 MB"`。</summary>
    public static string HumanSize(long bytes)
    {
        double n = bytes;
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (n < 1024 || unit == "GB")
            {
                return unit == "B" ? $"{n:F0} {unit}" : $"{n:F2} {unit}";
            }
            n /= 1024;
        }
        return $"{n:F2} GB";
    }

    /// <summary>字节/秒 → `"1.2 MB/s"`；速度还是 0 时给 `"—"`（免得显示 `"0 B/s"` 像卡住了）。</summary>
    public static string HumanSpeed(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "—" : HumanSize((long)bytesPerSecond) + "/s";

    /// <summary>算文件的 sha1（取消时抛 OperationCanceledException）。</summary>
    public static string Sha1File(string path, CancellationToken token = default)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    /// <summary>404：这个源上就没有这个文件。换源有用，重试没用。</summary>
    private static bool IsNotFound(Exception ex)
        => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };

    /// <summary>把网络异常翻成人话（别把堆栈甩给用户）。</summary>
    public static string ShortError(Exception ex) => ex switch
    {
        DownloadCancelledException => "已取消",
        // ⚠️ TaskCanceledException 必须排在 OperationCanceledException **前面**：
        //    它是后者的子类，反过来写的话这一条永远轮不到（HttpClient 超时抛的就是它）。
        TaskCanceledException => "连接超时",
        OperationCanceledException => "已取消",
        HttpRequestException { StatusCode: { } code } => $"服务器返回 {(int)code}",
        HttpRequestException => "网络连不上",
        InvalidDataException data => data.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>
    /// 等一会儿再重试。
    ///
    /// ⚠️ 拆成小段睡：直接睡满的话用户点了取消要等两秒才停，
    ///    界面上会像卡住。被取消要立刻抛，别把"取消"变成"失败"。
    /// </summary>
    private static void Backoff(double seconds, DownloadTask task)
    {
        var end = Now() + seconds;
        while (Now() < end)
        {
            if (task.Token.IsCancellationRequested)
            {
                throw new DownloadCancelledException();
            }
            Thread.Sleep(50);
        }
    }

    private static string TmpOf(string dest) => dest + ".tmp";

    private static void QuietUnlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就算了，下次下载会覆盖它
        }
    }
}
