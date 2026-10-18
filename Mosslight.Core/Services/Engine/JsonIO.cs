using System.Text;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

//  ⚠️ 为什么值得单独一个文件：`JsonNode.Parse(byte[])` **不认识 BOM**。
//  ⚠️ 顺带说一下**不**处理的情况：UTF-16 / UTF-32。那些文件在真实世界里
// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-jsonio-cs

/// <summary>读 JSON 的统一入口（会跳过 UTF-8 BOM）。</summary>
public static class JsonIO
{
    /// <summary>UTF-8 BOM 的三个字节。</summary>
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// 把字节按 UTF-8 解析成 JSON，**自动跳过开头的 BOM**。
    ///
    /// 解析不出来会抛 <see cref="System.Text.Json.JsonException"/> ——
    /// 调用方自己决定是记进告警还是当空数据。
    /// </summary>
    public static JsonNode? ParseBytes(byte[] bytes)
        => ParseBytes(bytes, default);

    /// <summary>
    /// 同上，但可以带解析选项（版本 JSON 那边允许注释和尾逗号 ——
    /// 有些版本 JSON 是手改过的，容忍一点比直接报错强）。
    /// </summary>
    public static JsonNode? ParseBytes(byte[] bytes, System.Text.Json.JsonDocumentOptions options)
    {
        var span = bytes.AsSpan();

        // 注意要比长度：一个只有 BOM 的文件（3 字节）不该被切成空再报"空输入"
        if (span.Length >= 3 && span[0] == Bom[0] && span[1] == Bom[1] && span[2] == Bom[2])
        {
            span = span[3..];
        }

        return span.Length == 0 ? null : JsonNode.Parse(span, nodeOptions: null, options);
    }

    /// <summary>读文件并解析（会跳过 BOM）。</summary>
    public static JsonNode? ParseFile(string path) => ParseBytes(File.ReadAllBytes(path));

    /// <summary>读文件并解析，带解析选项。</summary>
    public static JsonNode? ParseFile(string path, System.Text.Json.JsonDocumentOptions options)
        => ParseBytes(File.ReadAllBytes(path), options);

    /// <summary>
    /// 把 JSON 写回文件：**不带 BOM** 的 UTF-8。
    ///
    /// 我们自己写出去的那些（落盘的版本 JSON、缓存、配置）一律不带 ——
    /// 不带 BOM 的文件是所有工具都认的；带 BOM 的只有一部分认。
    /// </summary>
    public static void WriteFile(string path, JsonNode node, bool indented = true)
    {
        var text = node.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text + "\n", new UTF8Encoding(false));
    }
}
