# -*- coding: utf-8 -*-
r"""把打好的 zip **解压到一个干净目录**，然后照用户的做法跑一次 —— 验"解压就能用"。

这一步是整条链路上最接近用户的一环，验的是：
  1. 解压出来的**目录结构**对不对（顶层**恰好一个** `Mosslight.exe`，没有 Assets\）
  2. 解压出来的 exe **真的能起来**（单文件自解压 + 原生库，语言数据在程序集里）
  3. `--screenshot` 那张图不是白板
  4. 语言数据真的读到了（`--lang-check`：来源必须是内嵌，条数要够）
  5. `--page <清单>` 每一页都还能截出来（清单过期在这里会当场露馅）
  6. 解压出来的 exe 没有被 Windows 标记/被路径里的空格搞挂

⚠️ 语言文件改成内嵌资源之后，这里**不再**去看 `Assets\lang\*.json` ——
   那个目录本来就不该存在。改成跑 `--lang-check` 看每种语言的来源和条数。

⚠️ 又改（两个包）：release.yml 现在出**两个** zip（框架依赖的小包 + 自带运行时的大包）。
   两个包**都要跑这一段**，而且判据完全一样 —— 尤其那句"顶层恰好 1 项"，
   小包也是 PublishSingleFile，**没有"小包可以散装"这种例外**。
   所以 `--zip` 现在可以给**多次**：

       python tools\check_unzip_run.py --zip <小包.zip> --zip <大包.zip> --work D:\...\unzip

   给一个 zip 时行为跟以前完全一样（解压到 `--work` 指定的目录）；
   给多个时各自解压到 `--work\<小包|大包>`，互不覆盖 ——
   **不能共用同一个目录**：共用的话第二个包跑之前得先清空，
   而"清空"一旦没做干净，验的就不是"干净目录"了。

⚠️ 小包（框架依赖）要求运行这份 exe 的机器上**有 .NET 10 桌面运行时**。
   开发机 / GitHub runner 上都有（装了 SDK 就带），所以这里跑得起来；
   但这一步**证明不了**"没装运行时的机器上会弹什么"—— 那件事只能拿一台
   干净的 Windows 试，脚本里没法验。

⚠️⚠️ **这一步会写 `%APPDATA%\\Mosslight\\`，所以脚本自己备份 + 还原。**
   以前这里的注释写的是"截图只读不写（Save 只在改设置时发生）"——**那是错的**，
   实测（2026-10）：跑一次 `--page home --screenshot` 之后，
     · `java_cache.json` 被刷新（启动时会扫一遍本机 Java 并把结果写进缓存）；
     · `config.json` 被重新序列化（内容一样，但排版变了，字节不同）。
   两个都无害（都是缓存/重排版，不是设置被改），但"验完发现用户配置变了"
   本身就是不该发生的事。所以现在：
     · 跑之前把整个 `%APPDATA%\\Mosslight` 快照到临时目录；
     · 全部跑完之后（**包括中途抛异常**，走 finally）原样还原；
     · 还原后再逐文件比一次 sha256，不一致就报出来。
   不想让它动你的配置就**别加 `--no-restore-appdata`**（默认就是还原）。
   顺带：这些窗口真的会去读 `%APPDATA%` 里那份**用户真实配置** ——
   也就是说截图长什么样跟你本机的设置有关（语言、主题、游戏目录）。
   要固定的"默认形态"得靠 workflow 里那一步，它会放一个 `portable.txt`
   （配置落在临时目录里，从零开始）。
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

# 页面清单从 workflow 里读，保证"验的就是 CI 会跑的那张表"
REPO = Path(__file__).resolve().parent.parent


def appdata_dir() -> Path:
    """用户在用的那份配置目录。没有就返回一个不存在的路径（调用方自己判）。"""
    roaming = os.environ.get("APPDATA")
    if not roaming:
        return Path("__no_appdata__")
    return Path(roaming) / "Mosslight"


def tree_hashes(root: Path) -> dict:
    """整棵树逐文件 sha256。目录不存在返回空 dict。"""
    out = {}
    if not root.exists():
        return out
    for p in sorted(root.rglob("*")):
        if p.is_file():
            h = hashlib.sha256()
            with p.open("rb") as f:
                for chunk in iter(lambda: f.read(1 << 20), b""):
                    h.update(chunk)
            out[str(p.relative_to(root))] = h.hexdigest()
    return out


def snapshot_appdata() -> tuple[Path | None, dict]:
    """把 %APPDATA%\\Mosslight 拷到临时目录，返回 (快照路径, 快照哈希)。

    目录不存在就返回 (None, {}) —— 那就不用还原（跑完还它一个"不存在"）。
    """
    src = appdata_dir()
    if not src.exists():
        return None, {}
    tmp = Path(tempfile.mkdtemp(prefix="dsh-appdata-")) / "Mosslight"
    shutil.copytree(src, tmp, dirs_exist_ok=True, ignore_dangling_symlinks=True)
    return tmp, tree_hashes(tmp)


def restore_appdata(snap: Path | None, before: dict) -> bool:
    """把配置目录还原成快照的样子，再比一次哈希。返回是否逐字节一致。

    ⚠️ 还原策略是"**先删干净再拷回去**"，不是"覆盖"：
       覆盖的话，这一轮里**新生成**的文件（比如缓存目录下多出来的
       `xx.bin`）会留在原地 —— 那正是"验完发现用户配置变了"的来源。
    """
    src = appdata_dir()
    if snap is None:
        # 跑之前就没有这个目录 —— 那就不该留下它
        if src.exists():
            shutil.rmtree(src, ignore_errors=True)
            print(f"（跑之前没有 {src}，已删掉这一轮新建的）")
        return not src.exists()

    if src.exists():
        shutil.rmtree(src)
    shutil.copytree(snap, src, dirs_exist_ok=True)
    after = tree_hashes(src)
    if after == before:
        return True
    only_before = sorted(set(before) - set(after))
    only_after = sorted(set(after) - set(before))
    changed = sorted(k for k in set(before) & set(after) if before[k] != after[k])
    print(f"[X] 还原之后跟跑之前对不上："
          f"少了 {only_before[:5]}、多了 {only_after[:5]}、内容变了 {changed[:5]}")
    return False


def smoke_pages() -> list[str]:
    import yaml
    doc = yaml.safe_load((REPO / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8"))
    return (doc.get("env") or {})["SMOKE_PAGES"].split()


def kind_of(zip_path: Path) -> str:
    """从 zip 名判是哪个包。名字是 release.yml 里定的，这里只跟着认。

    ⚠️ 判据是**有没有 selfcontained 这个词**，不是"名字更长"之类：
        `Mosslight-2.0-v2.0.0-win64.zip` 是 `...-win64-selfcontained.zip` 的前缀，
        用 startswith 之类的写法会把大包误判成小包。
    """
    return "大包" if "selfcontained" in zip_path.name.lower() else "小包"


def run_one(z: Path, work: Path, settle: int, pages: list[str]) -> int:
    """验一个 zip。返回 0 表示这个包过了。"""
    kind = kind_of(z)
    print("\n" + "#" * 78)
    print(f"#  【{kind}】{z.name}（{z.stat().st_size / 1024 / 1024:.2f} MB）")
    print("#" * 78)

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
        # ⚠️ 语言数据改成内嵌资源之后，zip 里**应该只有一个 Mosslight.exe**。
        #    这条以前是"最多允许一层子目录（Assets/lang/*.json 就是一层）"，
        #    现在反过来：多出任何一项都是回归。判据用"恰好 1 项"，
        #    不写黑名单（黑名单挡不住"多出来一个别的文件"）。
        #    **两个包都是这条判据** —— 小包也是 PublishSingleFile。
        if names != ["Mosslight.exe"]:
            extra = [n for n in names if n != "Mosslight.exe"]
            print(f"[X] [{kind}] zip 顶层应该恰好 1 项 Mosslight.exe，实际 {len(names)} 项"
                  + (f"，多出来：{extra[:5]}" if extra else ""))
            return 1
        if not any(n == "Mosslight.exe" for n in names):
            print(f"[X] [{kind}] 根目录没有 Mosslight.exe")
            return 1
        zf.extractall(work)

    print("\n解压后的目录树（前两层）：")
    for p in sorted(work.rglob("*")):
        rel = p.relative_to(work)
        if len(rel.parts) <= 2:
            print("   " + str(rel) + ("/" if p.is_dir() else f"   {p.stat().st_size}"))

    exe = work / "Mosslight.exe"
    exe_mb = exe.stat().st_size / 1024 / 1024
    print(f"\nexe: {exe}  {exe_mb:.2f} MB")

    # 顺手报一下"这个包是不是真的自带运行时" —— 只看大小就够判断了：
    # 自带运行时的那个 60MB 上下，框架依赖的那个 2MB 上下，差 30 倍。
    # ⚠️ **不拿这一条当门禁**（那种判据在 workflow 里已经有了，而且是按
    #    两个包各自的门槛卡的）；这里只是让日志里能一眼看出"这个包是哪种形态"，
    #    免得"小包其实是大包"这种事在看输出时才被发现。
    print(f"     （{kind}形态参照：框架依赖约 2MB、自带运行时约 60MB）")

    # 注意：**不**放 portable.txt —— 要验的是"用户解压出来直接双击"的样子。
    # 代价是它会去读 %APPDATA%\Mosslight\config.json；截图只读不写（Save 只在改设置时发生）。
    shots = work / "_shots"
    shots.mkdir(exist_ok=True)

    # 先验语言数据：**没有 Assets\lang 这个目录**，翻译只能来自程序集。
    # `--lang-check` 会逐个语言把来源和条数打出来，不建窗口、几秒就退。
    lang = subprocess.run([str(exe), "--lang-check"], capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=180)
    print("\n---- --lang-check ----")
    for line in (lang.stdout or "").splitlines():
        print("   " + line)
    if lang.returncode != 0:
        print(f"[X] [{kind}] --lang-check 退出码 {lang.returncode} —— 内嵌语言数据有问题")
        for line in (lang.stderr or "").splitlines():
            print("   ! " + line)
        return 1
    if "结论：全部通过" not in (lang.stdout or ""):
        print(f"[X] [{kind}] --lang-check 没报「全部通过」")
        return 1

    failed = []
    for page in pages:
        png = shots / (page.replace(":", "-") + ".png")
        if png.exists():
            png.unlink()
        p = subprocess.run([str(exe), "--page", page, "--screenshot", str(png),
                            "--settle", str(settle)],
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
        print(f"[X] [{kind}] {len(failed)} 页没通过：{failed}")
        return 1
    print(f"[OK] [{kind}] 解压出来的 exe 能跑，{len(pages)} 页全部截出图（>=5000 字节）")
    return 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    ap = argparse.ArgumentParser()
    # ⚠️ action='append'：两个包都要验，所以这一个参数要给两次。
    #    给一次时行为跟以前一模一样（解压到 --work 那个目录）。
    ap.add_argument("--zip", required=True, action="append",
                    help="要验的 zip。两个包都要验时给两次")
    ap.add_argument("--work", required=True, help="解压到哪儿（会先清空）")
    ap.add_argument("--settle", type=int, default=3000)
    ap.add_argument("--all-pages", action="store_true", help="把 SMOKE_PAGES 全部跑一遍")
    ap.add_argument("--no-restore-appdata", action="store_true",
                    help="跑完**不**还原 %%APPDATA%%\\Mosslight（默认会还原，见文件头注释）")
    args = ap.parse_args()

    zips = [Path(z).resolve() for z in args.zip]
    for z in zips:
        if not z.exists():
            raise SystemExit(f"找不到 zip：{z}")
    work = Path(args.work).resolve()

    pages = smoke_pages() if args.all_pages else ["home"]

    # ⚠️ 快照必须在**任何一个 exe 起来之前**做。跑起来之后配置就已经被动过了。
    snap, before = (None, {})
    if not args.no_restore_appdata:
        snap, before = snapshot_appdata()
        print(f"%APPDATA%\\Mosslight：跑之前 {len(before)} 个文件"
              + (f"，已快照到 {snap}" if snap else "（目录不存在）"))
    else:
        print("[!] --no-restore-appdata：这一轮对 %APPDATA%\\Mosslight 的改动会留下")

    results = []
    multi = len(zips) > 1
    try:
        for z in zips:
            # 一个 zip 时用 --work 原样（跟以前一致）；多个时各自一个子目录，
            # 子目录名用 kind（小包 / 大包），一眼能对上。
            sub = (work / kind_of(z)) if multi else work
            results.append((kind_of(z), z, sub, run_one(z, sub, args.settle, pages)))
    finally:
        # ⚠️ 用 finally：中途抛异常（比如 exe 崩了）也得把配置还回去，
        #    不然"验失败"和"用户配置被改"会同时发生，后者更麻烦。
        if not args.no_restore_appdata:
            print()
            if restore_appdata(snap, before):
                print(f"[OK] %APPDATA%\\Mosslight 已逐字节还原（{len(before)} 个文件）")
            else:
                print("[X] %APPDATA%\\Mosslight 还原后逐字节校验不通过 —— 请看上面的差异")
                results.append(("（配置还原）", Path(""), Path(""), 1))

    print("\n" + "=" * 78)
    for kind, z, sub, code in results:
        print(f"  [{'OK' if code == 0 else 'X '}] {kind} {z.name}  → 截图在 {sub / '_shots'}")
    bad = [k for k, _, _, c in results if c != 0]
    print("=" * 78)
    if bad:
        print(f"[X] {len(bad)} 个包没通过：{bad}")
        return 1
    print(f"[OK] {len(results)} 个包都过了")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
