using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Services.Engine;

namespace Mosslight.Core.Services;

// 账户管理（`<配置目录>/accounts.json`）；目前只有离线验证可用。
// ⚠️ 离线 UUID 必须是 `OfflinePlayer:<名字>` 的 MD5（v3 UUID），读入后一律重算，漏置版本位 / variant 位会和 PCL / HMCL 算得不一样。

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

    // 读写

    public void Reload()
    {
        _accounts.Clear();
        _current = null;
        // 读到"多账号 / 多角色"的旧文件时会被裁剪，裁过就得落盘
        var trimmedProfiles = false;

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
                        var account = new Account(
                            Name: name,
                            Kind: ParseKind(obj["type"]?.GetValue<string>()),
                            Uuid: obj["uuid"]?.GetValue<string>() ?? string.Empty,
                            AvatarPath: null,
                            CreatedAt: ParseTime(obj["created_at"]?.GetValue<string>()),
                            AccessToken: obj["access_token"]?.GetValue<string>() ?? string.Empty,
                            RefreshToken: obj["refresh_token"]?.GetValue<string>() ?? string.Empty,
                            AuthServer: obj["auth_server"]?.GetValue<string>() ?? string.Empty,
                            ClientToken: obj["client_token"]?.GetValue<string>() ?? string.Empty,
                            TokenExpiresAt: ParseTime(obj["token_expires_at"]?.GetValue<string>()),
                            AvailableProfiles: ParseProfiles(obj["profiles"] as JsonArray),
                            SelectedProfileUuid: obj["selected_profile"]?.GetValue<string>() ?? string.Empty);
                        var trimmed = TrimToSelectedProfile(account);
                        // 读到多角色的旧文件 → 裁过 → 下面要落盘（否则下次启动又读回两个）
                        trimmedProfiles |= !ReferenceEquals(trimmed, account);
                        _accounts.Add(trimmed);
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

        if (trimmedProfiles)
        {
            Save();
        }
    }

    /// <summary>修掉历史遗留的错误离线 UUID。</summary>
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
            if (account.TokenExpiresAt is { } expiresAt)
            {
                item["token_expires_at"] = expiresAt.ToString("yyyy-MM-ddTHH:mm:ss");
            }
            if (account.AvailableProfiles is { Count: > 0 } profiles)
            {
                var array = new JsonArray();
                foreach (var profile in profiles)
                {
                    array.Add(new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name });
                }
                item["profiles"] = array;
                item["selected_profile"] = account.SelectedProfileUuid.Length > 0
                    ? account.SelectedProfileUuid
                    : account.Uuid;
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

    // 对外

    /// <summary>把 LittleSkin 登录出来的角色写进账户表。</summary>
    public Account AddThirdParty(
        string name, string uuid, string accessToken, string refreshToken, string authServer,
        string clientToken = "", DateTimeOffset? tokenExpiresAt = null,
        IReadOnlyList<Auth.MinecraftProfile>? profiles = null)
    {
        var existing = _accounts.FirstOrDefault(a => a.Name == name);
        // 单身份模式：落盘前就把角色表裁成"选中的这个"（换角色要重新登录）
        var account = TrimToSelectedProfile(new Account(
            Name: name,
            Kind: AccountKind.ThirdParty,
            Uuid: uuid,
            AvatarPath: null,
            CreatedAt: existing?.CreatedAt ?? DateTimeOffset.Now,
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            AuthServer: authServer,
            ClientToken: clientToken,
            TokenExpiresAt: tokenExpiresAt,
            AvailableProfiles: profiles,
            SelectedProfileUuid: uuid));

        var index = _accounts.FindIndex(a => a.Name == name);
        if (index >= 0)
        {
            _accounts[index] = account;
        }
        else
        {
            _accounts.Add(account);
        }
        _current = account;
        Save();
        return account;
    }

    public Account AddOffline(string name) => AddOffline(name, LaunchRules.OfflineUuid(name));

    /// <summary>
    /// 离线档案，但 UUID 由调用方给（新建向导里可以选"行业规范 / 旧版 / 自定义"）。
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

        var index = _accounts.FindIndex(a => a.Name == name);
        if (index >= 0)
        {
            _accounts[index] = account;
        }
        else
        {
            _accounts.Add(account);
        }
        _current = account;
        Save();
        return account;
    }

    /// <summary>
    /// 续期成功后回写（启动前刷新 token 用）：只改 token 相关字段，其他字段原样保留。
    /// </summary>
    public Account? UpdateThirdPartyTokens(
        string name, string accessToken, string refreshToken, string clientToken,
        DateTimeOffset? tokenExpiresAt)
    {
        var index = _accounts.FindIndex(a => a.Name == name);
        if (index < 0)
        {
            return null;
        }
        var updated = _accounts[index] with
        {
            AccessToken = accessToken,
            RefreshToken = string.IsNullOrEmpty(refreshToken)
                ? _accounts[index].RefreshToken
                : refreshToken,
            ClientToken = string.IsNullOrEmpty(clientToken) ? _accounts[index].ClientToken : clientToken,
            TokenExpiresAt = tokenExpiresAt ?? _accounts[index].TokenExpiresAt,
        };
        _accounts[index] = updated;
        if (_current?.Name == name)
        {
            _current = updated;
        }
        Save();
        return updated;
    }

    /// <summary>accounts.json 里的 `profiles` 数组 → 角色列表。</summary>
    private static List<Auth.MinecraftProfile> ParseProfiles(JsonArray? array)
    {
        var list = new List<Auth.MinecraftProfile>();
        if (array is null)
        {
            return list;
        }
        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }
            var id = obj["id"]?.GetValue<string>() ?? string.Empty;
            var name = obj["name"]?.GetValue<string>() ?? string.Empty;
            if (id.Length > 0 || name.Length > 0)
            {
                list.Add(new Auth.MinecraftProfile(id, name));
            }
        }
        return list;
    }

    /// <summary>
    /// 单身份模式：只留**当前选中的那个角色**，同一个账号下的其它角色丢掉；⚠️ 跟"单账号"是同一条
    /// 规矩的两半：换角色跟换账号一样，要**重新登录**。
    /// </summary>
    private static Account TrimToSelectedProfile(Account account)
    {
        if (account.AvailableProfiles is not { Count: > 1 } profiles)
        {
            return account;
        }

        var keep = profiles.FirstOrDefault(p =>
                       string.Equals(p.Id, account.SelectedProfileUuid, StringComparison.OrdinalIgnoreCase))
                   ?? profiles.FirstOrDefault(p =>
                       string.Equals(p.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
                   ?? profiles[0];

        return account with
        {
            AvailableProfiles = new[] { keep },
            SelectedProfileUuid = keep.Id,
        };
    }

    /// <summary>
    /// 在"这个账号的多个角色"之间切换：改名字 / uuid / 选中标记，然后存盘；⚠️ 单身份模式下这条路
    /// 径平时到不了（角色表只剩一个）。
    /// </summary>
    public Account? SelectProfile(string accountName, string profileUuid)
    {
        var index = _accounts.FindIndex(a => a.Name == accountName);
        if (index < 0)
        {
            return null;
        }
        var account = _accounts[index];
        var profile = account.AvailableProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, profileUuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Uuid, profileUuid, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return null;
        }

        var updated = TrimToSelectedProfile(account with
        {
            Name = profile.Name,
            Uuid = profile.Uuid,
            SelectedProfileUuid = profile.Id,
        });
        _accounts[index] = updated;
        if (_current?.Name == accountName)
        {
            _current = updated;
        }
        Save();
        return updated;
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

    // 小工具

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
