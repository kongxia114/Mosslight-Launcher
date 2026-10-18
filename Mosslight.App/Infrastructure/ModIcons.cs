using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// Modrinth 项目图标（搜索结果卡左边那个 64×64 的方块）。
///
/// 移植自 1.0.0 的 `ui/pages/mods_page.py`：那边的 `_load_icon` / `_apply_icon` /
/// `_poll_icons` 三件套（起线程下、下完推队列、主线程 150ms 轮询收）——
/// WPF 这边不需要队列：`await` 回来的时候**已经站在界面线程上**
/// （没有 `ConfigureAwait(false)`，见 <see cref="FetchAsync"/>），直接赋值就行。
///
/// ## 缓存分两层，key 不一样
/// · **字节层**（<see cref="InFlight"/> 里那一份）：key 只有 url。
///   十张卡搜到同一个项目、或者同一个项目在一次搜索里出现两次，只发**一个**请求。
///   结果留在 <see cref="InFlight"/> 里**不删** —— 那就是"下载过的 url 集合"，
///   同一个 url 永远不会下第二次（任务要求的"内存缓存"就是它）。
/// · **位图层**（<see cref="Cache"/>）：key 是 (url, 目标边长)。
///   同一张图在 100% / 125% / 150% 三种缩放下要三种尺寸，
///   只缓存位图的话每次换 DPI 都要重新解码缩放。
///
/// ## 为什么按 DPI 出图
/// 跟 <see cref="VersionIcons"/> 一个道理：图标在界面上是 64 逻辑像素，
/// 屏幕 125% 时它占 80 个**物理**像素。只给 64 的话 WPF 会把图拉大 —— 看着就是糊的。
/// 所以这里算 `64 × 屏幕缩放` 个像素再交给逻辑尺寸固定为 64 的 Image
/// （DIP 坐标会自动乘回去，物理像素正好 1:1）。
///
/// ## 线程约定（⚠️ 别破）
/// 这个类**只在界面线程上用**：<see cref="LoadAsync"/> 的调用方、两层缓存、
/// <see cref="InFlight"/> 字典全都在界面线程上碰。里面的 await 没有一个
/// `ConfigureAwait(false)`，就是为了让下载回来之后的续体**回到界面线程** ——
/// 于是缓存不需要加锁、位图也不必 Freeze。
/// 谁要是从后台线程调它，得自己 Dispatcher.Invoke 回来。
///
/// ## 失败不抛
/// 图标是锦上添花：url 为空、HTTP 报错、超时、字节解不出图 —— 一律返回 null，
/// 让调用方去用兜底方块图。**绝不让一个图标把整页搜索结果带崩**。
/// </summary>
public static class ModIcons
{
    /// <summary>卡片图标边长（逻辑像素，= QSS 里 #ModCardIcon 的 64×64）。</summary>
    public const int CardSize = 64;

    /// <summary>卡片的圆角（= QSS 的 border-radius: 8）。代码里裁图要用同一个数。</summary>
    public const double CardCornerRadius = 8;

    /// <summary>User-Agent：跟引擎那边用同一个，Modrinth 对匿名 UA 也算客气。</summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Core.Services.Engine.DownloadManager.UserAgent);
        return client;
    }

    /// <summary>(url, 目标边长) → 已经缩好的位图。</summary>
    private static readonly Dictionary<(string Url, int Target), ImageSource?> Cache = new();

    /// <summary>
    /// url → 这一份的下载任务（成功是字节，失败是 null）。
    /// ⚠️ 完成后**故意不删**：它同时充当"这个 url 下过了"的记录，
    ///    删掉就等于把缓存扔了，滚动列表重建时会把同一张图重下一遍。
    /// </summary>
    private static readonly Dictionary<string, Task<BitmapSource?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// 拿一张图（没下过就下）。永远不抛，失败返回 null。
    ///
    /// <paramref name="dpiScale"/> 给 <c>VisualTreeHelper.GetDpi(this).DpiScaleX</c>
    /// （拿不到就传 1.0）。
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(string url, int size, double dpiScale = 1.0)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var scale = Math.Max(1.0, dpiScale);
        var target = Math.Max(1, (int)Math.Round(size * scale));
        var key = (url, target);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!InFlight.TryGetValue(url, out var task))
        {
            task = FetchAsync(url);
            InFlight[url] = task;
        }

        var image = await task;
        var result = image is null ? null : VersionIcons.ScaleToLongest(image, target);
        Cache[key] = result;
        return result;
    }

    /// <summary>
    /// 下载 + 解码成位图。**失败返回 null，不抛**。
    ///
    /// ⚠️ 这里刻意**不写** <c>ConfigureAwait(false)</c>：续体要回到界面线程
    ///    （理由见类注释的"线程约定"）。
    /// </summary>
    private static async Task<BitmapSource?> FetchAsync(string url)
    {
        try
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var image = DecodeBytes(bytes);

            // 像素图（放大成一坨色块）别显示出来：宁可用兜底方块图。
            // 64 逻辑像素的坑位塞一张 1×1 的图，看着就是"图标没了"。
            return image is not null && Math.Min(image.PixelWidth, image.PixelHeight) >= 16
                ? image
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or IOException or InvalidOperationException
                                      or UriFormatException or NotSupportedException)
        {
            // 下载/解码失败都当"这张图没有" —— 图标不该让整页搜不出来
            return null;
        }
    }

    /// <summary>字节 → 位图（图坏了返回 null，不抛）。</summary>
    private static BitmapSource? DecodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            // OnLoad + 立刻 Freeze：图整张读进内存，别留一个跟着 MemoryStream 的懒加载对象
            // （using 一退出流就关了，懒加载的图会解不出来）
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException
                                      or FileFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-infrastructure-modicons-cs</summary>
    public static string FallbackNameFor(string projectType) => (projectType ?? string.Empty).ToLowerInvariant() switch
    {
        "modpack" => "pack",
        "resourcepack" => "GrassPath",
        "shader" => "RedstoneLampOn",
        "datapack" => "RedstoneBlock",
        _ => "forge",        // mod 以及认不出来的类型
    };
}
