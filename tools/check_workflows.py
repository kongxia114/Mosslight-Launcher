# -*- coding: utf-8 -*-
r"""workflow 静态检查（比 yamlcheck.py 更进一步）

1. YAML 能不能解析
2. uses: 的 action 名 / major 版本格式
3. **每个 `run:` 块里的 PowerShell 能不能解析** —— 用 PowerShell 自己的
   Parser::ParseFile 过一遍（不是"我觉得语法对"）
4. 两个 workflow 的 SMOKE_PAGES 是否一致（这次改动的核心约束之一）
5. 触发语义提示

用法：
    python tools\check_workflows.py
"""
import re
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
WF = REPO / ".github" / "workflows"
USES_RE = re.compile(r"^(?P<name>[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-/]+)?)@(?P<ver>.+)$")


def ps_parse_errors(script: str) -> str:
    """把脚本交给 PowerShell 解析，返回错误文本（空串 = 语法 OK）

    ⚠️ 必须用**真** PowerShell 解析，不能拿正则凑：这次改动里出现了
    `-in @(...)`、`$env:X -split`、函数定义这些东西，正则判断不了。
    """
    with tempfile.TemporaryDirectory() as td:
        ps1 = Path(td) / "snippet.ps1"
        # 带 BOM 写：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按本地 ANSI 解码，
        # 里面的中文会变乱码（那是"编码"问题，会伪装成语法错误）
        ps1.write_text(script, encoding="utf-8-sig")
        cmd = (
            "$e=$null;"
            "$t=[System.Management.Automation.Language.Parser]::ParseFile("
            f"'{ps1}', [ref]$null, [ref]$e);"
            "if($e){ $e | ForEach-Object { $_.ToString() } }"
        )
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        return (proc.stdout or "").strip()


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    fail = 0
    smoke = {}

    for f in ("ci.yml", "release.yml"):
        p = WF / f
        print("=" * 74)
        print(f)
        doc = yaml.safe_load(p.read_text(encoding="utf-8"))
        print("  [OK] YAML 解析通过")

        on = doc.get("on", doc.get(True))
        print(f"  on = {on}")

        env = doc.get("env") or {}
        if "SMOKE_PAGES" in env:
            smoke[f] = env["SMOKE_PAGES"].split()

        for jn, job in doc["jobs"].items():
            for i, s in enumerate(job["steps"], 1):
                label = s.get("name") or s.get("uses") or f"step {i}"
                if "uses" in s:
                    m = USES_RE.match(s["uses"])
                    if not m:
                        print(f"  [X] [{i}] uses 格式可疑: {s['uses']}")
                        fail += 1
                        continue
                    ver = m.group("ver")
                    ok = bool(re.fullmatch(r"v\d+(\.\d+)*", ver)) or bool(re.fullmatch(r"[0-9a-f]{40}", ver))
                    print(f"  [{'OK' if ok else 'X '}] [{i}] uses {s['uses']}")
                    if not ok:
                        fail += 1
                    continue

                script = s.get("run", "")
                if not script.strip():
                    continue
                # `${{ }}` 换成占位值 —— 不换的话拼进 PowerShell 里的是 `${{ ... }}`，
                # 那是解析错误，会把"工作流占位符"误报成"脚本语法错"。
                probe = re.sub(r"\$\{\{\s*(.+?)\s*\}\}", "PLACEHOLDER", script)
                errs = ps_parse_errors(probe)
                if errs:
                    print(f"  [X] [{i}] {label} —— PowerShell 解析失败：")
                    for line in errs.splitlines()[:6]:
                        print("         " + line)
                    fail += 1
                else:
                    print(f"  [OK] [{i}] {label} —— PowerShell 语法通过")

    print("=" * 74)
    a, b = smoke.get("ci.yml"), smoke.get("release.yml")
    if a is None or b is None:
        print("[X] 有 workflow 没定义 SMOKE_PAGES")
        fail += 1
    elif a == b:
        print(f"[OK] 两份 SMOKE_PAGES 一致（{len(a)} 项）")
        for x in a:
            print("       " + x)
    else:
        print("[X] 两份 SMOKE_PAGES 不一致！")
        print("   只在 ci.yml   :", sorted(set(a) - set(b)))
        print("   只在 release  :", sorted(set(b) - set(a)))
        fail += 1

    print("=" * 74)
    print("结果：", "全部通过" if fail == 0 else f"{fail} 处问题")
    return 1 if fail else 0


if __name__ == "__main__":
    raise SystemExit(main())
