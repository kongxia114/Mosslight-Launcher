"""检查「源码里用到的文案键」是不是都在 5 个语言文件里。

用途：语言系统最怕的不是"翻得不好"，而是**键忘了加** —— 那样切到英文/日文/俄文时，
那几句会原样显示中文（用户看到的就是"语言系统坏了"）。2026-10 真发生过一次：
121 条 2.0 新加的提示文案（下载/启动/安装器那批）从没进过语言文件，
英文界面里混着中文。

用法（在仓库根目录）：

    python tools\\check_lang_keys.py

退出码 0 = 全部齐全；1 = 有缺失（会打印缺的键和用到它的位置）。
只做字面量提取（`Localizer.Tr("...")` 与 XAML 的 `{loc:Tr ...}`）；
用变量/拼接传进 Tr 的键抓不到 —— 那种要么是动态文案，要么已经在 Localizer 里处理。
"""

from __future__ import annotations

import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LANG_DIR = os.path.join(ROOT, "Mosslight.Core", "Localization", "lang")
SKIP_PARTS = ("\\obj", "\\bin", "\\.git")

# `Localizer.Tr(` 后面可以跟多个用 `+` 拼起来的字面量（跨行长文案）
TR_START = re.compile(r"Localizer\.Tr\(\s*")
LITERAL = re.compile(r'"((?:[^"\\]|\\.)*)"')
XAML_KEY = re.compile(r"\{loc:Tr\s+([^}]*)\}")


def decode(raw: str) -> str:
    """C# 字符串里的常见转义（别用 unicode_escape，它会把中文弄坏）。"""
    return (raw.replace('\\"', '"').replace("\\n", "\n")
               .replace("\\t", "\t").replace("\\r", "\r").replace("\\\\", "\\"))


def cs_keys(text: str) -> set[str]:
    keys: set[str] = set()
    for start in TR_START.finditer(text):
        i = start.end()
        parts: list[str] = []
        while True:
            while i < len(text) and text[i] in " \t\r\n":
                i += 1
            m = LITERAL.match(text, i)
            if not m:
                break
            parts.append(decode(m.group(1)))
            i = m.end()
            while i < len(text) and text[i] in " \t\r\n":
                i += 1
            if i < len(text) and text[i] == "+":
                i += 1
                continue
            break
        if parts:
            keys.add("".join(parts))
    return keys


def xaml_keys(text: str) -> set[str]:
    keys: set[str] = set()
    for m in XAML_KEY.finditer(text):
        key = m.group(1).strip()
        if len(key) >= 2 and key[0] == key[-1] and key[0] in "'\"":
            key = key[1:-1]
        keys.add(key)
    return keys


def collect() -> dict[str, list[str]]:
    """键 → 用到它的位置（只留前几处，够定位就行）。"""
    used: dict[str, list[str]] = {}
    for root, dirs, files in os.walk(ROOT):
        if any(part in root for part in SKIP_PARTS):
            continue
        for name in files:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, ROOT)
            if name.endswith(".cs"):
                text = open(path, encoding="utf-8", errors="replace").read()
                for key in cs_keys(text):
                    used.setdefault(key, []).append(rel)
            elif name.endswith(".xaml"):
                text = open(path, encoding="utf-8", errors="replace").read()
                for key in xaml_keys(text):
                    used.setdefault(key, []).append(rel)
    return used


def main() -> int:
    languages: dict[str, set[str]] = {}
    for name in sorted(os.listdir(LANG_DIR)):
        if name.endswith(".json"):
            with open(os.path.join(LANG_DIR, name), encoding="utf-8") as f:
                languages[name[:-5]] = set(json.load(f))
    if not languages:
        print(f"没有找到语言文件：{LANG_DIR}")
        return 1

    used = collect()
    bad = 0
    for code, keys in languages.items():
        missing = sorted(k for k in used if k not in keys)
        if not missing:
            print(f"[ok] {code}: 源码用到的 {len(used)} 个键都在")
            continue
        bad += len(missing)
        print(f"[!] {code}: 缺 {len(missing)} 条")
        for k in missing[:40]:
            where = ", ".join(used[k][:2])
            print(f"    {k.replace(chr(10), '\\n')}    ← {where}")
        if len(missing) > 40:
            print(f"    …还有 {len(missing) - 40} 条")

    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
