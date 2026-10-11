# -*- coding: utf-8 -*-
r"""把打好的 zip **解压到一个干净目录**，然后照用户的做法跑一次 —— 验"解压就能用"。

这一步是整条链路上最接近用户的一环，验的是：
  1. 解压出来的**目录结构**对不对（exe 和 Assets 直接在根，不多一层）
  2. 解压出来的 exe **真的能起来**（单文件自解压 + 原生库 + Assets\lang 都在）
  3. `--screenshot` 那张图不是白板
  4. `--page <清单>` 每一页都还能截出来（清单过期在这里会当场露馅）
  5. 解压出来的 exe 没有被 Windows 标记/被路径里的空格搞挂

用法：
    python tools\check_unzip_run.py --zip D:\...\Mosslight-2.0-v2.0.0-win64.zip --work D:\...\unzip
"""
import argparse
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

# 页面清单从 workflow 里读，保证"验的就是 CI 会跑的那张表"
REPO = Path(__file__).resolve().parent.parent


def smoke_pages() -> list[str]:
    import yaml
    doc = yaml.safe_load((REPO / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8"))
    return (doc.get("env") or {})["SMOKE_PAGES"].split()


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--zip", required=True)
    ap.add_argument("--work", required=True, help="解压到哪儿（会先清空）")
    ap.add_argument("--settle", type=int, default=3000)
    ap.add_argument("--all-pages", action="store_true", help="把 SMOKE_PAGES 全部跑一遍")
    args = ap.parse_args()

    z = Path(args.zip).resolve()
    work = Path(args.work).resolve()
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)

    with zipfile.ZipFile(z) as zf:
        names = zf.namelist()
        print(f"zip 里有 {len(names)} 项：")
        for n in sorted(names):
            print("   " + n)
        # 顶层结构检查：所有条目都必须在根上
        top = {n.split("/")[0] for n in names}
        print(f"顶层条目：{sorted(top)}")
        # 顶层结构检查：最多允许一层子目录（`Assets/lang/*.json` 就是一层）。
        # ⚠️ 一开始这里写成 `n.count("/") > 1`（想表达"多于两层"），其实那是
        #    **两层** —— `Assets/lang/x.json` 正好有 2 个斜杠，全被误判成"嵌套太深"。
        #    按路径段数写才不容易错。
        bad = [n for n in names if len(n.split("/")) > 3]
        if bad:
            print("[X] 有嵌套超过两层的条目：", bad[:5])
            return 1
        if not any(n == "Mosslight.exe" for n in names):
            print("[X] 根目录没有 Mosslight.exe")
            return 1
        zf.extractall(work)

    print("\n解压后的目录树（前两层）：")
    for p in sorted(work.rglob("*")):
        rel = p.relative_to(work)
        if len(rel.parts) <= 2:
            print("   " + str(rel) + ("/" if p.is_dir() else f"   {p.stat().st_size}"))

    exe = work / "Mosslight.exe"
    print(f"\nexe: {exe}  {exe.stat().st_size/1024/1024:.1f} MB")

    # 注意：**不**放 portable.txt —— 要验的是"用户解压出来直接双击"的样子。
    # 代价是它会去读 %APPDATA%\Mosslight\config.json；截图只读不写（Save 只在改设置时发生）。
    shots = work / "_shots"
    shots.mkdir(exist_ok=True)

    pages = smoke_pages() if args.all_pages else ["home"]
    failed = []
    for page in pages:
        png = shots / (page.replace(":", "-") + ".png")
        if png.exists():
            png.unlink()
        p = subprocess.run([str(exe), "--page", page, "--screenshot", str(png),
                            "--settle", str(args.settle)],
                           capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=180)
        size = png.stat().st_size if png.exists() else 0
        ok = p.returncode == 0 and size >= 5000
        print(f"  {'OK ' if ok else 'X  '} {page:<32} exit={p.returncode} {size} bytes")
        if not ok:
            failed.append(page)
            crash = work / "Logs" / "crash.log"
            if crash.exists():
                print("     crash.log:", crash.read_text(encoding="utf-8", errors="replace")[-600:])

    print()
    if failed:
        print(f"[X] {len(failed)} 页没通过：{failed}")
        return 1
    print(f"[OK] 解压出来的 exe 能跑，{len(pages)} 页全部截出图（>=5000 字节）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
