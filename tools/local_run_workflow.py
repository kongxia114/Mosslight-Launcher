"""把 GitHub Actions 的工作流**在本地跑一遍**（只跑 run: 步骤）

为什么值得写这个：workflow 的 YAML 能解析 ≠ 里面的 PowerShell 能跑。
`${{ }}` 替换、GITHUB_OUTPUT/GITHUB_ENV 的传递、Step 之间靠 env 传值 ——
这些只有真跑一遍才知道。而推上去才发现，一轮就是几分钟 + 一次红色的提交。

它做的事：
  - 在仓库的一份**临时副本**里跑（步骤会改 AppInfo.cs、生成 publish-fx/ 之类，不能污染真仓库）
  - 按顺序执行每个 run: 步骤，uses: 步骤跳过（本地没有那些 action）
  - 模拟 GITHUB_OUTPUT / GITHUB_ENV / RUNNER_TEMP / GITHUB_REF_NAME 等
  - 把 ${{ ... }} 用给定的上下文替换掉

⚠️ 跑 release.yml 现在是**重**的：要 publish **两个**包（框架依赖 + 自带运行时，
   后者 60MB 上下、还要压缩）、各拷到干净目录冒烟一次（都真的建窗口截图）、
   再打两个 zip。整套下来比只有一个包的时候明显慢，**这是预期的**，
   不是卡死了。只想快验语法/传值的话用 ci.yml（它只试发布**小包**）。

   这里**不需要**为两个包做任何特殊处理：ZIP / ZIP_SC 是"打 zip"那一步
   写进 GITHUB_ENV、再由这个脚本读回来塞进上下文的，
   跟其它 env 变量走的是同一条路。

用法：
    python tools/local_run_workflow.py .github/workflows/release.yml --tag v2.0.0
    python tools/local_run_workflow.py .github/workflows/ci.yml --event workflow_dispatch
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:
    print("需要 pyyaml：python -m pip install pyyaml")
    raise SystemExit(2)

REPO = Path(__file__).resolve().parent.parent


def copy_repo(dest: Path) -> None:
    """复制仓库到临时目录（跳过编译产物、.git、IDE 缓存，省时间）

    ⚠️ `.vs` 必须跳过：Visual Studio 开着的时候里面那些 `.vsidx` 是**被锁住的**，
    `copytree` 撞上就是一个 WinError 32 直接把整个脚本带崩（不是"跳过这个文件"，
    是抛异常）。Logs 同理（启动器写日志时可能正占着）。

    ⚠️ `publish` / `publish-fx` / `publish-sc` 三个都要跳过：release.yml 现在
    publish **两个**包，输出目录分别是 `publish-fx` 和 `publish-sc`
    （ci.yml 那个单包试发布用的是 `publish`）。
    不跳的话，本地跑过一次之后真仓库里会留着上次的产物
    （几十上百 MB），下一次 `copytree` 白拷一遍 —— 更糟的是
    "这次的产物"和"上次的残留"混在一起时，日志里会出现上一次的文件。
    """
    ignore = shutil.ignore_patterns(
        "bin", "obj", ".git", ".vs", "publish", "publish-fx", "publish-sc",
        "__pycache__", "Logs", "*.pdb")

    try:
        shutil.copytree(REPO, dest, ignore=ignore, dirs_exist_ok=True,
                        ignore_dangling_symlinks=True)
    except shutil.Error as exc:
        # copytree 是"攒够一批错误再一次性抛"，而不是遇到就停 —— 所以这里
        # **吞掉它继续跑**：没拷过去的顶多是某个被占用的文件，对构建没影响。
        print(f"（有 {len(exc.args[0])} 个文件没拷过去，已跳过）")


def find_shell() -> str:
    """找一个 PowerShell 来跑步骤。

    ⚠️ GitHub 的 `shell: pwsh` 用的是 **PowerShell 7**；本机可能只装了
    Windows PowerShell 5.1（`pwsh` 不在 PATH 上）。两者在
    `Set-Content -Encoding UTF8` 上有个差别（5.1 **带 BOM**、7 不带），
    所以本地跑过 ≠ CI 一定过 —— 但语法错、变量名写错、步骤之间传值传错
    这些**最常见的**问题，两边表现是一样的，够用了。
    """
    for candidate in ("pwsh", "powershell"):
        if shutil.which(candidate):
            return candidate
    raise SystemExit("找不到 pwsh / powershell")


def substitute(text: str, ctx: dict) -> str:
    """把 ${{ ... }} 换成上下文里的值；认不出来的原样留着（好让报错看得见）"""
    def repl(m):
        expr = m.group(1).strip()
        # 支持 `a == 'b'` 这种比较（只用在 with: 里，run: 里用不到，但保险）
        cmp = re.match(r"^(.+?)\s*==\s*'([^']*)'$", expr)
        if cmp:
            left = ctx.get(cmp.group(1).strip(), "")
            return "true" if str(left) == cmp.group(2) else "false"
        # 支持 `a || b` / `a && b`
        for op in ("||", "&&"):
            if op in expr:
                parts = [p.strip() for p in expr.split(op)]
                vals = [str(ctx.get(p, "")) for p in parts]
                if op == "||":
                    return next((v for v in vals if v and v != "false"), vals[-1])
                return next((v for v in vals if not v or v == "false"), vals[-1])
        return str(ctx.get(expr, ""))
    return re.sub(r"\$\{\{\s*(.+?)\s*\}\}", repl, text)


def parse_kv_file(path: Path) -> dict:
    """读 GITHUB_OUTPUT / GITHUB_ENV（key=value 一行一条；多行语法这里用不到）

    ⚠️ 要能读 **UTF-16**：PowerShell 5.1 的 `>>` 默认写 UTF-16LE（带 FF FE BOM），
    而 PowerShell 7 写 UTF-8。GitHub 的 `shell: pwsh` 是 PS7 所以没问题，
    但本地用 5.1 跑的时候就会在这儿炸成 UnicodeDecodeError。
    """
    if not path.exists():
        return {}
    raw = path.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        text = raw.decode("utf-16")
    else:
        text = raw.decode("utf-8-sig", errors="replace")

    out = {}
    for line in text.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def main() -> int:
    # ⚠️ 必须显式把 stdout 切到 UTF-8：这个脚本会把步骤的输出原样回显，
    #    而自检打的是 `✓` 和一堆中文 —— Windows 控制台默认 GBK，
    #    撞上 `✓` 直接 UnicodeEncodeError 把脚本带崩（不是显示乱码，是崩）。
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("workflow")
    ap.add_argument("--event", default="push")
    ap.add_argument("--tag", default="v2.0.0-test")
    ap.add_argument("--input-version", default="")
    ap.add_argument("--keep", action="store_true", help="跑完保留临时目录")
    args = ap.parse_args()

    doc = yaml.safe_load((REPO / args.workflow).read_text(encoding="utf-8"))
    jobs = doc.get("jobs", {})

    scratch = Path(tempfile.mkdtemp(prefix="dsh-act-"))
    work = scratch / "repo"
    shell = find_shell()
    print(f"临时副本：{work}")
    print(f"PowerShell：{shell}")
    copy_repo(work)

    runner_temp = scratch / "runner"
    runner_temp.mkdir(parents=True, exist_ok=True)

    # 这些是 GitHub 会给的上下文
    ctx = {
        "github.event_name": args.event,
        "github.ref_name": args.tag,
        "github.ref": f"refs/tags/{args.tag}",
        "github.repository": "kongxia114/Mosslight-Launcher",
        "runner.os": "Windows",
        "runner.temp": str(runner_temp),
        "inputs.version": args.input_version,
        "inputs.prerelease": "false",
    }
    process_env = dict(os.environ)
    process_env.update({
        "GITHUB_REPOSITORY": ctx["github.repository"],
        "GITHUB_REF_NAME": ctx["github.ref_name"],
        "GITHUB_EVENT_NAME": ctx["github.event_name"],
        "RUNNER_TEMP": str(runner_temp),
    })

    # ⚠️ `NUGET_PACKAGES` / `DOTNET_CLI_HOME` **只在真的设了值的时候才传下去**。
    #
    # 以前这里是 `os.environ.get("NUGET_PACKAGES", "")` —— 本机没设这个变量时
    # 传下去的是**空字符串**，而那是"设了、但设成了空"：
    #   · 空字符串在 PowerShell / .NET 里会喂给 Path.Combine 拼出**垃圾路径**
    #     （实测拼成了 `<仓库>\..\..\..\..\..\Program Files\dotnet\library-packs\`），
    #   · 于是 MSBuild 在 `Microsoft.PackageDependencyResolution.targets` 里
    #     找不到 `Microsoft.NET.ILLink.Tasks`，
    #   · 报的是 `error NETSDK1064: 未找到版本为 10.0.12 的包` ——
    #     **只在 `dotnet publish` 那一步炸**（`dotnet restore` / `build` 都不受影响），
    #     看着特别像"这次改的东西把发布搞坏了"，其实跟被改的代码一点关系都没有。
    #
    # 不设这个变量（而不是设成空）时，dotnet 用默认的 `~/.nuget/packages`，
    # 也就是 GitHub runner 上的行为 —— 那才是要模拟的东西。
    for name in ("NUGET_PACKAGES", "DOTNET_CLI_HOME"):
        value = os.environ.get(name)
        if value:
            process_env[name] = value
        else:
            process_env.pop(name, None)

    failures = []
    for job_name, job in jobs.items():
        print(f"\n=== job: {job_name} ===")
        for i, step in enumerate(job.get("steps", []), 1):
            label = step.get("name", step.get("uses", f"step {i}"))

            if "uses" in step and "run" not in step:
                print(f"  [{i}] 跳过（uses）: {label}")
                continue

            script = substitute(step.get("run", ""), ctx)
            step_id = step.get("id")

            out_file = runner_temp / f"out_{i}.txt"
            env_file = runner_temp / f"env_{i}.txt"
            out_file.write_text("", encoding="utf-8")
            env_file.write_text("", encoding="utf-8")

            step_env = dict(process_env)
            step_env.update({k: substitute(str(v), ctx) for k, v in (job.get("env") or {}).items()})
            step_env.update({k: substitute(str(v), ctx) for k, v in (step.get("env") or {}).items()})
            step_env["GITHUB_OUTPUT"] = str(out_file)
            step_env["GITHUB_ENV"] = str(env_file)

            ps1 = scratch / f"step_{i}.ps1"
            # ⚠️ 必须**带 BOM** 写：Windows PowerShell 5.1 读无 BOM 的 .ps1 会按
            #    本地 ANSI 解码，脚本里那些中文（错误信息、正则）会当场变乱码。
            #    PS 7 两种都认，所以带 BOM 是两边都安全的写法。
            ps1.write_text(script, encoding="utf-8-sig")

            print(f"  [{i}] 跑: {label}")
            proc = subprocess.run(
                [shell, "-NoProfile", "-NonInteractive", "-File", str(ps1)],
                cwd=work, env=step_env, capture_output=True, text=True,
                encoding="utf-8", errors="replace",
            )

            tail = (proc.stdout or "").strip().splitlines()
            for line in tail[-6:]:
                print(f"        | {line}")
            if proc.returncode != 0:
                print(f"        !! 退出码 {proc.returncode}")
                for line in (proc.stderr or "").strip().splitlines()[-14:]:
                    print(f"        ! {line}")
                failures.append(label)
                break

            # 步骤产出传给后面的步骤
            if step_id:
                for k, v in parse_kv_file(out_file).items():
                    ctx[f"steps.{step_id}.outputs.{k}"] = v
            for k, v in parse_kv_file(env_file).items():
                ctx[f"env.{k}"] = v
                process_env[k] = v
                print(f"        -> env.{k} = {v}")

    print()
    if failures:
        print(f"有 {len(failures)} 个步骤失败：")
        for f in failures:
            print(f"  ✗ {f}")
    else:
        print("全部 run: 步骤都过了")

    if args.keep or failures:
        print(f"临时目录保留在：{scratch}")
    else:
        shutil.rmtree(scratch, ignore_errors=True)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
