# -*- coding: utf-8 -*-
"""把 release.yml 里 `组装发行说明` 那一步抽出来，在一个**带 tag 的临时仓库**里真跑一遍。

为什么要这么测：那段逻辑要吃三样只有真 git 仓库才有的东西 ——
  · `git describe --tags --abbrev=0 --match 'v*' "<tag>^"`（找上一个 tag 做 compare 链接）
  · `CHANGELOG.md` 的小节匹配
  · 空正文 / 有正文两条分支
而 GitHub 上的 `continue-on-error: true` 只能靠"真推一次"才知道有没有走错分支，
这里在本地的临时仓库里把三种情形都摆出来跑。

用法：
    python tools\\check_release_body.py
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
WF = REPO / ".github" / "workflows" / "release.yml"
GIT = shutil.which("git") or r"C:\ETS2LA\system\git\cmd\git.exe"
PS = "powershell"  # GitHub 上是 pwsh；本机只有 5.1，语法/逻辑两边一致


def extract_step() -> str:
    doc = yaml.safe_load(WF.read_text(encoding="utf-8"))
    for job in doc["jobs"].values():
        for s in job["steps"]:
            if s.get("name") == "组装发行说明":
                return s["run"]
    raise SystemExit("release.yml 里找不到 `组装发行说明` 这一步")


def run(cmd, cwd, env=None):
    return subprocess.run(cmd, cwd=cwd, env=env, capture_output=True,
                          text=True, encoding="utf-8", errors="replace")


def build_repo(root: Path, changelog: str | None):
    """造一个有两个提交 + v1.9.0 / v2.0.0 两个 tag 的临时仓库"""
    root.mkdir(parents=True, exist_ok=True)
    run([GIT, "init", "-q", "-b", "master"], root)
    run([GIT, "config", "user.email", "t@t"], root)
    run([GIT, "config", "user.name", "t"], root)
    (root / "a.txt").write_text("one\n", encoding="utf-8")
    run([GIT, "add", "-A"], root)
    run([GIT, "commit", "-q", "-m", "first"], root)
    run([GIT, "tag", "v1.9.0"], root)
    (root / "a.txt").write_text("two\n", encoding="utf-8")
    if changelog is not None:
        (root / "CHANGELOG.md").write_text(changelog, encoding="utf-8")
    run([GIT, "add", "-A"], root)
    run([GIT, "commit", "-q", "-m", "second"], root)
    run([GIT, "tag", "v2.0.0"], root)


CHANGELOG_WITH = """\
# 变更记录

## 未发布

<!-- 平时就往这里追加；发版时流程会优先找精确匹配的版本号小节 -->

## v2.0.0

- 新增日志窗口
- 修好了下载页的加载器列表

## v1.9.0

- 老版本
"""

CHANGELOG_EMPTY = """\
# 变更记录

## 未发布

<!-- 这一版还没写说明 -->
"""


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    script = extract_step()
    # `${{ ... }}` 换成真值（本来该由 GitHub 替换）
    script = script.replace("${{ steps.ver.outputs.tag }}", "v2.0.0")
    if "${{" in script:
        print("[!] 还有没替换掉的 ${{ }}：", re.findall(r"\$\{\{.*?\}\}", script))

    cases = [
        ("有 CHANGELOG 且精确匹配版本号", CHANGELOG_WITH, "v2.0.0"),
        ("CHANGELOG 里只有空的「未发布」", CHANGELOG_EMPTY, "v2.0.0"),
        ("仓库里根本没有 CHANGELOG", None, "v2.0.0"),
    ]

    fail = 0
    for label, changelog, tag in cases:
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "repo"
            build_repo(root, changelog)
            tmp_body = Path(td) / "generated.md"
            # 仓库里那份"兜底正文"（提交进版本库的那一份）
            fallback = "（兜底正文：这份不该被覆盖，除非生成成功）\n"
            (root / "RELEASE_BODY.md").write_text(fallback, encoding="utf-8")

            step_script = script  # tag 已经替换过了（见上面）
            ps1 = Path(td) / "step.ps1"
            ps1.write_text(step_script, encoding="utf-8-sig")

            env = dict(os.environ)
            env.update({
                "ZIP": "Mosslight-2.0-v2.0.0-win64.zip",
                "GITHUB_REPOSITORY": "kongxia114/Mosslight-Launcher",
                "TMP_BODY": str(tmp_body),
                "GITHUB_OUTPUT": str(Path(td) / "out.txt"),
                "GITHUB_ENV": str(Path(td) / "env.txt"),
            })
            # ⚠️ 必须把 git 放进 PATH：这段脚本要 `git describe` 找上一个 tag 生成
            #    compare 链接。GitHub runner 上 git 本来就在 PATH 上；
            #    本机可能没有 —— 不放的话这条分支永远测不到（而它是**静默**的：
            #    RunWithLog 会把失败吞掉，正文只是少一行）。这个坑就是这样发现的。
            git_dir = str(Path(GIT).parent)
            env["PATH"] = git_dir + os.pathsep + env.get("PATH", "")
            ps1.write_text(step_script, encoding="utf-8-sig")
            proc = run([PS, "-NoProfile", "-NonInteractive", "-File", str(ps1)],
                       root, env)

            print("=" * 74)
            print(f"情形：{label}")
            print(f"  退出码 = {proc.returncode}")
            # 把步骤自己的输出打出来 —— RunWithLog 的 warning 也在这里面
            for line in (proc.stdout or "").strip().splitlines():
                if line.strip():
                    print("  · " + line)
            for line in (proc.stderr or "").strip().splitlines():
                if line.strip():
                    print("  ! " + line)
            if proc.returncode != 0:
                print("  [!] stderr:", (proc.stderr or "").strip()[:500])
                fail += 1
                continue

            body = (root / "RELEASE_BODY.md").read_text(encoding="utf-8")
            print("  ---- RELEASE_BODY.md ----")
            for line in body.splitlines():
                print("  | " + line)

            checks = [
                ("正文里有真实 zip 名", "Mosslight-2.0-v2.0.0-win64.zip" in body),
                ("正文里有下载小节", "## 下载" in body),
                ("正文里有 compare 链接", "compare/v1.9.0...v2.0.0" in body),
                ("「别只拷 exe」那句在", "Assets" in body),
                ("没写死任何一个版本号以外的旧版本", "v2.0.0-test" not in body),
            ]
            if "精确匹配" in label:
                checks.append(("用的是 CHANGELOG 的 v2.0.0 小节", "新增日志窗口" in body))
                checks.append(("空行还在（没塌成一行）", "\n\n" in body))
            if label.startswith("CHANGELOG 里只有空"):
                checks.append(("空的「未发布」没被当成正文", "这一版还没写说明" not in body))
            if label.startswith("仓库里根本没有"):
                checks.append(("说了去哪儿看改动", "提交记录" in body))

            for what, ok in checks:
                print(f"  [{'OK' if ok else 'X '}] {what}")
                if not ok:
                    fail += 1

    print("=" * 74)
    print("结果：", "全部通过" if fail == 0 else f"{fail} 处不通过")
    return 1 if fail else 0


if __name__ == "__main__":
    raise SystemExit(main())
