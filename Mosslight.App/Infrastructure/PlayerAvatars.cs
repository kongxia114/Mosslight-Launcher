using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosslight.Core.Services;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// Minecraft 皮肤头像（移植自 1.0.0 的 `ui/avatar.py`）。
///
/// ## 为什么抠的是 (8,8) 那一块
/// 皮肤格式规定的：64×64 的皮肤里**头部正面**就在 (8,8)，8×8 像素；
/// (40,8) 是**帽子层**（第二层）—— 很多皮肤靠它画头发 / 帽子 / 兽耳（Ari 的头发、
/// Noor 的头巾都在这一层），所以必须叠在脸上一起抠，只抠脸会"缺头发"。
///
/// ## 挑哪张默认皮肤
/// 1.19.4 之后游戏里有 9 张默认皮肤，没设皮肤的玩家显示哪张由 **UUID** 决定：
/// `floorMod(UUID.hashCode(), 9)`。我们算的离线 UUID 跟游戏一模一样
/// （`OfflinePlayer:&lt;名字&gt;` 的 MD5 + 置位），所以只要这条规则对得上，
/// 启动器里的头像就跟游戏里一致。
///
/// ⚠️ 规则和这 9 张的顺序是照 Minecraft 的实现写的，**这里没法验证**。
///    要是发现跟游戏里显示的不一样，改 <see cref="DefaultSkins"/> 的顺序或
///    <see cref="DefaultSkinFor"/> 里那一行即可 —— 都集中在这一处。
///
/// ## 想换成自己的皮肤
/// 把皮肤 png 放到**配置目录**的 `skins/&lt;档案名&gt;.png`，优先级高于内置那 9 张。
/// 不用登录皮肤站，本地文件就行（为什么不能放 assets：打包之后 assets 是只读的，
/// 用户往那儿放东西在 exe 版里根本不生效）。
/// </summary>
public static class PlayerAvatars
{
    /// <summary>9 张默认皮肤。**顺序是照 Minecraft 的 DefaultPlayerSkin 抄的**，别乱动。</summary>
    public static readonly IReadOnlyList<string> DefaultSkins = new[]
    {
        "steve", "alex", "ari", "efe", "kai", "makena", "noor", "sunny", "zuri",
    };

    /// <summary>配置目录下"放自己皮肤"的子目录名（就在启动器目录的 `Mosslight` 里）。</summary>
    public const string CustomSkinDirName = "skins";

    /// <summary>那个目录里的说明文件。</summary>
    public const string ReadmeName = "说明.txt";

    private const string ReadmeText = """
        这个目录放你自己的 Minecraft 皮肤。

        文件名 = 档案名。比如档案叫 BaLeLe，就把皮肤存成 BaLeLe.png
        （档案名在「账户」那一页能看到）

        要求：
          · 64×64 的皮肤 png（老式 64×32 也行，脸的位置一样）
          · 头像取的是 (8,8) 到 (15,15) 那块脸，帽子层 (40,8) 会叠上去

        放进来就优先用它，覆盖游戏默认的那 9 张。不用登录皮肤站，本地文件就行。
        """;

