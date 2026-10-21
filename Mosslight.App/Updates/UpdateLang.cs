using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Localization;

namespace Mosslight.App.Updates;

/// <summary>
/// 更新弹窗上那几句固定文案（标题 / 按钮 / 提示语）。
///
/// 为什么不走 Core 的 <see cref="Localizer"/>：那个是**整个界面**的词典，
/// 而这里只有五句、改的人也可能只是翻译更新窗 —— 单独一份 `Assets\update\lang.json`
/// 改起来更省事。更新日志正文（Release body）不在这里：那是中文原文，直接展示。
///
/// ⚠️ 资源读不到 / 语言没覆盖 → 一律退回中文，绝不因为少一句文案让更新窗打不开。
/// </summary>
public static class UpdateLang
{
    private const string Fallback = "zh_CN";
    private static readonly object Gate = new();
    private static JsonObject? _all;

    public static string Get(string key, params (string Name, string Value)[] args)
    {
        var text = Lookup(Localizer.CurrentLanguage, key) ?? Lookup(Fallback, key) ?? key;
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", value);
        }
        return text;
    }

    private static string? Lookup(string language, string key)
    {
        var all = Load();
        if (all is null)
        {
            return null;
        }
        // 先精确，再退到语言前缀（`zh_HK` → `zh_CN` 那类没有单独翻译的情况）
        if (all[language] is JsonObject exact && exact[key] is JsonValue v1)
        {
            return v1.GetValue<string>();
        }
        var prefix = language.Split('_')[0];
        foreach (var (name, node) in all)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && node is JsonObject obj && obj[key] is JsonValue v2)
            {
                return v2.GetValue<string>();
            }
        }
        return null;
    }

    private static JsonObject? Load()
    {
        lock (Gate)
        {
            if (_all is not null)
            {
                return _all;
            }
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var name = Array.Find(assembly.GetManifestResourceNames(),
                    n => n.EndsWith("update.lang.json", StringComparison.OrdinalIgnoreCase));
                if (name is null)
                {
                    return null;
                }
                using var stream = assembly.GetManifestResourceStream(name)!;
                return _all = JsonNode.Parse(stream) as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
