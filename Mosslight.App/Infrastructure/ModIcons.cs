using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosslight.App.Infrastructure;

/// <summary>Modrinth 项目图标（搜索结果卡左边那个 64×64 的方块）。</summary>
public static class ModIcons
{
    /// <summary>卡片图标边长（逻辑像素，= QSS 里 #ModCardIcon 的 64×64）。</summary>
    public const int CardSize = 64;

    /// <summary>卡片的圆角（= QSS 的 border-radius: 8）。</summary>
    public const double CardCornerRadius = 8;

    // User-Agent：跟引擎那边用同一个，Modrinth 对匿名 UA 也算客气。
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Core.Services.Engine.DownloadManager.UserAgent);
        return client;
    }

    // (url, 目标边长) → 已经缩好的位图。
    private static readonly Dictionary<(string Url, int Target), ImageSource?> Cache = new();

    // url → 这一份的下载任务（成功是字节，失败是 null）。
    private static readonly Dictionary<string, Task<BitmapSource?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>拿一张图（没下过就下）。</summary>
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
    /// 下载 + 解码成位图；⚠️ 这里刻意**不写** ConfigureAwait(false)：续体要回到界面线程 （理由见
    /// 类注释的"线程约定"）。
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

    public static string FallbackNameFor(string projectType) => (projectType ?? string.Empty).ToLowerInvariant() switch
    {
        "modpack" => "pack",
        "resourcepack" => "GrassPath",
        "shader" => "RedstoneLampOn",
        "datapack" => "RedstoneBlock",
        _ => "forge",        // mod 以及认不出来的类型
    };
}
