using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Services.Engine;

namespace Mosslight.Core.Services;

// ============================================================================
//  账户管理（<配置目录>/accounts.json）
//  （移植自 1.0.0 的 core/accounts.py）
//
//  目前只有离线验证可用；正版 / 第三方验证只占了 UI 的位置。
//
//  ⚠️ 离线 UUID 必须是 `OfflinePlayer:<名字>` 的 MD5（v3 UUID）。
//    早期版本是把 MD5 直接拼成 UUID 的，**漏了置版本位和 variant 位** ——
//    那些值不是合法 UUID，而且和 PCL / HMCL 算出来的不一样，
//    会让同一个玩家在存档和服务器里被当成两个人。
//    所以读进来之后一律**重算一遍**（离线 UUID 是玩家名的纯函数，不用问用户）。
// ============================================================================

/// <summary>账户的读写（`accounts.json`）。</summary>
public sealed class RealAccountService : IAccountService
{
    private readonly string _filePath;
    private readonly List<Account> _accounts = new();
    private Account? _current;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public RealAccountService(string configDir)
    {
        _filePath = Path.Combine(configDir, "accounts.json");
        Reload();
    }

    public IReadOnlyList<Account> All => _accounts;

    public Account? Current => _current;

    public string FilePath => _filePath;

    // ---------- 读写 ----------

