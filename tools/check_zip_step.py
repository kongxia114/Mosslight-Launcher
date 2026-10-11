# -*- coding: utf-8 -*-
r"""把 release.yml 里 `打 zip` 那一步抽出来，在**实验目录**里真跑一遍（不往仓库里落产物）。

为什么要单独跑：那一步里有几件事只有真跑才看得见 ——
  · `Compress-Archive -Path 'publish\*'` 到底把目录结构打成什么样（有没有多一层 publish\）；
  · zip 里那几条硬性检查（Mosslight.exe / Assets/lang/zh_CN.json / 没有 pdb）过不过；
  · 单文件 exe 有 60MB 上下，这一步到底要多久。

用法：
    python tools\check_zip_step.py --publish D:\deepseek\实验\relcheck\publish --work D:\deepseek\实验\relcheck\zipwork
"""
import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
WF = REPO / ".github" / "workflows" / "release.yml"


def extract_step(name: str) -> str:
    doc = yaml.safe_load(WF.read_text(encoding="utf-8"))
    for job in doc["jobs"].values():
        for s in job["steps"]:
            if s.get("name") == name:
                return s["run"]
    raise SystemExit(f"release.yml 里找不到 `{name}` 这一步")


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", required=True, help="已经 publish 好的目录")
    ap.add_argument("--work", required=True, help="在哪儿打 zip（实验目录）")
    args = ap.parse_args()

    publish = Path(args.publish).resolve()
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    if not (publish / "Mosslight.exe").exists():
        raise SystemExit(f"{publish} 里没有 Mosslight.exe")

    # 把 publish 拷进工作目录，让这一步的 'publish\*' 相对路径成立
    import shutil
    dst = work / "publish"
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(publish, dst)

    script = extract_step("打 zip").replace("${{ steps.ver.outputs.tag }}", "v2.0.0")
    ps1 = work / "step_zip.ps1"
    ps1.write_text(script, encoding="utf-8-sig")

    env = dict(os.environ)
    env["GITHUB_ENV"] = str(work / "env.txt")
    (work / "env.txt").write_text("", encoding="utf-8")

    import time
    t0 = time.time()
    proc = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-File", str(ps1)],
                          cwd=work, env=env, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    dt = time.time() - t0

    print(f"退出码 = {proc.returncode}   用时 {dt:.1f}s")
    for line in (proc.stdout or "").splitlines():
        print("  · " + line)
    for line in (proc.stderr or "").splitlines():
        print("  ! " + line)

    zips = list(work.glob("Mosslight-*-win64.zip"))
    if not zips:
        print("[X] 没有产出 zip")
        return 1
    z = zips[0]
    print(f"\nzip: {z}  {z.stat().st_size/1024/1024:.1f} MB")
    raw = (work / "env.txt").read_bytes()
    # PowerShell 5.1 的 `>>` 写 UTF-16LE、7 写 UTF-8 —— 两种都认
    txt = raw.decode("utf-16" if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig",
                     errors="replace").strip()
    print("GITHUB_ENV 里写的：", txt)
    return proc.returncode


if __name__ == "__main__":
    raise SystemExit(main())
