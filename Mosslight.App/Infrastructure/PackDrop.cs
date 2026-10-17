using System.IO;
using System.Windows;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 「把一个整合包拖进来」这件事里，**判断"拖进来的是不是那个文件"**的那一段。
///
/// 拖放有三个入口（整合包那张卡、主窗口任意位置、以后的别的地方），
/// 判断规则必须完全一样 —— 不然会出现"拖到卡片上能装、拖到窗口边上没反应"。
/// </summary>
internal static class PackDrop
{
    /// <summary>认得的扩展名。`.mczip` 是某些整合包站点自己的后缀，本质还是 zip。</summary>
    private static readonly string[] Extensions = { ".zip", ".mrpack", ".mczip" };

    /// <summary>
    /// 从拖放数据里取"那个整合包文件"。取不到返回 false（并把 <paramref name="path"/> 清空）。
    ///
    /// ⚠️ 只认**一个**文件：一次拖进来好几个的时候不去猜哪个是要装的那个 ——
    ///    猜错了就是"装了个别的包"，比干脆不反应糟糕得多。
    /// ⚠️ 不看后缀就放行是不行的（拖一张图进来也会被当成包），
    ///    但**也不只信后缀**：真正的格式判定在 `PackArchive.Detect` 里按内容做，
    ///    这里只是先把明显不是包的东西挡掉。
    /// </summary>
    public static bool TryGetSinglePack(IDataObject? data, out string path)
    {
        path = string.Empty;
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return false;
        }

        var candidate = files[0];
        if (!File.Exists(candidate))
        {
            return false;
        }

        var extension = Path.GetExtension(candidate);
        foreach (var known in Extensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
            {
                path = candidate;
                return true;
            }
        }
        return false;
    }
}