    public void Reload()
    {
        _accounts.Clear();
        _current = null;

        if (File.Exists(_filePath))
        {
            try
            {
                // 读字节，让 json 解析器自己处理 BOM
                var root = JsonIO.ParseBytes(File.ReadAllBytes(_filePath)) as JsonObject;
                if (root?["accounts"] is JsonArray array)
                {
                    foreach (var node in array)
                    {
                        if (node is not JsonObject obj)
                        {
                            continue;
                        }
                        var name = obj["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }
                        _accounts.Add(new Account(
                            Name: name,
                            Kind: ParseKind(obj["type"]?.GetValue<string>()),
                            Uuid: obj["uuid"]?.GetValue<string>() ?? string.Empty,
                            AvatarPath: null,
                            CreatedAt: ParseTime(obj["created_at"]?.GetValue<string>()),
                            AccessToken: obj["access_token"]?.GetValue<string>() ?? string.Empty,
                            RefreshToken: obj["refresh_token"]?.GetValue<string>() ?? string.Empty,
                            AuthServer: obj["auth_server"]?.GetValue<string>() ?? string.Empty,
                            ClientToken: obj["client_token"]?.GetValue<string>() ?? string.Empty));
                    }
                }
                var currentName = root?["current"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(currentName))
                {
                    _current = _accounts.FirstOrDefault(a => a.Name == currentName);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                          or InvalidOperationException or FormatException or NotSupportedException)
            {
                // 读不出来就当没有账户 —— 一个坏文件不该让启动器起不来
                System.Diagnostics.Debug.WriteLine($"[Accounts] load failed: {ex.Message}");
                _accounts.Clear();
                _current = null;
            }
        }

        RepairOfflineUuids();
    }

    /// <summary>
    /// 修掉历史遗留的错误离线 UUID。
    ///
    /// 离线 UUID 是玩家名的纯函数，重算一次就行，不用问用户。
    /// </summary>
    private void RepairOfflineUuids()
    {
        var changed = 0;
        for (var i = 0; i < _accounts.Count; i++)
        {
            var account = _accounts[i];
            if (account.Kind != AccountKind.Offline)
            {
                continue;
            }
            var correct = LaunchRules.OfflineUuid(account.Name);
            if (!string.Equals(account.Uuid, correct, StringComparison.OrdinalIgnoreCase))
            {
                _accounts[i] = account with { Uuid = correct };
                changed++;
            }
        }
        if (changed > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[Accounts] 修正了 {changed} 个离线 UUID（旧值漏了版本位）");
            Save();
        }
    }

    public bool Save()
    {
        var accounts = new JsonArray();
        foreach (var account in _accounts)
        {
            var item = new JsonObject
            {
                ["type"] = KindName(account.Kind),
                ["name"] = account.Name,
                ["uuid"] = account.Uuid,
                ["created_at"] = (account.CreatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-ddTHH:mm:ss"),
            };
            // 第三方登录态才写这三项；离线账户的 accounts.json 不该多出空字段
            if (account.AccessToken.Length > 0)
            {
                item["access_token"] = account.AccessToken;
            }
            if (account.RefreshToken.Length > 0)
            {
                item["refresh_token"] = account.RefreshToken;
            }
            if (account.AuthServer.Length > 0)
            {
                item["auth_server"] = account.AuthServer;
            }
            if (account.ClientToken.Length > 0)
            {
                item["client_token"] = account.ClientToken;
            }
            accounts.Add(item);
        }

        var payload = new JsonObject
        {
            ["accounts"] = accounts,
            ["current"] = _current?.Name,
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // 原子写：先写 .tmp 再替换，中途断电不会把账户列表写成半个文件
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, payload.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Move(tmp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Accounts] save failed: {ex.Message}");
            return false;
        }
    }

    // ---------- 对外 ----------

    /// <summary>
    /// 把 LittleSkin 登录出来的角色写进账户表。
    /// 同名已存在就只刷新 token / uuid（用户重新登录不该多出一行）。
    /// </summary>
    public Account AddThirdParty(
        string name, string uuid, string accessToken, string refreshToken, string authServer,
        string clientToken = "")
    {
        var existing = _accounts.FirstOrDefault(a => a.Name == name);
        var account = new Account(
            Name: name,
            Kind: AccountKind.ThirdParty,
            Uuid: uuid,
            AvatarPath: null,
            CreatedAt: existing?.CreatedAt ?? DateTimeOffset.Now,
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            AuthServer: authServer,
            ClientToken: clientToken);

        if (existing is not null)
        {
            _accounts[_accounts.IndexOf(existing)] = account;
        }
        else
        {
            _accounts.Add(account);
        }
        _current ??= account;
        Save();
        return account;
    }

    public Account AddOffline(string name) => AddOffline(name, LaunchRules.OfflineUuid(name));

    /// <summary>
    /// 离线档案，但 UUID 由调用方给（新建向导里可以选"行业规范 / 旧版 / 自定义"）。
    /// 空 uuid 会退回行业规范算法。
    /// </summary>
    public Account AddOffline(string name, string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            uuid = LaunchRules.OfflineUuid(name);
        }
        var account = new Account(
            Name: name,
            Kind: AccountKind.Offline,
            Uuid: uuid,
            CreatedAt: DateTimeOffset.Now);

        // 同名覆盖
        _accounts.RemoveAll(a => a.Name == name);
        _accounts.Add(account);
        _current = account;
        Save();
        return account;
    }

    public void Select(Account? account)
    {
        if (account is not null && !_accounts.Any(a => a.Name == account.Name))
        {
            return;
        }
        _current = account;
        Save();
    }

    public void Remove(string name)
    {
        _accounts.RemoveAll(a => a.Name == name);
        if (_current?.Name == name)
        {
            _current = _accounts.FirstOrDefault();
        }
        Save();
    }

    // ---------- 小工具 ----------

    private static AccountKind ParseKind(string? type) => type switch
    {
        "microsoft" => AccountKind.Microsoft,
        "thirdparty" => AccountKind.ThirdParty,
        _ => AccountKind.Offline,
    };

    private static string KindName(AccountKind kind) => kind switch
    {
        AccountKind.Microsoft => "microsoft",
        AccountKind.ThirdParty => "thirdparty",
        _ => "offline",
    };

    private static DateTimeOffset? ParseTime(string? text)
        => DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;

    /// <summary>账户类型的显示名（每次调用都过 Tr()，所以跟着语言走）。</summary>
    public static string TypeLabel(AccountKind kind) => Localization.Localizer.Tr(kind switch
    {
        AccountKind.Microsoft => "正版验证",
        AccountKind.ThirdParty => "第三方验证",
        _ => "离线验证",
    });
}
