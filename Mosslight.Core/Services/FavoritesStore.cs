using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services;

// 收藏夹（`<配置目录>/favorites.json`），收藏的是项目而非版本。
// ⚠️ 存整份搜索命中 JSON，别拆字段；文件坏了当空处理，不抛异常。

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

    /// <summary>收藏 / 取消收藏。</summary>
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

    // 落盘

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
