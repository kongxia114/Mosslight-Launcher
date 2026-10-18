using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services;

// ============================================================================
//  收藏夹（`<配置目录>/favorites.json`）
//
//  抄 PCL2 的作业：收藏的是**项目**（模组 / 整合包 / 资源包 / 光影包），不是某个版本。
//  点进去还是原来那个详情页，只是省得下次再搜一遍。
//
//  ⚠️ 每条里存着**整份搜索命中 JSON**（`hit`），不是拆出来的字段：
//    详情页要它有 `project_id` 才能拉版本列表，图标 / 标题 / 摘要也都在里头。
//    拆字段的话 Modrinth 以后加一个字段，收藏夹里的项目就会缺一块
//    （而且只在"从收藏夹进详情页"这条路上缺 —— 最难查的那种）。
//
//  ⚠️ 文件坏了就当空的，**不抛**：收藏夹坏掉不该让人开不了启动器
//     （跟配置文件的处理一致）。
// ============================================================================

/// <summary>一条收藏。</summary>
/// <param name="ProjectType">Modrinth 的 `project_type`（`mod` / `modpack` / `resourcepack` / `shader`）。</param>
/// <param name="ProjectId">Modrinth 的项目 id（`project_id`，拉版本列表要用）。</param>
/// <param name="Title">项目名（列表上直接显示，不用再请求一次）。</param>
/// <param name="IconUrl">图标地址（可能为空）。</param>
/// <param name="Hit">原始的搜索命中 JSON —— 详情页直接拿它用。</param>
/// <param name="AddedAt">收藏时间（新的排在前面）。</param>
public sealed record FavoriteItem(
    string ProjectType,
    string ProjectId,
    string Title,
    string IconUrl,
    JsonObject Hit,
    DateTimeOffset AddedAt);

/// <summary>收藏夹的读写。</summary>
public sealed class FavoritesStore
{
    /// <summary>文件名（放配置目录里，跟 `accounts.json` / `versions.json` 同一层）。</summary>
    public const string FileName = "favorites.json";

    private readonly string _path;
    private readonly List<FavoriteItem> _items = new();

    public FavoritesStore(string configDir)
    {
        _path = Path.Combine(configDir, FileName);
        Load();
    }

    /// <summary>收藏的项目（新的在前）。</summary>
    public IReadOnlyList<FavoriteItem> Items => _items;

    /// <summary>这个项目收藏了没有。</summary>
    public bool Contains(string? projectType, string? projectId)
        => IndexOf(projectType, projectId) >= 0;

    /// <summary>
    /// 收藏 / 取消收藏。返回 true = 现在**在**收藏夹里。
    ///
    /// 已经收藏过的再点一次就移除 —— 界面上只有一颗星，点一下切一次最不容易误解。
    /// </summary>
    public bool Toggle(FavoriteItem item)
    {
        var at = IndexOf(item.ProjectType, item.ProjectId);
        if (at >= 0)
        {
            _items.RemoveAt(at);
            Save();
            return false;
        }

        _items.Insert(0, item);
        Save();
        return true;
    }

    /// <summary>从收藏夹里删掉（没收藏过就算了）。</summary>
    public void Remove(string? projectType, string? projectId)
    {
        var at = IndexOf(projectType, projectId);
        if (at < 0)
        {
            return;
        }
        _items.RemoveAt(at);
        Save();
    }

    /// <summary>清空（自检用；界面上没入口）。</summary>
    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private int IndexOf(string? projectType, string? projectId)
    {
        var type = (projectType ?? string.Empty).Trim();
        var id = (projectId ?? string.Empty).Trim();
        if (type.Length == 0 || id.Length == 0)
        {
            return -1;
        }
        return _items.FindIndex(i =>
            string.Equals(i.ProjectType, type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.ProjectId, id, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 落盘 ----------

    private void Load()
    {
        _items.Clear();
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var root = JsonNode.Parse(File.ReadAllBytes(_path)) as JsonObject;
            if (root?["items"] is not JsonArray array)
            {
                return;
            }
            foreach (var node in array)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }
                var type = Str(obj, "type");
                var id = Str(obj, "id");
                if (type.Length == 0 || id.Length == 0)
                {
                    continue;
                }
                _items.Add(new FavoriteItem(
                    type, id,
                    Str(obj, "title"),
                    Str(obj, "icon"),
                    obj["hit"] as JsonObject ?? new JsonObject(),
                    DateTimeOffset.TryParse(Str(obj, "added_at"), out var at)
                        ? at
                        : DateTimeOffset.MinValue));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException
                                      or InvalidOperationException or FormatException)
        {
            // 坏文件就当空收藏夹（下次 Save 会把它整个覆盖成合法的）
            _items.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var array = new JsonArray();
            foreach (var item in _items)
            {
                array.Add(new JsonObject
                {
                    ["type"] = item.ProjectType,
                    ["id"] = item.ProjectId,
                    ["title"] = item.Title,
                    ["icon"] = item.IconUrl,
                    ["added_at"] = item.AddedAt.ToString("O"),
                    ["hit"] = item.Hit.DeepClone(),
                });
            }

            var root = new JsonObject { ["version"] = 1, ["items"] = array };
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or InvalidOperationException)
        {
            // 写不进去只影响"这次收藏没留住"，不该把界面弄崩
        }
    }

    private static string Str(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : string.Empty;
}
