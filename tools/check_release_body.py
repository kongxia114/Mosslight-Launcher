# -*- coding: utf-8 -*-
"""把 release.yml 里 `组装发行说明` 那一步抽出来，在一个**带 tag 的临时仓库**里真跑一遍。

为什么要这么测：那段逻辑要吃三样只有真 git 仓库才有的东西 ——
  · `git describe --tags --abbrev=0 --match 'v*' "<tag>^"`（找上一个 tag 做 compare 链接）
  · `CHANGELOG.md` 的小节匹配
  · 空正文 / 有正文两条分支
而 GitHub 上的 `continue-on-error: true` 只能靠"真推一次"才知道有没有走错分支，
这里在本地的临时仓库里把三种情形都摆出来跑。

⚠️ 又改（两个包）：正文现在要同时写**两个包**的下载说明（小包 / 大包各多大、
   不确定下哪个、装运行时的官方链接），而且大小是**现场量 zip 文件**量出来的。
   所以这里额外做两件事（见下面）：
     · 真的摆出两个 zip 文件，大小特意取一个 <10MB、一个 >=10MB，
       把 Get-SizeText 的两个格式化分支都走到；
     · 断言正文里**两个 zip 名都在** —— 只写小包名是这里最容易犯、
       又最不容易发现的错（正文看起来完全正常，只是大包没被提到）。

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

# 失败清单：所有"该红"的判定都往这里记一条，最后统一再打一遍。
#
# 为什么要有它：这个脚本在 CI 里的输出有好几百行，红的那一条往往在**上面**；
# 而 GitHub 的日志被截到只剩最后一屏时，看到的就是「结果：1 处不通过」加一串 [OK]
# —— 2026-10-19 那次就是这样，谁也不知道到底是哪条判据红的。
# 有了这个清单，最后几行就把「哪个情形、哪一条判据」直接点名出来。
FAIL_LIST: list[str] = []


def fail_case(case: str, what: str, detail: str = "") -> None:
    """记一条失败，供脚本末尾汇总。不影响 fail 计数（那个还是各处在加）。"""
    FAIL_LIST.append(f"[{case}] {what}" + (f"（{detail}）" if detail else ""))


REPO = Path(__file__).resolve().parent.parent
WF = REPO / ".github" / "workflows" / "release.yml"
GIT = shutil.which("git") or r"C:\ETS2LA\system\git\cmd\git.exe"
PS = "powershell"  # GitHub 上是 pwsh；本机只有 5.1，语法/逻辑两边一致

# 两个包的文件名 —— 跟 release.yml 里 `打 zip` 那张表**必须一致**。
# 这两个名字同时是"这一步要吃的外部输入"（真实发版时来自 GITHUB_ENV 的
# ZIP / ZIP_SC），所以这里手写一份是刻意的：手写的那份要是跟 workflow 漂了，
# 下面的断言会当场红（而不是静默地什么都没验）。
ZIP_FX = "Mosslight-2.0-v2.0.0-win64.zip"
ZIP_SC = "Mosslight-2.0-v2.0.0-win64-selfcontained.zip"


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


def build_repo(root: Path, changelog: str | None,
               prev_tag: str = "v1.9.0", cur_tag: str = "v2.0.0",
               tag_current: bool = True):
    """造一个有两个提交 + 两个 tag 的临时仓库

    `tag_current=False` 时不打当前那个 tag —— 那就是**手动 dispatch** 的情形：
    tag 是发版流程后面才建的，组装正文时它还不存在。
    """
    root.mkdir(parents=True, exist_ok=True)
    run([GIT, "init", "-q", "-b", "master"], root)
    run([GIT, "config", "user.email", "t@t"], root)
    run([GIT, "config", "user.name", "t"], root)
    (root / "a.txt").write_text("one\n", encoding="utf-8")
    run([GIT, "add", "-A"], root)
    run([GIT, "commit", "-q", "-m", "first"], root)
    run([GIT, "tag", prev_tag], root)
    (root / "a.txt").write_text("two\n", encoding="utf-8")
    if changelog is not None:
        (root / "CHANGELOG.md").write_text(changelog, encoding="utf-8")
    run([GIT, "add", "-A"], root)
    run([GIT, "commit", "-q", "-m", "second"], root)
    if tag_current:
        run([GIT, "tag", cur_tag], root)


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


def section_text(text: str, name: str) -> str:
    """按 workflow 的规则抠出某一节的内容（`^##\\s+(.+)$` 分节），并剥掉 HTML 注释。

    规则必须跟 release.yml 里那段 PowerShell **一致** —— 这里宽松一点
    （比如也认 `###`）就会放过"标题层级写错"这种错，而那正是最该拦的。
    """
    lines, current = [], None
    for line in text.splitlines():
        found = re.match(r"^##\s+(.+)$", line)
        if found:
            current = found.group(1).strip()
            continue
        if current == name:
            lines.append(line)
    return re.sub(r"(?s)<!--.*?-->", "", "\n".join(lines)).strip()


def check_new_versioning(script_template: str) -> int:
    """新版本号规则（学 PCL2，**不带 v 前缀**）下，正文里的 compare 链接要对。

    ⚠️ 为什么单独一节：`git describe --match 'v*'` 只认带 `v` 的 tag。
    版本号改成 `2.0.0` 这种写法之后，它永远找不到"上一个版本"，
    只能退到很久以前那个 `v1.0.5` —— compare 链接指着一堆无关的旧提交，
    而且**不报错**（RunWithLog 会把失败吞掉，正文只是少一行 / 指错地方）。
    这段逻辑改过，但之前的断言只覆盖带 `v` 的 tag，等于没验。

    再来一遍**手动 dispatch** 那种：tag 还没建，`"$tag^"` 解析不了 ——
    以前那条路 compare 链接是**静默消失**的（v1.5.1 的正文就少那一行）。
    """
    tag = "2.0.0"
    prev = "1.9.0"
    changelog = "# 变更记录\n\n## 未发布\n\n- 新规则的第一次发版\n"
    zip_fx = f"Mosslight-2.0-{tag}-win64.zip"
    zip_sc = f"Mosslight-2.0-{tag}-win64-selfcontained.zip"

    fail = 0
    for tag_current, label in ((True, "推 tag 触发（tag 已存在）"),
                              (False, "手动 dispatch（tag 还没建）")):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "repo"
            build_repo(root, changelog, prev_tag=prev, cur_tag=tag, tag_current=tag_current)
            (root / zip_fx).write_bytes(b"x" * 1024)
            (root / zip_sc).write_bytes(b"x" * 1024)
            ps1 = Path(td) / "step.ps1"
            ps1.write_text(
                script_template.replace("${{ steps.ver.outputs.tag }}", tag),
                encoding="utf-8-sig")

            env = dict(os.environ)
            env.update({
                "ZIP": zip_fx,
                "ZIP_SC": zip_sc,
                "GITHUB_REPOSITORY": "kongxia114/Mosslight-Launcher-2",
                "TMP_BODY": str(Path(td) / "generated.md"),
                "GITHUB_OUTPUT": str(Path(td) / "out.txt"),
                "GITHUB_ENV": str(Path(td) / "env.txt"),
            })
            env["PATH"] = str(Path(GIT).parent) + os.pathsep + env.get("PATH", "")
            proc = run([PS, "-NoProfile", "-NonInteractive", "-File", str(ps1)], root, env)

            print("=" * 74)
            print(f"情形：新版本号规则（tag `{tag}`，上一个 `{prev}`，都不带 v）· {label}")
            print(f"  退出码 = {proc.returncode}")
            for line in (proc.stdout or "").strip().splitlines():
                if line.strip():
                    print("  · " + line)
            if proc.returncode != 0:
                print("  [!] stderr:", (proc.stderr or "").strip()[:500])
                fail += 1
                fail_case(label, f"这一步 PowerShell 自己退成了 {proc.returncode}",
                          "不是某条判据红的，多半是脚本抛异常；看上面的 stderr")
                continue

            body = (root / "RELEASE_BODY.md").read_text(encoding="utf-8")
            checks = [
                (f"compare 指向不带 v 的上一个 tag（{prev}...{tag}）",
                 f"compare/{prev}...{tag}" in body),
                ("正文里用上了「未发布」的内容", "新规则的第一次发版" in body),
                (f"zip 名跟着不带 v 的 tag（{zip_fx}）", zip_fx in body),
            ]
            for what, ok in checks:
                print(f"  [{'OK' if ok else 'X '}] {what}")
                if not ok:
                    fail += 1
                    fail_case(label, what)
                    print("  ---- RELEASE_BODY.md ----")
                    for line in body.splitlines():
                        print("  | " + line)
    return fail


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    # 本机能过、CI 不能过的差异，多半就藏在下面这三样里 —— 先打出来，
    # 真红了的时候不用再去猜"是不是 PowerShell 版本不一样"。
    def _first_line(cmd: list[str]) -> str:
        try:
            p = run(cmd, REPO)
            text = ((p.stdout or "") + (p.stderr or "")).strip()
            return text.splitlines()[0] if text else f"<退出码 {p.returncode}>"
        except Exception as exc:  # noqa: BLE001 - 只是想打一行信息，不该因此中断
            return f"<{type(exc).__name__}: {exc}>"

    print("=" * 74)
    print("环境：python " + sys.version.split()[0])
    print("环境：git " + _first_line([GIT, "--version"]))
    print("环境：powershell " + _first_line(
        [PS, "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]))
    print("环境：culture " + str(__import__("locale").getpreferredencoding(False)))
    print("环境：仓库 " + str(REPO))

    template = extract_step()
    if "${{" in template:
        unresolved = [
            e for e in re.findall(r"\$\{\{.*?\}\}", template)
            # 只允许这一个表达式：它就是"版本号"这个外部输入，下面会按情形替换
            if "steps.ver.outputs.tag" not in e
        ]
        if unresolved:
            print("[!] 还有没替换掉的 ${{ }}：", unresolved)
    # `${{ ... }}` 换成真值（本来该由 GitHub 替换）。
    # ⚠️ 必须从 `template` 派生：直接改 `script` 再传下去的话，
    #    后面"新版本号规则"那一节就拿不到干净的模板了（真踩过）。
    script = template.replace("${{ steps.ver.outputs.tag }}", "v2.0.0")

    # ⚠️ 仓库里那份**真的** CHANGELOG.md 也当一个情形验：
    #    前面三个情形用的都是手写小样本（验的是解析逻辑），
    #    但"仓库里那份文件本身写得对不对"从来没人验过 ——
    #    而它写歪了不会报任何错：Release 页面只会静静地显示一句
    #    "没有在 CHANGELOG.md 里找到对应小节的说明"（2026-10 之前六次发版全是这样，
    #    用户看到的就是那句）。
    real_changelog = None
    unreleased = ""
    real_changelog_path = REPO / "CHANGELOG.md"
    if real_changelog_path.exists():
        real_changelog = real_changelog_path.read_text(encoding="utf-8")
        unreleased = section_text(real_changelog, "未发布")
    else:
        print("[!] 仓库里没有 CHANGELOG.md —— Release 正文会退回「没找到说明」")

    cases = [
        ("有 CHANGELOG 且精确匹配版本号", CHANGELOG_WITH, "v2.0.0"),
        ("CHANGELOG 里只有空的「未发布」", CHANGELOG_EMPTY, "v2.0.0"),
        ("仓库里根本没有 CHANGELOG", None, "v2.0.0"),
    ]
    # 真的那份只有「未发布」**有内容**时才验"回退能用"：
    # 空着的话它本来就该退回"没找到说明"，验了也只能验这件事，没意义。
    if real_changelog is not None and unreleased:
        # tag 故意用 CHANGELOG 里**没有**专门小节的版本号：走"回退到未发布"那条路
        # （真实发版就是这个情形：平时只往「未发布」里写）
        # ⚠️ 这里原来写的是 `v2.0.0` —— 一旦仓库里真的有了 `## v2.0.0` 小节
        #    （发版时把「未发布」改名过来就是这个结果），正文会走"精确匹配"那条路，
        #    于是"真的用了未发布的内容"这条断言必然失败。换成一个不可能存在的版本号，
        #    这条检查才永远在测"回退"这件事本身（2026-10 发 2.0.0 时踩到）。
        cases.append(("仓库里那份真的 CHANGELOG", real_changelog, "v2.0.0"))
    elif real_changelog is not None:
        print("[!] 「未发布」现在是空的 —— 跳过「真的回退到它」那一情形"
              "（下一版把改动写进去之后会自动恢复）")

    fail = 0

    # ---------- 先验**仓库里那份文件本身** ----------
    # 这一步跟 workflow 无关，验的是"人写进去的那份 CHANGELOG 到底能不能用"：
    # 标题层级写错（`### 未发布`）、或者压根没有这一节，在 Release 页面上
    # 都只表现成"没找到说明" —— 没有人会去查，所以这里硬拦。
    #
    # ⚠️ 但「未发布」**是空的**不算错：刚发完版就是那个状态
    #    （上一批内容挪进 `## <版本号>`，`## 未发布` 留着等下一批）。
    #    所以"空"只警告、不红 —— 反过来，把它判红的话每次发完版 CI 都会
    #    红一次，久了就没人看这条门禁了。
    print("=" * 74)
    print("仓库里的 CHANGELOG.md")
    if real_changelog is not None:
        has_unreleased = bool(re.search(r"(?m)^##\s+未发布\s*$", real_changelog))
        print(f"  [{'OK' if has_unreleased else 'X '}] 有 `## 未发布` 这一节")
        if not has_unreleased:
            fail += 1
            fail_case("仓库里的 CHANGELOG.md", "有 `## 未发布` 这一节")
            # 这条判据最容易被截图截掉（它在输出最前面），所以把"该怎么办"
            # 直接写在这儿 —— 发版改名之后忘了留空壳，几乎是必然会发生的事。
            print("      ↑ 刚发完版？把 `## 未发布` 改名成版本号之后，"
                  "记得**再留一个空的 `## 未发布`** 等下一批（见 CHANGELOG.md 开头那段说明）")
        elif not unreleased:
            print("  [··] 「未发布」暂时是空的 —— 下一版发版前记得往里写（现在不算错）")
        else:
            print(f"  [OK] 「未发布」里有正文（{len(unreleased)} 字符，"
                  f"开头：{unreleased.splitlines()[0][:40]}…）")
    else:
        print("  [X ] 仓库里没有 CHANGELOG.md")
        fail += 1
        fail_case("仓库里的 CHANGELOG.md", "仓库里没有 CHANGELOG.md")

    for label, changelog, tag in cases:
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "repo"
            build_repo(root, changelog)
            tmp_body = Path(td) / "generated.md"
            # 仓库里那份"兜底正文"（提交进版本库的那一份）
            fallback = "（兜底正文：这份不该被覆盖，除非生成成功）\n"
            (root / "RELEASE_BODY.md").write_text(fallback, encoding="utf-8")

            # ⚠️ **真的把两个 zip 摆出来**，而且大小是"能走进两个不同分支"的：
            #    正文里的大小是**现场量文件**量出来的（Get-SizeText），
            #    文件不在时它会退回"大小见附件" —— 那样这条路径就永远测不到，
            #    而"正文里写的大小跟实际附件对不上"正是最该拦住的错。
            #    取 1.4MB（<10MB，走"保留一位小数"那条）和 12MB（>=10MB，
            #    走"取整"那条），两个分支各盖一次。不真写 55MB：
            #    这里要验的是**格式化逻辑**，不是磁盘吞吐。
            (root / ZIP_FX).write_bytes(b"x" * int(1.4 * 1024 * 1024))
            (root / ZIP_SC).write_bytes(b"x" * int(12 * 1024 * 1024))

            step_script = script  # tag 已经替换过了（见上面）
            ps1 = Path(td) / "step.ps1"
            ps1.write_text(step_script, encoding="utf-8-sig")

            env = dict(os.environ)
            env.update({
                # 两个 zip 都要给：正文现在要同时写出**两个包的下载说明**，
                # 少给一个的话那一段会变成空文件名 —— 而正文照样生成得出来，
                # 所以下面的断言必须真的去正文里找这两个名字。
                "ZIP": ZIP_FX,
                "ZIP_SC": ZIP_SC,
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
                fail_case(label, f"这一步 PowerShell 自己退成了 {proc.returncode}",
                          "不是某条判据红的，多半是脚本抛异常；看上面的 stderr")
                continue

            body = (root / "RELEASE_BODY.md").read_text(encoding="utf-8")
            print("  ---- RELEASE_BODY.md ----")
            for line in body.splitlines():
                print("  | " + line)

            checks = [
                ("正文里有真实的小包 zip 名", "Mosslight-2.0-v2.0.0-win64.zip" in body),
                # ⚠️ 这一条以前没有（单包时代只有上面那一条）。两个包之后，
                #    只写小包名是**最容易犯又最不容易发现**的错：正文看起来
                #    完全正常，只是大包没被提到 —— 而"两个包"这件事的意义
                #    全在"用户知道还有另一个可以下"。
                ("正文里有真实的大包 zip 名", "Mosslight-2.0-v2.0.0-win64-selfcontained.zip" in body),
                ("正文里有下载小节", "## 下载" in body),
                ("正文里有 compare 链接", "compare/v1.9.0...v2.0.0" in body),
                # ⚠️ 这一条 2026-10 改过：语言文件改成内嵌资源之后，产物是**一个 exe**，
                #    正文不能再写"别只拷 exe / 要和 Assets\ 放一起"那种话 ——
                #    那是旧形态的说明，留着会让用户以为漏了个目录。
                #    现在要同时说清两件事：①解压就一个 exe；②想改翻译的话
                #    `Assets\lang\xx.json` 是**可选**的覆盖层。
                ("正文说了解压出来就一个 exe", "解压出来就一个" in body),
                ("正文里没有「别只拷 exe」这类旧说法", "别只把 exe 拷出去" not in body),
                ("正文说了改翻译是可选的覆盖层（Assets\\lang）", "Assets\\lang" in body),
                ("没写死任何一个版本号以外的旧版本", "v2.0.0-test" not in body),
                # ---------------- 两个包之后新增的判据 ----------------
                # 这几条盯的是"用户看完正文知不知道该点哪个下载"，而不是
                # "正文长得对不对"。少了它们，正文可以做到两个 zip 名都在、
                # 但用户完全不知道该下哪个 —— 那等于白做两个包。
                ("正文里给了「不确定下哪个」的粗暴判据", "不确定就" in body),
                ("正文里说了大包是零依赖 / 自带运行时", "自带运行时" in body),
                ("正文里有 .NET 桌面运行时的官方下载链接",
                 "dotnet.microsoft.com/download/dotnet/10.0" in body),
                ("正文里点名了要装的是 Desktop Runtime（不是 SDK）", "Desktop Runtime" in body),
                # 大小必须是**现场量出来的真实值**（约 x MB），不是写死的占位符。
                # 上面真的摆了 1.4MB / 12MB 两个文件，所以这里能断言到具体数字：
                #   1.4MB → '约 1.4 MB'（小于 10MB，保留一位小数）
                #   12MB  → '约 12 MB' （大于 10MB，取整）
                # 只断言"有大小字样"是不够的 —— 那样写死一句"约 2 MB"也能过。
                ("正文里写的是**量出来的**小包大小（约 1.4 MB）", "约 1.4 MB" in body),
                ("正文里写的是**量出来的**大包大小（约 12 MB）", "约 12 MB" in body),
                ("正文里没有退回「大小见附件」那种兜底说法", "大小见附件" not in body),
            ]
            if "精确匹配" in label:
                checks.append(("用的是 CHANGELOG 的 v2.0.0 小节", "新增日志窗口" in body))
                checks.append(("空行还在（没塌成一行）", "\n\n" in body))
            if label.startswith("CHANGELOG 里只有空"):
                checks.append(("空的「未发布」没被当成正文", "这一版还没写说明" not in body))
            if label.startswith("仓库里根本没有"):
                checks.append(("说了去哪儿看改动", "提交记录" in body))
            if label.startswith("仓库里那份真的"):
                # 这一条是"人写的那份文件真的被用上了"的唯一证据：
                # 不查它的话，`## 未发布` 写成 `### 未发布` 也能过 ——
                # 正文照样生成得出来，只是内容退回"没找到说明"。
                #
                # ⚠️ 期望的**不是**固定的「未发布」：workflow 的规则是
                #    "精确匹配 tag 的小节优先，找不到才退回未发布"，所以这里
                #    按同一条规则算出"本该用哪一节"再比。
                #    （2026-10 发 2.0.0 时踩到：把「未发布」改名成 `## v2.0.0`
                #      之后，正文走的是精确匹配那条路，而旧断言还在找未发布的首行，
                #      于是这条门禁在**正确行为**下红了。）
                expected = section_text(changelog, tag) or unreleased
                if expected:
                    marker = expected.splitlines()[0].strip()[:40]
                    checks.append((f"真的用了 CHANGELOG 里那一节的内容（{marker[:16]}…）",
                                   marker in body))
                checks.append(("没有退回「没找到说明」", "没有在" not in body))

            for what, ok in checks:
                print(f"  [{'OK' if ok else 'X '}] {what}")
                if not ok:
                    fail += 1
                    fail_case(label, what)

    # 最后：新版本号规则（不带 v 前缀）那一节 —— compare 链接必须跟着走
    fail += check_new_versioning(template)

    print("=" * 74)
    # ⚠️ 这段**必须在「结果」之前**：CI 日志只会留最后一屏，
    #    把清单放在结果后面就等于没放。
    if FAIL_LIST:
        print(f"失败清单（{len(FAIL_LIST)} 项）—— 上面那些 [X ] 就是它们：")
        for i, item in enumerate(FAIL_LIST, 1):
            print(f"  {i}. {item}")
        print("=" * 74)
    print("结果：", "全部通过" if fail == 0 else f"{fail} 处不通过")
    return 1 if fail else 0

if __name__ == "__main__":
    raise SystemExit(main())
