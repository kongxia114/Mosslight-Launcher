using System.IO;
using System.Windows;

namespace Mosslight.App.Infrastructure;

/// <summary>「把一个整合包拖进来」这件事里，**判断"拖进来的是不是那个文件"**的那一段。</summary>
internal static class PackDrop
{
    // 认得的扩展名。
    private static readonly string[] Extensions = { ".zip", ".mrpack", ".mczip" };

    /// <summary>
    /// 从拖放数据里取"那个整合包文件"； 只认**一个**文件：一次拖进来好几个的时候不去猜哪个是要装
    /// 的那个 —— 猜错了就是"装了个别的包"，比干脆不反应糟糕得多。
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
