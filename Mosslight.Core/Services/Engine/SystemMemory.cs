using System.Runtime.InteropServices;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  系统内存查询 + 给游戏的内存建议
//  （移植自 1.0.0 的 core/memory.py）
//
//  Windows 上 `GlobalMemoryStatusEx` 一次就能拿到总量 / 可用 / 占用百分比，
//  不用装额外的库（少一个依赖，打包也小一截）。
//
//  **这里只做"显示和建议"，不碰启动参数**：`-Xmx` 加多少是用户的事
//  （GC 参数在不同 Java 大版本之间差异很大，加错了 JVM 直接拒绝启动）。
// ============================================================================

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

    /// <summary>超过物理内存的这个比例就画一条红线 —— 再往上容易开始换页，表现是越玩越卡。</summary>
    public const double PageRiskRatio = 0.6;

    /// <summary>内存滑块的档位（MB）。512 的倍数最省事：拖出来的值不会出现 3586 这种数。</summary>
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

    /// <summary>当前系统内存情况。读不出来就返回全 0（用 <see cref="MemoryInfo.Ok"/> 判断）。</summary>
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

    /// <summary>
    /// 取整到 512 的档位 —— 滑块拖出来的值不会是整数档。
    ///
    /// 界面上所有能改内存的地方都要过这一道（设置页、版本设置页），
    /// 不然配置里会出现用户没想选的数字。
    /// </summary>
    public static int SnapMemory(int value)
    {
        // ⚠️ 用 `Math.Round(..., MidpointRounding.AwayFromZero)` 而不是默认的银行家舍入：
        //    Python 那边 `round()` 是银行家舍入（round(0.5) == 0），但这里的输入是
        //    整数除法后的结果，两边的差异只在 n*512+256 这种正好半点的情况下出现 ——
        //    而滑块本身就只能停在 512 的档位上，走不到半点。
        //    选 AwayFromZero 是为了让"往上取整"符合用户直觉。
        var stepped = (int)Math.Round(value / (double)MemoryStep, MidpointRounding.AwayFromZero) * MemoryStep;
        return Math.Max(MemoryMin, stepped);
    }

    /// <summary>
    /// 堆之外大概还要占多少（Metaspace、代码缓存、GC 结构、直接内存…）。
    ///
    /// 只是个经验估算：几百兆固定开销 + 堆的百分之几。
    /// 界面上一定要写"约"—— `-Xmx` 是**上限**不是预留，别让用户以为配了 4G 就实占 4G。
    /// </summary>
    public static int JvmOverheadMb(int heapMb)
        => (int)(320 + Math.Max(0, heapMb) * 0.06);

    /// <summary>
    /// 按物理内存给一个建议的最大堆。
    ///
    /// 经验值：物理内存的 1/4 起步，再按版本类型夹到合理区间，
    /// 并且**不能超过物理内存的 60%**（超了就是拿换页换内存，越玩越卡）。
    /// 小内存机器（4G 以下）另算 —— 那种机器怎么配都紧张。
    /// </summary>
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
