using System.Net.Http.Headers;
using System.Text;

namespace Mosslight.Core.Services.Auth;

/// <summary>
/// authlib-injector 的"有没有 / 缺了就下"。
///
/// 位置永远是 `<程序目录>/Libraries/authlib-injector.jar`（用
/// <see cref="TargetPath"/> 拼，别在别处写死路径）。
/// 启动第三方账户前调 <see cref="EnsureAsync"/>：已经有了就直接返回，没有就下。
/// </summary>
public static class AuthlibInjectorInstaller
{
    /// <summary>
    /// 下载地址：官方 v1.2.8 的 release 资产。
    /// ⚠️ 资产名里带版本号、tag 带 `v` 前缀，两者都不能省（写错过一次 → 404）。
    /// </summary>
    public const string DownloadUrl =
        "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.8/authlib-injector-1.2.8.jar";

    private const string UserAgentText = "Mosslight/2.0 (+https://github.com/kongxia114/Mosslight-Launcher-2)";

    public static string TargetPath => Path.Combine(
        AppContext.BaseDirectory, "Libraries", "authlib-injector.jar");

    public static string TargetDir => Path.Combine(AppContext.BaseDirectory, "Libraries");

    /// <summary>文件在、且不是 0 字节，才算就绪。</summary>
    public static bool IsReady
        => File.Exists(TargetPath) && new FileInfo(TargetPath).Length > 0;

    /// <summary>
    /// 确保依赖存在。成功返回 (true, null)；失败返回 (false, 原因)，调用方负责提示。
    /// `progress` 用来把"正在下载…"喂给界面的状态行。
    /// </summary>
    public static async Task<(bool Ok, string Error)> EnsureAsync(
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (IsReady)
        {
            return (true, string.Empty);
        }

        try
        {
            Directory.CreateDirectory(TargetDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return (false, $"建不了 Libraries 目录：{ex.Message}");
        }

        var tmp = TargetPath + ".tmp";
        try
        {
            progress?.Report("正在下载 authlib-injector（约 200 KB）…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentText);
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"下载失败：HTTP {(int)response.StatusCode}");
                }
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, useAsync: true);
                await source.CopyToAsync(target, token).ConfigureAwait(false);
            }

            // 下到的是不是个 jar（zip 头就是 "PK"）—— 防止网关返回一页 HTML 也被当成成功
            var length = new FileInfo(tmp).Length;
            if (length <= 0)
            {
                return (false, "下载下来的文件是空的");
            }
            await using (var probe = File.OpenRead(tmp))
            {
                var magic = new byte[2];
                _ = await probe.ReadAsync(magic, token).ConfigureAwait(false);
                if (magic[0] != (byte)'P' || magic[1] != (byte)'K')
                {
                    return (false, "下载到的不是 jar（可能被网络网关换成了网页）");
                }
            }

            File.Move(tmp, TargetPath, overwrite: true);
            progress?.Report("authlib-injector 已就绪");
            return (true, string.Empty);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"下载失败：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "下载超时");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return (false, $"保存失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件删不掉就留着，下次覆盖
            }
        }
    }
}
