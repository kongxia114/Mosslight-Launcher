using System.Runtime.InteropServices;

namespace Mosslight.Core.Services.Engine;

// 系统内存查询 + 给游戏的内存建议（GlobalMemoryStatusEx，无额外依赖）。
// 这里只做显示和建议，不碰 -Xmx 等启动参数。

/// <summary>系统内存情况。</summary>
public sealed record MemoryInfo(
    int TotalMb = 0,
    int AvailableMb = 0,
    int PercentUsed = 0)
{
    public bool Ok => TotalMb > 0;

    public int UsedMb => Math.Max(0, TotalMb - AvailableMb);
}

/// <summary>系统内存查询与建议。</summary>
public static class SystemMemory
{
    private const long Mb = 1024 * 1024;

    /// <summary>建议值的天花板 / 地板（MB）：原版。</summary>
    public static readonly (int Low, int High) VanillaRange = (2048, 4096);

    /// <summary>建议值的天花板 / 地板（MB）：带模组。</summary>
    public static readonly (int Low, int High) ModdedRange = (4096, 12288);

    /// <summary>
    /// 超过物理内存的这个比例就画一条红线 —— 再往上容易开始换页，表现是越玩越卡。
    /// </summary>
    public const double PageRiskRatio = 0.6;

    /// <summary>内存滑块的档位（MB）。</summary>
    public const int MemoryStep = 512;

    public const int MemoryMin = 512;

    public const int MemoryMax = 65536;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>当前系统内存情况。</summary>
    public static MemoryInfo Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MemoryInfo();
        }
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return new MemoryInfo();
            }
            return new MemoryInfo(
                TotalMb: (int)((long)status.ullTotalPhys / Mb),
                AvailableMb: (int)((long)status.ullAvailPhys / Mb),
                PercentUsed: (int)status.dwMemoryLoad);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                      or MarshalDirectiveException)
        {
            // 拿不到就算了，界面退回"只按你填的值画"
            return new MemoryInfo();
        }
    }

    /// <summary>取整到 512 的档位 —— 滑块拖出来的值不会是整数档。</summary>
    public static int SnapMemory(int value)
    {
        // ⚠️ 用 MidpointRounding.AwayFromZero 做内存档位取整，符合“往上取整”的直觉；不要改成默认银行家舍入。
        var stepped = (int)Math.Round(value / (double)MemoryStep, MidpointRounding.AwayFromZero) * MemoryStep;
        return Math.Max(MemoryMin, stepped);
    }

    /// <summary>堆之外大概还要占多少（Metaspace、代码缓存、GC 结构、直接内存…）。</summary>
    public static int JvmOverheadMb(int heapMb)
        => (int)(320 + Math.Max(0, heapMb) * 0.06);

    /// <summary>按物理内存给一个建议的最大堆。</summary>
    public static int RecommendMemory(int totalMb, bool modded = false)
    {
        var (low, high) = modded ? ModdedRange : VanillaRange;
        if (totalMb <= 0)
        {
            return low;
        }
        if (totalMb <= 4096)
        {
            // 4G 及以下：给一半，但至少 1G，别把系统挤死
            return RoundToStep(Math.Max(1024, Math.Min(totalMb / 2, 2048)));
        }
        var ceiling = (int)(totalMb * PageRiskRatio);
        var want = Math.Max(low, totalMb / 4);
        return RoundToStep(Math.Max(1024, Math.Min(want, Math.Min(high, ceiling))));
    }

    /// <summary>取整到 512 的倍数 —— 滑块和下拉框里好看，也没人会真要 3586 这种数。</summary>
    private static int RoundToStep(int value, int step = MemoryStep)
        => Math.Max(step, (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step);
}
