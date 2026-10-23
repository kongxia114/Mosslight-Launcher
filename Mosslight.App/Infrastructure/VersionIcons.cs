using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 版本类型图标（版本行左边那个方块）；⚠️ 图缺一个不该让列表崩掉：认不出名字就返回 null，调用方留
/// 空位就行。
/// </summary>
public static class VersionIcons
{
    /// <summary>版本行里那个方块（逻辑像素）。</summary>
    public const int RowSize = 28;

    /// <summary>「安装选项」页顶上版本 chip 里的小图。</summary>
    public const int ChipSize = 22;

    // 加载器 → 图标名。
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

    // 版本 type → 图标名。
    private static readonly Dictionary<string, string> TypeIcons = new(StringComparer.Ordinal)
    {
        ["release"] = "vanilla",
        ["snapshot"] = "snapshot",
        ["old_alpha"] = "old",
        ["old_beta"] = "old",
    };

    /// <summary>这个版本该用哪张内置图（没有对应图就返回空串）。</summary>
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

    // 缩好的图按 (图标名, 目标像素) 缓存 —— 版本列表里同一张图会出现几十次。
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
    /// 分步缩到长边 target（先把长边对半砍到 2 倍以内，再收尾）；⚠️ 只有 28 / 22 两个尺寸需要它，
    /// 所以原来一直是 private。
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
