using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// 把 inheritsFrom 链压成一份自给自足的实例版本 JSON（主类 / 库 / 参数 / assetIndex / logging）。
// ⚠️ 合并规则必须跟 VersionJsonLoader.Merge 完全一致：标量子覆盖父、库父在前且子覆盖同名、参数父在前子追加。

/// <summary>把版本继承链压平成一份自包含的版本 JSON。</summary>
public static class VersionJsonFlattener
{
    /// <summary>
    /// 把 versionId（会顺着 `inheritsFrom` 往上）压成一份 JSON，`id` 换成 newId、去掉
    /// `inheritsFrom` 和 `jar`。
    /// </summary>
    public static JsonObject? Flatten(string mcDir, string versionId, string newId)
    {
        var chain = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var id = versionId;

        while (!string.IsNullOrEmpty(id))
        {
            // 循环继承 / 链太深：当成坏数据，别死循环
            if (!seen.Add(id) || chain.Count >= LaunchRules.MaxInheritDepth)
            {
                return null;
            }

            var raw = VersionJsonLoader.LoadRaw(mcDir, id);
            if (raw is null)
            {
                return null;
            }
            chain.Add(raw);
            id = Str(raw, "inheritsFrom") ?? string.Empty;
        }

        // 上面是从子往父走的，压平要按"父 → 子"的顺序叠
        chain.Reverse();

        var merged = new JsonObject();
        foreach (var raw in chain)
        {
            MergeInto(merged, raw);
        }

        merged["id"] = newId;
        merged.Remove("inheritsFrom");

        // ⚠️ `jar` 也要去掉：自包含实例的客户端 jar 就在自己目录里，
        // 留着父版本的 `jar` 会把启动器指回原来那个版本（拷出来的 jar 白拷）。
        merged.Remove("jar");
        return merged;
    }

    /// <summary>把 child 叠到 target 上。</summary>
    private static void MergeInto(JsonObject target, JsonObject child)
    {
        foreach (var (key, value) in child)
        {
            switch (key)
            {
                case "libraries":
                    target["libraries"] = MergeLibraries(
                        target["libraries"] as JsonArray, value as JsonArray);
                    break;

                case "arguments":
                    target["arguments"] = MergeArguments(
                        target["arguments"] as JsonObject, value as JsonObject);
                    break;

                // 这几个由调用方自己接管，别从父版本抄过来
                case "id":
                case "inheritsFrom":
                case "jar":
                    break;

                default:
                    target[key] = value?.DeepClone();
                    break;
            }
        }
    }

    /// <summary>
    /// 库列表：父在前、子覆盖同名（**留在原位置**）；⚠️ 顺序有意义：Forge 的 `-DignoreList` 和
    /// BootstrapLauncher 都依赖 classpath 里的先后顺序，跟官方启动器一致才不会莫名其妙起不来。
    /// </summary>
    private static JsonArray MergeLibraries(JsonArray? parent, JsonArray? child)
    {
        var merged = new JsonArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var source in new[] { parent, child })
        {
            if (source is null)
            {
                continue;
            }
            foreach (var node in source)
            {
                if (node is not JsonObject lib)
                {
                    continue;
                }
                var name = Str(lib, "name") ?? string.Empty;
                if (name.Length > 0 && index.TryGetValue(name, out var at))
                {
                    merged[at] = lib.DeepClone();
                }
                else
                {
                    index[name] = merged.Count;
                    merged.Add(lib.DeepClone());
                }
            }
        }
        return merged;
    }

    /// <summary>参数：父在前、子追加（`game` / `jvm` 各管各的）。</summary>
    private static JsonObject MergeArguments(JsonObject? parent, JsonObject? child)
    {
        var merged = new JsonObject();
        foreach (var group in new[] { "game", "jvm" })
        {
            var list = new JsonArray();
            foreach (var source in new[] { parent, child })
            {
                if (source?[group] is not JsonArray items)
                {
                    continue;
                }
                foreach (var node in items)
                {
                    list.Add(node?.DeepClone());
                }
            }
            if (list.Count > 0)
            {
                merged[group] = list;
            }
        }
        return merged;
    }

    private static string? Str(JsonObject? obj, string key)
        => obj?[key] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
