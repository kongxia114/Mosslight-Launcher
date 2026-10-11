# -*- coding: utf-8 -*-
r"""发版流程的本地验证总入口：把能离线验的东西一次性跑完。

为什么要有这个：这次改动散在 4 个文件里（两个 workflow + .gitignore + RELEASE_BODY.md），
"改完随手验一下"很容易漏掉其中一项。这个脚本按顺序把每一层都过一遍：

  1. 两个 workflow 的 YAML + 每个 run: 块的 PowerShell 语法 + 两份冒烟清单是否一致
  2. 版本号推导（哪些 tag 会踩 CS7035）
  3. 发行说明的三个分支（在带 tag 的临时仓库里真跑）
  4. 打 zip 那一步（在实验目录里真打一个）
  5. 解压到干净目录、从那儿真跑起来（可选，--run-unzip）

⚠️ 全程**不往仓库里落产物**、**不碰 git**、**不碰 %APPDATA%\\Mosslight**。
   最重的那一层（dotnet publish + 全套截图）不在这里，用：
       python tools\local_run_workflow.py .github\workflows\release.yml --tag v2.0.0

用法：
    python tools\verify_release.py                      # 1~3
    python tools\verify_release.py --publish <dir> --work <dir> --zip <zip>   # 连 4~5 一起
"""
import argparse
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PY = sys.executable


def run(title: str, script: str, extra: list[str] | None = None) -> bool:
    print("\n" + "#" * 78)
    print(f"#  {title}")
    print("#" * 78)
    cmd = [PY, str(REPO / "tools" / script)] + (extra or [])
    proc = subprocess.run(cmd, cwd=REPO)
    ok = proc.returncode == 0
    print(f"--> {title}：{'通过' if ok else '不通过'}")
    return ok


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", help="已 publish 好的目录（给了就顺带验打 zip）")
    ap.add_argument("--work", help="打 zip 的工作目录（实验目录）")
    ap.add_argument("--zip", help="已打好的 zip（给了就顺带验解压运行）")
    ap.add_argument("--unzip", help="解压到哪儿")
    args = ap.parse_args()

    results = []
    results.append(("workflow 静态检查", run("1. workflow 静态检查", "check_workflows.py")))
    results.append(("版本号推导", run("2. 版本号推导（CS7035）", "check_version_norm.py")))
    results.append(("发行说明三分支", run("3. 发行说明三分支", "check_release_body.py")))

    if args.publish and args.work:
        results.append(("打 zip", run("4. 打 zip（真打）", "check_zip_step.py",
                                      ["--publish", args.publish, "--work", args.work])))
    if args.zip and args.unzip:
        results.append(("解压运行", run("5. 解压到干净目录真跑", "check_unzip_run.py",
                                        ["--zip", args.zip, "--work", args.unzip])))

    print("\n" + "=" * 78)
    for name, ok in results:
        print(f"  [{'OK' if ok else 'X '}] {name}")
    bad = [n for n, ok in results if not ok]
    print("=" * 78)
    print("结果：", "全部通过" if not bad else f"不通过：{bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
