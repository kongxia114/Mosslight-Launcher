using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 版本类型图标（版本行左边那个方块）。
///
/// 移植自 1.0.0 的 `ui/icons.py`：图是同一批（`Assets\icons\version\*.png`，
/// 256×256 等距渲染的方块），映射表也一字不差。
///
/// ## 为什么要分步缩图
/// 256 → 28 一次缩是 8 倍，双线性滤波会丢很多细节（看着糊）。先对半砍到
/// 只剩 2 倍以内、再收尾，等于给了一个粗糙的金字塔滤波，代价只是多几次缩放。
///
/// ## 为什么要按 DPI 放大
/// 屏幕 125% 时那个 28px 的方块实际要 35 个物理像素。只给 28 的话 WPF 会把图
/// 拉大 —— 看着就是糊的。所以这里按屏幕缩放算目标像素，再把图交给一个
/// 逻辑尺寸固定为 28 的 Image（DIP 坐标会自动乘回去，物理像素正好 1:1）。
///
/// ⚠️ 图缺一个不该让列表崩掉：认不出名字就返回 null，调用方留空位就行。
/// </summary>
public static class VersionIcons
{
    /// <summary>版本行里那个方块（逻辑像素）。</summary>
    public const int RowSize = 28;

    /// <summary>「安装选项」页顶上版本 chip 里的小图。</summary>
    public const int ChipSize = 22;

    /// <summary>
    /// 加载器 → 图标名。**顺序有意义**，具体的一定要排在泛化的前面：
    ///   neoforge 里含 "forge"，optifabric 里含 "fabric"，
    ///   cleanroom 虽然不含 "forge"，但它是 Forge 的分支，排前面没坏处。
    /// </summary>
    private static readonly (string Key, string Name)[] LoaderIcons =
    {
        ("neoforge", "neoforge"),
        ("cleanroom", "cleanroom"),
        ("optifabric", "optifine"),
        ("labymod", "labymod"),
        ("forge", "forge"),
        ("fabric", "fabric"),
        ("quilt", "quilt"),
        ("optifine", "optifine"),
    };

    /// <summary>版本 type → 图标名。远古版本用圆石，快照用命令方块（开发版）。</summary>
    private static readonly Dictionary<string, string> TypeIcons = new(StringComparer.Ordinal)
    {
        ["release"] = "vanilla",
        ["snapshot"] = "snapshot",
        ["old_alpha"] = "old",
        ["old_beta"] = "old",
    };

    /// <summary>本地版本目录名里带的加载器名字。顺序同上：具体在前。</summary>
    private static readonly (string Key, string Name)[] LocalLoaderHints =
    {
        ("neoforge", "neoforge"),
        ("cleanroom", "cleanroom"),
        ("optifabric", "optifine"),
        ("labymod", "labymod"),
        ("forge", "forge"),
        ("fabric", "fabric"),
        ("quilt", "quilt"),
        ("optifine", "optifine"),
    };

    /// <summary>
    /// 这个版本该用哪张内置图（没有对应图就返回空串）。
    ///
    /// <paramref name="loader"/> 是加载器 key 或版本目录名（小写化后按包含关系认）；
    /// <paramref name="pack"/> = 整合包，用箱子图 —— 它排在加载器前面：
    /// 右边的标签已经写了加载器名，图标用箱子能多带一点信息。
    /// </summary>
    public static string NameFor(string type = "", string loader = "", bool pack = false)
    {
        if (pack)
        {
            return "pack";
        }

        var text = loader.ToLowerInvariant();
        foreach (var (key, name) in LoaderIcons)
        {
            if (text.Contains(key, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return TypeIcons.TryGetValue(type, out var typeName) ? typeName : string.Empty;
    }

    /// <summary>从版本目录名猜图标（本地多出来的版本：`1.20.1-Forge_47.2.0` 这种）。</summary>
    public static string NameForFolder(string folderName)
    {
        var text = folderName.ToLowerInvariant();
        foreach (var (key, name) in LocalLoaderHints)
        {
            if (text.Contains(key, StringComparison.Ordinal))
            {
                return name;
            }
        }
        return string.Empty;
    }

    /// <summary>缩好的图按 (图标名, 目标像素) 缓存 —— 版本列表里同一张图会出现几十次。</summary>
    private static readonly Dictionary<(string Name, int Target), ImageSource?> Cache = new();

    /// <summary>图标名 → 位图（按 size 逻辑像素 + 屏幕缩放算目标物理像素）。</summary>
    public static ImageSource? Load(string name, int size, double dpiScale = 1.0)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var scale = Math.Max(1.0, dpiScale);
        var target = Math.Max(1, (int)Math.Round(size * scale));
        var key = (name, target);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = Decode(name);
        var result = image is null ? null : ScaleStepwise(image, target);
        Cache[key] = result;
        return result;
    }

    private static BitmapSource? Decode(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/icons/version/{name}.png", UriKind.Absolute);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            // OnLoad：图整张读进内存，别留一个跟着 pack 流的懒加载对象
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException
                                      or System.IO.FileNotFoundException or ArgumentException)
        {
            // 图缺了/坏了就当没有 —— 图标是锦上添花，不该让整页起不来
            return null;
        }
    }

    /// <summary>
    /// 分步缩到长边 <paramref name="target"/>（先把长边对半砍到 2 倍以内，再收尾）。
    ///
    /// ⚠️ 只有 28 / 22 两个尺寸需要它，所以原来一直是 private。
    /// 2026-10 加社区资源卡片图标（<see cref="ModIcons"/>）时改成 internal：
    /// 那边同样是"大图缩到 64 逻辑像素 × DPI"，重写一份分步算法只会让两边
    /// 慢慢长歪（改了一边另一边还是糊的）。**行为一个字没动。**
    /// </summary>
    internal static BitmapSource ScaleToLongest(BitmapSource source, int target)
        => ScaleStepwise(source, target);

    private static BitmapSource ScaleStepwise(BitmapSource source, int target)
    {
        var current = source;
        var width = Math.Max(current.PixelWidth, current.PixelHeight);

        while (width / 2 >= target * 2)
        {
            width /= 2;
            current = Resize(current, width);
        }
        return Resize(current, target);
    }

    private static BitmapSource Resize(BitmapSource source, int target)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= 0 || longest == target)
        {
            return source;
        }

        var factor = (double)target / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }
}