    /// <summary>
    /// Java 的 `UUID.hashCode()`。
    ///
    /// Java 的实现是：高 64 位和低 64 位异或，再折成 32 位**有符号**整数：
    ///
    ///     long hilo = mostSigBits ^ leastSigBits;
    ///     return ((int)(hilo &gt;&gt; 32)) ^ (int)hilo;
    ///
    /// ⚠️ 最后要按**有符号**看：`floorMod` 对负数的结果跟"无符号取模"差一点，
    ///    差这一点就会挑错皮肤（差 4 个 mod 9 —— 挑出来是完全不同的一个人）。
    /// </summary>
    public static int JavaUuidHash(string? uuidText)
    {
        var hex = (uuidText ?? string.Empty).Replace("-", string.Empty).Trim();
        if (hex.Length != 32)
        {
            return 0;
        }

        ulong hi = 0, lo = 0;
        for (var i = 0; i < 16; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var value))
            {
                return 0;
            }
            if (i < 8)
            {
                hi = (hi << 8) | value;
            }
            else
            {
                lo = (lo << 8) | value;
            }
        }

        var hilo = hi ^ lo;
        var folded = (uint)((hilo >> 32) ^ hilo);
        return unchecked((int)folded);
    }

    /// <summary>这个 UUID 在游戏里会显示哪张默认皮肤（解析不了就 Steve）。</summary>
    public static string DefaultSkinFor(string? uuidText)
    {
        var index = JavaUuidHash(uuidText) % DefaultSkins.Count;
        // ⚠️ 负数要折回来：C# 的 `%` 是"截断取余"，-1 % 9 = -1（Python 的 % 才是非负）
        if (index < 0)
        {
            index += DefaultSkins.Count;
        }
        return DefaultSkins[index];
    }

    /// <summary>配置目录里那个自己放的皮肤文件（没有返回 null）。</summary>
    public static string? CustomSkinFile(string configDir, string? accountName)
    {
        if (SanitizeName(accountName) is not { } name)
        {
            return null;
        }

        var path = Path.Combine(configDir, CustomSkinDirName, name + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 这个档案的头像（读不出皮肤返回 null，调用方自己退回名字首字母）。
    ///
    /// 查找顺序（**都是本地文件，不联网**），跟 1.0.0 的 <c>account_skin_path()</c> 一致：
    ///   1. 档案自带的头像文件（<see cref="Account.AvatarPath"/>）—— 给第三方皮肤站留的口子，
    ///      **现在恒为 null**（没有皮肤站登录，字段没人写）
    ///   2. 配置目录 `skins/&lt;档案名&gt;.png` —— 自己给这个档案准备的皮肤
    ///   3. 同名内置皮肤 `Assets/icons/skins/&lt;档案名&gt;.png` —— 随程序分发的（源码运行时方便）
    ///   4. 按 UUID 挑的那张默认皮肤（离线档案用现算的 UUID，跟游戏里挑的一致）
    /// </summary>
    /// <param name="account">档案。</param>
    /// <param name="configDir">配置目录（自检会传临时目录）。</param>
    /// <param name="size">显示边长（逻辑像素；头像位是 32）。</param>
    /// <param name="dpr">屏幕缩放（125% 的屏传 1.25）。</param>
    public static BitmapSource? Avatar(Account? account, string configDir, int size = 32, double dpr = 1.0)
        => LoadSkin(account, configDir) is { } skin ? ComposeFace(skin, size, dpr) : null;

    /// <summary>按名字拿内置皮肤的头像（自检 / 以后做"挑皮肤"界面用）。</summary>
    public static BitmapSource? FromEmbedded(string skinName, int size = 32, double dpr = 1.0)
        => LoadEmbedded(skinName) is { } skin ? ComposeFace(skin, size, dpr) : null;

    /// <summary>按磁盘文件拿头像（配置目录里自己放的皮肤）。</summary>
    public static BitmapSource? FromFile(string path, int size = 32, double dpr = 1.0)
        => LoadFile(path) is { } skin ? ComposeFace(skin, size, dpr) : null;

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-infrastructure-playeravatars-cs</summary>
    public static BitmapSource? ComposeFace(BitmapSource skin, int size, double dpr)
    {
        var scaleFactor = Math.Max(1.0, dpr);
        // 放大倍数取整（32 逻辑像素 @125% → 40 物理像素 = 5 倍）
        var scale = Math.Max(1, (int)Math.Round(size * scaleFactor / 8.0));
        var pixels = 8 * scale;                 // 位图的物理像素
        var dip = pixels / scaleFactor;         // 位图在界面上占的逻辑尺寸（≈ size）

        // ① 两张 8×8：脸 (8,8) + 帽子层 (40,8)。
        //    ⚠️ 老式 64×32 皮肤**没有帽子层**：不判尺寸的话 CroppedBitmap 会直接抛
        //       （超出源图范围），整张头像就画不出来。
        var face = ToStraightBgra(new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8)));
        var hat = skin.PixelWidth >= 64 && skin.PixelHeight >= 64
            ? ToStraightBgra(new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8)))
            : null;

        // ② 先在 8×8 上把帽子层叠到脸上（跟 1.0.0 的 QPainter 两遍 drawImage 等价）
        var head = hat is null ? face : BlendOver(face, hat);

        //    ⚠️ 这一步为什么不用 `dc.DrawImage` + `RenderOptions.SetBitmapScalingMode(...)`：
        // ③ 整数倍**最近邻展开**：在内存里复制像素，一个源像素摊成 scale×scale 的同色块。
        //
        //       那条路对 **RenderTargetBitmap.Render** 不生效 —— 实测 32×32 的头像里
        //       水平相邻像素完全相同的只有 19%（真正最近邻应该是 ~75%），也就是
        //       被双线性插值糊成一片了（用户 2026-10：头像看着是模糊的）。
        //       自己复制像素就完全不经过 WPF 的采样器，结果确定、与 DPI 无关。
        var expanded = ExpandNearest(head, scale);

        // ④ 1:1 画进 RenderTargetBitmap 只为**圆角裁剪**（抗锯齿），全程没有缩放，
        //    所以不会再把像素糊掉。画布坐标是逻辑像素、位图是物理像素，两者差一个 dpr，
        //    这里 `dip * scaleFactor == pixels` 正好对齐，是严格的 1:1。
        var sharp = new WriteableBitmap(
            pixels, pixels, 96 * scaleFactor, 96 * scaleFactor, PixelFormats.Bgra32, null);
        sharp.WritePixels(new Int32Rect(0, 0, pixels, pixels), expanded, pixels * 4, 0);
        sharp.Freeze();

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, dip, dip), dip / 2.0, dip / 2.0));
            dc.DrawImage(sharp, new Rect(0, 0, dip, dip));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(
            pixels, pixels, 96 * scaleFactor, 96 * scaleFactor, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>8×8 的皮肤方块 → 任意格式统一转成**直通 alpha** 的 Bgra32（8×8×4 字节）。</summary>
    private static byte[] ToStraightBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var buffer = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(buffer, converted.PixelWidth * 4, 0);
        return buffer;
    }

    /// <summary>
    /// 把帽子层按 alpha 叠到脸上（直通 alpha 的"source over"）。
    ///
    /// 皮肤里帽子层多数像素是全透明、少数完全不透明，但**半透明是允许的**
    /// （渐隐的头发、玻璃质感的头饰），所以按标准 over 合成而不是"非零就覆盖"。
    /// </summary>
    private static byte[] BlendOver(byte[] below, byte[] above)
    {
        var result = new byte[below.Length];
        for (var i = 0; i < below.Length; i += 4)
        {
            var sa = above[i + 3] / 255.0;           // 上层 alpha
            var da = below[i + 3] / 255.0;           // 下层 alpha
            var outA = sa + da * (1 - sa);

            if (outA <= 0)
            {
                continue;                            // 两层都透明 → 结果透明
            }

            for (var c = 0; c < 3; c++)
            {
                var sc = above[i + c];
                var dc2 = below[i + c];
                result[i + c] = (byte)Math.Clamp(
                    Math.Round((sc * sa + dc2 * da * (1 - sa)) / outA), 0, 255);
            }
            result[i + 3] = (byte)Math.Clamp(Math.Round(outA * 255), 0, 255);
        }
        return result;
    }

    /// <summary>
    /// 整数倍最近邻展开：每行/每列的每个像素复制 <paramref name="scale"/> 次。
    /// 输入是 8×8 的 Bgra32，输出是 (8*scale)² 的同格式缓冲区。
    /// </summary>
    private static byte[] ExpandNearest(byte[] source8, int scale)
    {
        const int srcSide = 8;
        var dstSide = srcSide * scale;
        var srcStride = srcSide * 4;
        var dstStride = dstSide * 4;
        var result = new byte[dstStride * dstSide];

        for (var sy = 0; sy < srcSide; sy++)
        {
            for (var sx = 0; sx < srcSide; sx++)
            {
                var si = sy * srcStride + sx * 4;
                // 先把这个源像素横向摊开成 scale 个
                var dx0 = sx * scale;
                for (var dy = 0; dy < scale; dy++)
                {
                    var rowStart = (sy * scale + dy) * dstStride + dx0 * 4;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var di = rowStart + dx * 4;
                        result[di] = source8[si];
                        result[di + 1] = source8[si + 1];
                        result[di + 2] = source8[si + 2];
                        result[di + 3] = source8[si + 3];
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 确保"放自己皮肤"的目录存在，顺手放一份说明。
    ///
    /// ⚠️ 这个目录**必须有人创建**：光"去找"的话用户按设置页说的路径找过去会发现
    ///    目录不存在，以为功能坏了（1.0.0 就被这么报过）。建不了就算了，不影响启动。
    /// </summary>
    public static void EnsureSkinDir(string configDir)
    {
        try
        {
            var folder = Path.Combine(configDir, CustomSkinDirName);
            Directory.CreateDirectory(folder);
            var readme = Path.Combine(folder, ReadmeName);
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme, ReadmeText);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // 目录建不了不该拦启动
        }
    }

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>
    /// 名字 → 能当文件名的皮肤名（不合法返回 null）。
    ///
    /// ⚠️ 只接受"文件名"，不接受路径：档案名来自配置文件（用户可以手改），
    ///    指到别处就是越界读别人的 png。
    /// </summary>
    private static string? SanitizeName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0
            || string.Equals(name, ".", StringComparison.Ordinal)
            || string.Equals(name, "..", StringComparison.Ordinal))
        {
            return null;
        }
        return name;
    }

    /// <summary>这个档案用哪张皮肤（生图，还没抠脸；找不到返回 null）。</summary>
    private static BitmapSource? LoadSkin(Account? account, string configDir)
    {
        if (account is null)
        {
            return null;
        }

        // 1. 档案自带的头像文件。⚠️ 现在没有代码会写这个字段（第三方皮肤站才有），
        //    但字段在模型里，留着口子总比以后改签名强。
        if (!string.IsNullOrWhiteSpace(account.AvatarPath)
            && LoadFile(account.AvatarPath!) is { } ownSkin)
        {
            return ownSkin;
        }

        // 2. 配置目录里的同名皮肤
        if (CustomSkinFile(configDir, account.Name) is { } custom
            && LoadFile(custom) is { } customSkin)
        {
            return customSkin;
        }

        // 3. 同名内置皮肤（1.0.0 允许把皮肤丢进 assets/icons/skins/<档案名>.png）
        if (SanitizeName(account.Name) is { } named && LoadEmbedded(named) is { } namedSkin)
        {
            return namedSkin;
        }

        //    ⚠️ 离线档案没写 UUID 就按离线规则现算 —— 用的是**引擎同一份实现**
        // 4. 按 UUID 挑默认那 9 张。
        //    （`LaunchRules.OfflineUuid`），别在这儿再抄一遍 MD5，抄了就会跟游戏对不上。
        var uuid = account.Kind == AccountKind.Offline && account.Uuid.Length == 0
            ? Core.Services.Engine.LaunchRules.OfflineUuid(account.Name)
            : account.Uuid;
        return LoadEmbedded(DefaultSkinFor(uuid));
    }

    /// <summary>
    /// 读一张内置皮肤。
    ///
    /// ⚠️ 走的是 <see cref="Assembly.GetManifestResourceStream(string)"/>，
    ///    **不是** XAML 那套 `pack://` 资源。为什么：皮肤要能在**控制台宿主**
    ///    里读（自检就靠这个验"9 张都能抠出脸"），而 `pack://` 这套是
    ///    WPF 的 `Application` 起来时才注册的 —— 控制台里
    ///    `new Uri("pack://...")` 会抛「Invalid port specified」，
    ///    注册了协议之后 `BitmapImage` 还会再抛「URI prefix is not recognized」
    ///    （WebRequest 那边没接 `pack`）。这些坑都只在没 Application 时出现，
    ///    界面里一切正常 —— 也就意味着**光看界面发现不了**，只有自检会红。
    ///
    /// 所以 `Mosslight.App.csproj` 里皮肤是 `<EmbeddedResource>`（其它图标仍是 `<Resource>`）。
    /// </summary>
    private static BitmapSource? LoadEmbedded(string skinName)
    {
        var name = SanitizeName(skinName)?.ToLowerInvariant();
        if (name is null || !EmbeddedSkins().TryGetValue(name, out var resource))
        {
            return null;    // 没这张（第 3 步会退到按 UUID 挑的那 9 张）
        }

        try
        {
            using var stream = typeof(PlayerAvatars).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return null;
            }

            // OnLoad：读完就撒手，别把流挂在位图上（外面可能还要改文件）
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                                      or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 内嵌皮肤的名字 → 资源全名（懒建一次）。
    ///
    /// ⚠️ 不硬编码前缀：资源名是 MSBuild 用 `$(RootNamespace)` + 相对路径拼出来的
    ///    （现在是 `Mosslight.App.Assets.icons.skins.steve.png`），
    ///    以后改 RootNamespace / 挪目录名都会变。这里按后缀匹配，改名字不会悄悄失效。
    /// </summary>
    private static Dictionary<string, string> EmbeddedSkins()
    {
        if (_embeddedSkins is not null)
        {
            return _embeddedSkins;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullName in typeof(PlayerAvatars).Assembly.GetManifestResourceNames())
        {
            if (!fullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var at = fullName.LastIndexOf(SkinResourceMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }
            var skinName = fullName[(at + SkinResourceMarker.Length)..^".png".Length];
            map[skinName] = fullName;
        }

        _embeddedSkins = map;
        return map;
    }

    /// <summary>内嵌皮肤资源名里那一段固定标记（见 <see cref="EmbeddedSkins"/>）。</summary>
    private const string SkinResourceMarker = ".Assets.icons.skins.";

    private static Dictionary<string, string>? _embeddedSkins;

    private static BitmapSource? LoadFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                                      or UriFormatException or ArgumentException
                                      or InvalidOperationException)
        {
            return null;
        }
    }
}
