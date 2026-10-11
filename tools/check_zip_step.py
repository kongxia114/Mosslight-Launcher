# -*- coding: utf-8 -*-
r"""把 release.yml 里 `打 zip` 那一步抽出来，在**实验目录**里真跑一遍（不往仓库里落产物）。

为什么要单独跑：那一步里有几件事只有真跑才看得见 ——
  · `Compress-Archive -Path '<目录>\*'` 到底把目录结构打成什么样（有没有多一层目录）；
  · **两个包**各自那几条硬性检查过不过
    （**顶层恰好 1 项 Mosslight.exe / 没有 Assets\ / 没有散装 dll / 没有 pdb**）；
  · 两个包各自的**大小门槛**（小包下限 1MB + 上限 10MB、大包下限 10MB）对不对 ——
    这两条是这一轮新加的，只写不跑等于没写；
  · 单文件 exe 有 60MB 上下，大包那一次到底要多久。

⚠️ 2026-10 改过：语言文件改成内嵌资源之后，zip 里**只该有一个 exe**。
   "zip 根必须能找到 Assets/lang/zh_CN.json" 那条断言反过来了 ——
   现在 Assets\ 出现就是回归。

⚠️ 又改（两个包）：原来只打一个 zip，现在**两个**（框架依赖的小包 + 自带运行时的大包）。
   所以这里要**两个包都验一遍**，而不是"找到一个就算过" —— 只验其中一个的话，
   另一个包坏了这里照样绿。

用法：
    python tools\check_zip_step.py --publish D:\deepseek\实验\relcheck\pub-fx --work D:\deepseek\实验\relcheck\zipwork
    python tools\check_zip_step.py --publish-fx <小包publish> --publish-sc <大包publish> --work <打zip的工作目录>

    `--publish` 是**老写法**（单包时代留下的），等价于"把它当成小包，大包跳过"，
    留着是为了以前记在文档/脚本里的命令还能直接跑。
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
WF = REPO / ".github" / "workflows" / "release.yml"

# 两个包在 release.yml 里的输出目录名 —— 跟 `打 zip` 那一步的表**必须一致**。
# 这里只是本地跑测试时用来摆目录，改 workflow 里那两张表时记得一起看这里。
FX_DIR = "publish-fx"
SC_DIR = "publish-sc"


def extract_step(name: str) -> str:
    doc = yaml.safe_load(WF.read_text(encoding="utf-8"))
    for job in doc["jobs"].values():
        for s in job["steps"]:
            if s.get("name") == name:
                return s["run"]
    raise SystemExit(f"release.yml 里找不到 `{name}` 这一步")


def read_env_file(path: Path) -> dict:
    """读 GITHUB_ENV。PowerShell 5.1 的 `>>` 写 UTF-16LE、7 写 UTF-8 —— 两种都认"""
    if not path.exists():
        return {}
    raw = path.read_bytes()
    text = raw.decode("utf-16" if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig",
                      errors="replace")
    out = {}
    for line in text.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", help="（老写法）单个已 publish 好的目录，当成小包")
    ap.add_argument("--publish-fx", help=f"小包（框架依赖）的 publish 目录，默认摆成 {FX_DIR}")
    ap.add_argument("--publish-sc", help=f"大包（自带运行时）的 publish 目录，默认摆成 {SC_DIR}")
    ap.add_argument("--work", required=True, help="在哪儿打 zip（实验目录）")
    args = ap.parse_args()

    fx = args.publish_fx or args.publish
    sc = args.publish_sc
    if not fx:
        raise SystemExit("至少要给一个包：--publish-fx（或老写法的 --publish）")
    if args.publish and not args.publish_fx:
        # 老写法：只有一个小包，明说一句，免得看日志的人以为大包静默失败了
        print("[!] 用的是老写法 --publish：只验小包，大包这次**没验**")

    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)

    # 把 publish 目录拷进工作目录，让这一步里那些相对路径（publish-fx\* / publish-sc\*）成立
    layouts = []
    if fx:
        layouts.append((Path(fx).resolve(), FX_DIR, "小包"))
    if sc:
        layouts.append((Path(sc).resolve(), SC_DIR, "大包"))

    for src, name, kind in layouts:
        if not (src / "Mosslight.exe").exists():
            raise SystemExit(f"[{kind}] {src} 里没有 Mosslight.exe")
        dst = work / name
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(src, dst)
        print(f"[{kind}] {src} → {dst}")

    script = extract_step("打 zip").replace("${{ steps.ver.outputs.tag }}", "v2.0.0")
    ps1 = work / "step_zip.ps1"
    ps1.write_text(script, encoding="utf-8-sig")

    env = dict(os.environ)
    env["GITHUB_ENV"] = str(work / "env.txt")
    (work / "env.txt").write_text("", encoding="utf-8")

    t0 = time.time()
    proc = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-File", str(ps1)],
                          cwd=work, env=env, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    dt = time.time() - t0

    print(f"\n退出码 = {proc.returncode}   用时 {dt:.1f}s")
    for line in (proc.stdout or "").splitlines():
        print("  · " + line)
    for line in (proc.stderr or "").splitlines():
        print("  ! " + line)

    if proc.returncode != 0:
        return proc.returncode

    env_values = read_env_file(work / "env.txt")
    print("\nGITHUB_ENV 里写的：", env_values)

    # ⚠️ 两个 zip 都要在，而且是**按名字**找 —— 不能用 `Mosslight-*-win64.zip` 那种
    #    通配去"找到一个就算过"：小包那个名字是大包名字的前缀，
    #    通配要么漏掉大包，要么（写成 '*.zip'）把两个混成一个。
    #    名字从 GITHUB_ENV 的 ZIP / ZIP_SC 里读，跟 workflow 用同一份值。
    expected = []
    if fx:
        expected.append(("小包", env_values.get("ZIP", "Mosslight-2.0-v2.0.0-win64.zip")))
    if sc:
        expected.append(("大包",
                         env_values.get("ZIP_SC", "Mosslight-2.0-v2.0.0-win64-selfcontained.zip")))

    bad = 0
    sizes = {}
    for kind, name in expected:
        z = work / name
        if not z.exists():
            # 兜底：名字没从 env 里读到（旧版 workflow？），按后缀找一下，但要把话说清楚
            print(f"[X] [{kind}] 没有产出 {name}")
            bad += 1
            continue
        mb = z.stat().st_size / 1024 / 1024
        sizes[kind] = mb
        print(f"[OK] [{kind}] {z.name}  {mb:.2f} MB")
        # 顶层清单也打出来 —— "两个包都真的只有 1 个 exe"这件事的证据在这里
        import zipfile
        with zipfile.ZipFile(z) as zf:
            names = sorted(zf.namelist())
        print(f"      顶层清单（{len(names)} 项）：{names}")

    if len(sizes) == 2:
        # 两个包的体量必须差开 —— 这条是"两个包"这件事本身的验收标准
        if sizes["小包"] >= sizes["大包"]:
            print(f"[X] 小包（{sizes['小包']:.2f} MB）不小于大包（{sizes['大包']:.2f} MB）"
                  " —— 两个包的 --self-contained 多半写重了")
            bad += 1
        else:
            print(f"[OK] 小包比大包小 {sizes['大包'] / max(sizes['小包'], 0.001):.0f} 倍"
                  f"（{sizes['小包']:.2f} MB vs {sizes['大包']:.2f} MB）")

    if bad:
        print(f"\n[X] {bad} 处不通过")
        return 1
    print(f"\n[OK] 这一步过了，{len(expected)} 个 zip 都在")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
