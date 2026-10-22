"""生成启动引擎的金标准（launch_golden.json + fixtures/）

为什么要这么干：启动命令拼装是**这个项目里最容易"看着对、其实错"的地方** ——
rules 的"最后一条匹配生效"、`${version_type}` 的取值、老版本的 `-Djava.library.path`、
log4j 配置的路径替换，每一条都是编译能过、跑起来也不报错、但游戏就是起不来（或者
起来以后行为不对）的那种错。

所以这里**用 Python 版当权威**，把同一份 fixture 喂给两边，把结果逐值比出来。

fixture 是脚本写出来的（不是手打的），这样 C# 自检和这份金标准看到的是**同一批文件**，
不会出现"两边各写一份、写着写着不一样了"。

用法：
    python gen_launch_golden.py
输出：
    tests/fixtures/<mc_dir>/...   给两边共用的假 .minecraft
    tests/launch_golden.json      金标准
"""

import base64
import json
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
FIXTURES = REPO / "tests" / "fixtures"
MC_DIR = FIXTURES / "mc"
OUT = REPO / "tests" / "launch_golden.json"

sys.path.insert(0, str(REPO.parent / "DHML-src"))
sys.path.insert(0, str(Path(r"D:\开发工程\DHML-src")))


# ---------------------------------------------------------------- fixture

# 一个"长得像 1.20.1"的原版 JSON：带 arguments（jvm + game）、带 rules 的参数、
# 带 natives 的库、带 log4j 配置。刻意把几个坑都放进来：
#   - jvm 里有一条 os 规则（非 windows 才加的 -Dfoo），必须被过滤掉
#   - game 里 --width/--height 藏在 has_custom_resolution 后面（默认 True → 要加）
#   - --demo 藏在 is_demo_user 后面（默认 False → 不能加）
VANILLA = {
    "id": "1.20.1",
    "type": "release",
    "mainClass": "net.minecraft.client.main.Main",
    "releaseTime": "2023-06-12T13:25:51+00:00",
    "javaVersion": {"component": "java-runtime-gamma", "majorVersion": 17},
    "assetIndex": {"id": "5", "sha1": "aaaa", "size": 123, "url": "https://example.invalid/5.json"},
    "assets": "5",
    "downloads": {
        "client": {"sha1": "bbbb", "size": 24000, "url": "https://example.invalid/client.jar"}
    },
    "logging": {
        "client": {
            "argument": "-Dlog4j.configurationFile=${path}",
            "file": {"id": "client-1.12.xml", "sha1": "cccc", "size": 888,
                     "url": "https://example.invalid/client-1.12.xml"},
            "type": "log4j2-xml",
        }
    },
    "libraries": [
        {"name": "com.example:always:1.0",
         "downloads": {"artifact": {"path": "com/example/always/1.0/always-1.0.jar",
                                    "sha1": "d1", "size": 1,
                                    "url": "https://example.invalid/always.jar"}}},
        # 只有 natives、没有主 artifact 的库（LWJGL 2 那种）：**不该进 classpath**，
        # 也**不该算"缺失"** —— 这是 1.0.0 踩过的坑
        {"name": "com.example:nativesonly:2.0",
         "downloads": {"classifiers": {
             "natives-windows": {"path": "com/example/nativesonly/2.0/nativesonly-2.0-natives-windows.jar",
                                 "sha1": "d2", "size": 2,
                                 "url": "https://example.invalid/nativesonly.jar"}}},
         "natives": {"windows": "natives-windows"},
         "extract": {"exclude": ["META-INF/"]}},
        # 只在 osx 上要的库 → 不该进 classpath
        {"name": "com.example:maconly:3.0",
         "rules": [{"action": "allow", "os": {"name": "osx"}}],
         "downloads": {"artifact": {"path": "com/example/maconly/3.0/maconly-3.0.jar",
                                    "sha1": "d3", "size": 3,
                                    "url": "https://example.invalid/maconly.jar"}}},
        # 先 allow 再 disallow windows → **最后一条匹配生效** → 不加
        {"name": "com.example:lastwins:4.0",
         "rules": [{"action": "allow"}, {"action": "disallow", "os": {"name": "windows"}}],
         "downloads": {"artifact": {"path": "com/example/lastwins/4.0/lastwins-4.0.jar",
                                    "sha1": "d4", "size": 4,
                                    "url": "https://example.invalid/lastwins.jar"}}},
    ],
    "arguments": {
        "jvm": [
            {"rules": [{"action": "allow", "os": {"name": "osx"}}], "value": ["-Dmac.only=true"]},
            "-Djava.library.path=${natives_directory}",
            "-Dminecraft.launcher.brand=${launcher_name}",
            "-cp", "${classpath}",
        ],
        "game": [
            "--username", "${auth_player_name}",
            "--version", "${version_name}",
            "--gameDir", "${game_directory}",
            "--assetsDir", "${assets_root}",
            "--assetIndex", "${assets_index_name}",
            "--uuid", "${auth_uuid}",
            "--accessToken", "${auth_access_token}",
            "--userType", "${user_type}",
            "--versionType", "${version_type}",
            {"rules": [{"action": "allow", "features": {"has_custom_resolution": True}}],
             "value": ["--width", "${resolution_width}", "--height", "${resolution_height}"]},
            {"rules": [{"action": "allow", "features": {"is_demo_user": True}}],
             "value": ["--demo"]},
        ],
    },
}

# 加载器版本：**只写 inheritsFrom、不写 jar**（Fabric / NeoForge 的真实写法）。
# ⚠️ 这正是"去找 <子版本id>.jar（不存在）→ classpath 里没有 Minecraft 的类 →
#    Fabric 报 couldn't locate the game"那个坑的现场。
FABRIC = {
    "id": "1.20.1-fabric",
    "inheritsFrom": "1.20.1",
    "releaseTime": "2023-06-13T00:00:00+00:00",
    "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
    "libraries": [
        {"name": "net.fabricmc:fabric-loader:0.15.7",
         "downloads": {"artifact": {"path": "net/fabricmc/fabric-loader/0.15.7/fabric-loader-0.15.7.jar",
                                    "sha1": "e1", "size": 5,
                                    "url": "https://example.invalid/loader.jar"}}},
    ],
    "arguments": {"jvm": ["-DFabricMcEmu=net.minecraft.client.main.Main"], "game": []},
}

# OptiFine 那种：**没有 jar 字段**，但自己目录里躺着一个 <id>.jar（打过补丁的客户端）。
# 官方启动器的规矩是"自己目录里那个优先" —— 照 JSON 回退会退回父版本的原版 jar，
# 等于 OptiFine 白装了，而且一声不响。
OPTIFINE_ID = "1.20.1-OptiFine_HD_U_I6"
OPTIFINE = {
    "id": OPTIFINE_ID,
    "inheritsFrom": "1.20.1",
    "releaseTime": "2023-08-01T00:00:00+00:00",
    "type": "release",
    "libraries": [],
}

# Forge 1.16- 的写法：显式写了 `"jar": "<父版本 id>"`，**不能被自己目录里的 jar 顶掉**
FORGE_OLD_ID = "1.16.5-forge-36.2.34"
FORGE_OLD = {
    "id": FORGE_OLD_ID,
    "inheritsFrom": "1.20.1",
    "releaseTime": "2021-01-01T00:00:00+00:00",
    "jar": "1.20.1",
    "libraries": [],
}


def write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


# 一个"缺了、而且补得了"的库：JSON 里有 url，本地没有文件。
#
# ⚠️ 这一条是 core/repair.py 的用例 —— 别的库要么本地有（always）、
#    要么只有 natives 没有主 jar（nativesonly）、要么规则不匹配（maconly
#    / lastwins），**都不算缺**。三种情况都要在金标准里体现出来。
#
# ⚠️ 必须在**模块级**加（不能塞进 build_fixture 里、更不能塞进 write_json 里）：
#    build_fixture 是先把 1.20.1.json 写下去的，加晚了那一份就没有这一条；
#    塞进 write_json 更糟 —— 那个函数被调用好几次，会重复 append。
VANILLA["libraries"].append({
    "name": "com.example:gonelib:5.0",
    "downloads": {"artifact": {
        "path": "com/example/gonelib/5.0/gonelib-5.0.jar",
        "sha1": "d5" * 20, "size": 5,
        "url": "https://example.invalid/gonelib.jar"}},
})


def build_fixture() -> None:
    if FIXTURES.exists():
        shutil.rmtree(FIXTURES)
    MC_DIR.mkdir(parents=True)

    write_json(MC_DIR / "versions" / "1.20.1" / "1.20.1.json", VANILLA)
    write_json(MC_DIR / "versions" / "1.20.1-fabric" / "1.20.1-fabric.json", FABRIC)
    write_json(MC_DIR / "versions" / OPTIFINE_ID / f"{OPTIFINE_ID}.json", OPTIFINE)
    write_json(MC_DIR / "versions" / FORGE_OLD_ID / f"{FORGE_OLD_ID}.json", FORGE_OLD)

    # OptiFine 自己目录里那个打过补丁的 jar
    (MC_DIR / "versions" / OPTIFINE_ID / f"{OPTIFINE_ID}.jar").write_bytes(b"optifine-patched")

    # 真实存在的库文件（classpath 里该出现的那两个）
    for rel in ("com/example/always/1.0/always-1.0.jar",
                "net/fabricmc/fabric-loader/0.15.7/fabric-loader-0.15.7.jar"):
        p = MC_DIR / "libraries" / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(b"jar")

    # 客户端 jar
    (MC_DIR / "versions" / "1.20.1" / "1.20.1.jar").write_bytes(b"client")

    # 版本隔离的特征：saves/ 出现在版本目录里。
    #
    # ⚠️ **里面必须放一个文件**。git 不跟踪空目录 —— 只 mkdir 的话，
    #    这个目录在 `git clone` 之后**根本不存在**，于是 CI 上
    #    `1.20.1-fabric` 的 isolated 变成 false，自检报
    #    「期望 True 实际 False」；而本地因为是刚生成的就一直是过的。
    #    这种"本地绿、CI 红"最难查，所以这里显式留一个占位文件。
    saves = MC_DIR / "versions" / "1.20.1-fabric" / "saves"
    saves.mkdir(parents=True, exist_ok=True)
    (saves / ".gitkeep").write_text(
        "这个文件只是为了让 saves/ 能被 git 跟踪（git 不跟踪空目录）。\n",
        encoding="utf-8")

    # 一个坏版本目录（json 是坏的）→ 必须只记进 errors，不能拖垮整次扫描
    bad = MC_DIR / "versions" / "broken"
    bad.mkdir(parents=True, exist_ok=True)
    (bad / "broken.json").write_text("{ this is not json", encoding="utf-8")

    # ⚠️ 一条**带 UTF-8 BOM** 的版本 JSON —— 必须是**能认出来**的正常版本。
    #
    # 这一条是回归用例：`JsonNode.Parse(byte[])` 内部的 Utf8JsonReader
    # **明确不支持跳过 BOM**，喂进去第一句就是
    # 「0xEF is an invalid start of a value」。而 1.0.0 那边读版本 JSON 用的是
    # `encoding="utf-8-sig"`，本来就防着这件事 —— 记事本改一下 json 就会带上 BOM，
    # 于是那个版本会**从列表里凭空消失**，只在扫描告警里留一句"json 格式错误"。
    # （用户真机上就是这么发现的：临时游戏目录里手写的版本一直扫不到。）
    bom_version = MC_DIR / "versions" / "1.20.1-bom"
    bom_version.mkdir(parents=True, exist_ok=True)
    (bom_version / "1.20.1-bom.json").write_bytes(
        b"\xef\xbb\xbf" + json.dumps(
            {"id": "1.20.1-bom", "type": "release",
             "mainClass": "net.minecraft.client.main.Main", "libraries": []},
            ensure_ascii=False).encode("utf-8"))
    # 客户端 jar 也放一个，让它算"完整"
    (bom_version / "1.20.1-bom.jar").write_bytes(b"client")

    # 一个"不是版本目录"的目录（没有带 id 的 json）→ 必须被静默跳过。
    # ⚠️ 同样要塞个文件进去：空的会被 git 丢掉，那这条用例在 CI 上就白测了。
    native_libs = MC_DIR / "versions" / "native-libraries"
    native_libs.mkdir(parents=True, exist_ok=True)
    (native_libs / "lwjgl.dll").write_bytes(b"not a version dir")

    # log4j 配置（launch.py 只在文件真的存在时才加那个参数）
    logcfg = MC_DIR / "assets" / "log_configs" / "client-1.12.xml"
    logcfg.parent.mkdir(parents=True, exist_ok=True)
    logcfg.write_text("<Configuration/>", encoding="utf-8")

    build_mods_fixture()


# 专门给"mod 该装到哪"那套反查用的 fixture。
# ⚠️ 单独一个目录，不塞进上面的 MC_DIR：那里面已经有一份扫描用的 fixture，
#    混在一起会让"扫描顺序"那条用例跟着一起变（一个用例动到另一条，最难查）。
MODS_MC = FIXTURES / "mc-mods"

# 文件夹名照 PCL2 / HMCL 的真实写法来 —— 这几条正是 `loader_token`
# 那个"整体比对"要防的：
#   · `1.20.1-NeoForge_47.1.106`：找 forge **不能**命中它（"neoforge" 含 "forge"）
#   · `1.20.1-Fabric 0.19.5` vs `1.20.1-Fabric 0.19.10`：装了两份，要挑版本号高的
#   · `1.20.4-legacy-fabric-0.14.0`：带前缀的写法，兜底那条要认得
MODS_VERSION_DIRS = (
    "1.20.1",
    "1.20.1-Fabric 0.19.5",
    "1.20.1-Fabric 0.19.10",
    "1.12.2-Forge_14.23.5.2864",
    "1.20.1-NeoForge_47.1.106",
    "1.20.4-legacy-fabric-0.14.0",
    "1.20.1-1.20.1-forge-51.0.0",       # Forge 官方安装器生成的中间目录
    "empty-folder",                      # 没有 json → 不该被当成版本
)


def build_mods_fixture() -> None:
    if MODS_MC.exists():
        shutil.rmtree(MODS_MC)
    for name in MODS_VERSION_DIRS:
        d = MODS_MC / "versions" / name
        d.mkdir(parents=True, exist_ok=True)
        if name == "empty-folder":
            continue                    # 故意不给 json
        write_json(d / f"{name}.json", {"id": name, "type": "release"})


# ---------------------------------------------------------------- 整合包

class _FakeZip:
    """`_find_index()` 只用到 `namelist()`，这里给个最小的替身

    ⚠️ 用真的 zipfile 也行，但那样"多层嵌套"这种用例得先造出一个真包来 ——
    这个函数是纯逻辑，给它一个名字列表就够了。
    """

    def __init__(self, names):
        self._names = list(names)

    def namelist(self):
        return list(self._names)


# 一个**真能装上**的 .mrpack。刻意放齐这几类：
#   · 三种 env：required / optional（要装）/ unsupported（服务端专用，要跳过）
#   · 一条**不安全路径**（`../evil.txt`）→ 必须被挡下并记 warning
#   · 一条**重复落点** → 只装第一个
#   · 一条**没有下载地址** → 记 warning、不进清单
#   · 覆盖层：overrides 里一个、client-overrides 里一个（后者要盖前者）
MRPACK_INDEX = {
    "formatVersion": 1,
    "game": "minecraft",
    "versionId": "1.0.0",
    "name": "Cobblemon Official Modpack [Fabric]",
    "summary": "一个测试用的整合包",
    "dependencies": {"minecraft": "1.20.1", "fabric-loader": "0.15.7"},
    "files": [
        {"path": "mods/sodium.jar", "hashes": {"sha1": "aa" * 20, "sha512": "bb" * 64},
         "fileSize": 100, "downloads": ["https://example.invalid/sodium.jar"],
         "env": {"client": "required", "server": "required"}},
        {"path": "mods/optional.jar", "hashes": {"sha1": "cc" * 20},
         "fileSize": 200, "downloads": ["https://example.invalid/optional.jar"],
         "env": {"client": "optional", "server": "optional"}},
        # 服务端专用 → 客户端**不装**，只记到 skipped
        {"path": "mods/serveronly.jar", "hashes": {"sha1": "dd" * 20},
         "fileSize": 300, "downloads": ["https://example.invalid/server.jar"],
         "env": {"client": "unsupported", "server": "required"}},
        # 不安全路径 → 挡下 + warning
        {"path": "../evil.txt", "hashes": {"sha1": "ee" * 20},
         "fileSize": 1, "downloads": ["https://example.invalid/evil.txt"]},
        # 重复落点 → 只装第一个
        {"path": "mods/sodium.jar", "hashes": {"sha1": "ff" * 20},
         "fileSize": 999, "downloads": ["https://example.invalid/other.jar"]},
        # 没有下载地址 → warning、不进清单
        {"path": "config/nourl.json", "hashes": {"sha1": "11" * 20}, "fileSize": 5,
         "downloads": []},
    ],
}


def build_mrpack(path: Path) -> None:
    """造一个真的 .mrpack（zip），C# 那边读同一份字节"""
    import zipfile

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("modrinth.index.json",
                    json.dumps(MRPACK_INDEX, ensure_ascii=False, indent=2))
        zf.writestr("overrides/config/settings.json", '{"from": "overrides"}')
        zf.writestr("client-overrides/config/settings.json", '{"from": "client"}')
        zf.writestr("overrides/mods/extra.jar", "jar-bytes")

# ---------------------------------------------------------------- 金标准


def normalize(args, mc_dir: Path) -> list:
    """把绝对路径换成占位符 —— 两边跑的时候根目录不一样

    ⚠️ 替换的是 `前缀 + 分隔符`，即结果里会留下 `<MC>/xxx`。**必须**统一走这一个
    函数：早先 natives_dir / classpath 那两处用的是"只替换前缀"的写法
    （`.replace(str(MC_DIR), "<MC>")`），于是同一个路径在 args 里是 `<MC>/…`、
    在 classpath 里是 `<MC>\\…` —— 两处对不上，C# 那边逐值比的时候就会报一堆
    "期望 <MC>\\… 实际 <MC>/…" 的假失败。占位符的写法只能有一处。
    """
    prefix = str(mc_dir)
    out = []
    for a in args:
        text = str(a).replace(prefix + "\\", "<MC>/").replace(prefix + "/", "<MC>/")
        out.append(text)
    return out


def normalize_one(path, mc_dir: Path) -> str:
    """单个路径的归一化 —— 跟 normalize() 用同一套规则"""
    return str(path).replace(str(mc_dir) + "\\", "<MC>/").replace(str(mc_dir) + "/", "<MC>/")


def main() -> int:
    build_fixture()

    try:
        from core.launch import (build_launch_plan, load_version, merge_version,
                                 load_version_json, rules_allow, maven_to_path)
    except Exception as exc:                                   # noqa: BLE001
        print(f"导入 1.0.0 的 core.launch 失败：{exc}")
        return 2

    golden = {}

    # ---------- 纯函数 ----------
    golden["offline_uuid"] = {
        name: __import__("core.launch", fromlist=["offline_uuid"]).offline_uuid(name)
        for name in ("Notch", "Steve", "BaBaLe", "")
    }
    golden["maven"] = {
        name: list(maven_to_path(name))
        for name in ("net.minecraft:client:1.20.1",
                     "org.lwjgl:lwjgl:3.3.1:natives-windows",
                     "net.minecraftforge:forge:1.20.1-47.2.0:universal@zip",
                     "bad", "")
    }
    golden["rules"] = {
        "empty": rules_allow([]),
        "windows": rules_allow([{"action": "allow", "os": {"name": "windows"}}]),
        "last_wins": rules_allow([{"action": "allow"},
                                  {"action": "disallow", "os": {"name": "windows"}}]),
        "osx": rules_allow([{"action": "allow", "os": {"name": "osx"}}]),
        "feature_on": rules_allow([{"action": "allow",
                                    "features": {"has_custom_resolution": True}}]),
        "feature_off": rules_allow([{"action": "allow",
                                     "features": {"is_demo_user": True}}]),
    }

    # ---------- 继承链合并 ----------
    merged = load_version(MC_DIR, "1.20.1-fabric")
    golden["merged_fabric"] = {
        "id": merged["id"],
        "mainClass": merged["mainClass"],
        "jar": merged["jar"],
        "type": merged["type"],
        "javaVersion": merged.get("javaVersion"),
        "assetIndex": (merged.get("assetIndex") or {}).get("id"),
        "library_names": [l.get("name") for l in merged.get("libraries") or []],
        "jvm_arg_count": len((merged.get("arguments") or {}).get("jvm") or []),
    }

    merged_opti = load_version(MC_DIR, OPTIFINE_ID)
    golden["merged_optifine_jar"] = merged_opti["jar"]
    merged_forge = load_version(MC_DIR, FORGE_OLD_ID)
    golden["merged_forge_jar"] = merged_forge["jar"]

    # ---------- 启动计划 ----------
    from core.launch import LaunchRequest as PyLaunchRequest

    def plan_for(vid, extra=None):
        kwargs = dict(
            mc_dir=MC_DIR, version_id=vid, java_path="C:\\java\\bin\\javaw.exe",
            username="BaBaLe", min_memory=512, max_memory=4096,
            width=1280, height=720, launcher_name="Mosslight",
            launcher_version="v2.0.0-alpha", locale="zh_CN",
        )
        if extra:
            kwargs.update(extra)
        plan = build_launch_plan(PyLaunchRequest(**kwargs))
        return {
            "args": normalize(plan.args, MC_DIR),
            "cwd_is_version_dir": str(plan.cwd) == str(MC_DIR / "versions" / vid),
            "natives_dir": normalize_one(plan.natives_dir, MC_DIR),
            "needs_natives": plan.needs_natives,
            "classpath": sorted(normalize_one(p, MC_DIR) for p in plan.classpath),
            "version_type": plan.version_type,
            "warnings": plan.warnings,
            "missing_libraries": plan.missing_libraries,
        }

    golden["plan_vanilla"] = plan_for("1.20.1")
    golden["plan_fabric"] = plan_for("1.20.1-fabric",
                                     {"loader_label": "Fabric", "isolated": True})
    # 自定义信息：填了就顶掉品牌默认值
    golden["plan_custom_info"] = plan_for("1.20.1", {"version_type": "MyLauncher/Test"})
    golden["plan_server"] = plan_for("1.20.1", {"server_address": "mc.example.com:25566"})
    # 额外参数：JVM 参数要在 -cp 之前；游戏参数要在最后
    golden["plan_extra"] = plan_for("1.20.1", {"extra_jvm_args": "-XX:+UseG1GC",
                                               "extra_game_args": "--width 1920"})
    golden["plan_locale"] = plan_for("1.20.1", {"locale": "zh_CN"})

    # ---------- 扫描 ----------
    from core.versions import VersionScanner
    scanner = VersionScanner(MC_DIR)
    scanned = scanner.scan()

    # ⚠️ **2.0 有意跟 1.0.0 不一样的一处，在生成端就改掉**（不能只改 JSON：
    #    否则下次重新生成又被 1.0.0 的语义冲回去，CI 又莫名其妙红一次）。
    #
    #    1.0.0 按**文件夹名**判整合包 —— 目录名不是纯版本号（`1.20.1-bom`）就归 pack；
    #    2.0 只认 modpack 元数据（见提交 a25423a「改名不再误判整合包」，
    #    判据在 Mosslight.Core/Services/Engine/VersionScanner.cs 的 Kind 赋值处），
    #    所以它是 vanilla，在列表里也从"整合包"那一段前移到"官方版本"那一段。
    #
    #    这里只覆盖**分类**；段内顺序沿用 1.0.0 算出来的相对次序（sort 是稳定的）。
    SCAN_KIND_2_0 = {"1.20.1-bom": "vanilla"}
    KIND_RANK = {"vanilla": 0, "loader": 1, "pack": 2}
    for item in scanned:
        item["kind"] = SCAN_KIND_2_0.get(item["id"], item["kind"])
    scanned.sort(key=lambda v: KIND_RANK.get(v["kind"], 2))

    golden["scan"] = {
        "order": [v["id"] for v in scanned],
        "kinds": {v["id"]: v["kind"] for v in scanned},
        # ⚠️ 两个都要：`loader` 是小写 key（fabric / neoforge），
        #    `loader_label` 是给界面显示的品牌名（Fabric / NeoForge）。
        #    只比其中一个的话，"NeoForge 被写成 Neoforge"这种错抓不住。
        "loaders": {v["id"]: v["loader"] for v in scanned},
        "loader_labels": {v["id"]: v["loader_label"] for v in scanned},
        "isolated": {v["id"]: v["isolated"] for v in scanned},
        "complete": {v["id"]: v["complete"] for v in scanned},
        "errors_count": len(scanner.errors),
    }

    # ---------- 加载器解析 ----------
    from core import loaders as L

    # 每家的回包形状都照实测的来（见 core/loaders.py 开头那张表）
    forge_payload = [
        {"version": "1.20.1-47.2.0", "build": 47, "modified": "2024-01-01",
         "files": [{"category": "installer", "format": "jar", "hash": "abc123"}]},
        {"version": "1.20.1-47.3.0", "build": 48, "modified": "2024-02-01",
         "files": [{"category": "installer", "format": "jar", "hash": "def456"}]},
        # ⚠️ 版本号比较必须按**分段数字**：47.10 要比 47.9 大，
        #    字符串比会反过来（"47.1" < "47.9"）
        {"version": "1.20.1-47.10.0", "build": 50, "modified": "2024-03-01",
         "files": [{"category": "installer", "format": "jar", "hash": "ghi789"}]},
        {"version": "1.20.1-47.9.0", "build": 49, "modified": "2024-02-15", "files": []},
        # 没有 version 的条目要丢掉
        {"build": 1},
    ]
    neoforge_payload = [
        {"mcversion": "1.20.1", "version": "47.1.5", "rawVersion": "1.20.1-47.1.5"},
        {"mcversion": "1.20.1", "version": "47.1.10", "rawVersion": "1.20.1-47.1.10"},
        {"mcversion": "1.20.1", "version": "47.1.9", "rawVersion": "1.20.1-47.1.9"},
    ]
    fabric_payload = [
        {"loader": {"version": "0.19.5", "stable": True}},
        {"loader": {"version": "0.9.0", "stable": True}},
        {"loader": {"version": "0.20.0-beta.9", "stable": False}},
        {"loader": {"version": "", "stable": True}},       # 空版本号要丢掉
    ]
    optifine_payload = [
        {"filename": "OptiFine_1.20.1_HD_U_I5.jar", "type": "HD_U", "patch": "I5",
         "forge": "Forge 47.0.35", "mcversion": "1.20.1"},
        {"filename": "OptiFine_1.20.1_HD_U_I6.jar", "type": "HD_U", "patch": "I6",
         "forge": "Forge 47.2.0", "mcversion": "1.20.1"},
        # ⚠️ 预览版必须被滤掉 —— 不滤的话 latest 会变成它（实测 1.21.1 踩过）
        {"filename": "preview_OptiFine_1.20.1_HD_U_I6_pre6.jar", "type": "preview",
         "patch": "preview6", "forge": "", "mcversion": "1.20.1"},
        {"filename": "OptiFine_1.20.1_HD_U_I7_pre1.jar", "type": "beta",
         "patch": "pre7", "forge": "", "mcversion": "1.20.1"},
    ]

    # 专门用来验"版本号按分段数字比"的一批。
    # ⚠️ 必须**单独一条用例**：`0.19.10 > 0.19.5` 和 `0.19.5 > 0.9.0`
    #    正是字符串比会搞反的两处，混在上面那份 fabric 回包里验不干净。
    version_key_payload = [
        {"loader": {"version": "0.9.0", "stable": True}},
        {"loader": {"version": "0.19.5", "stable": True}},
        {"loader": {"version": "0.19.10", "stable": True}},
        {"loader": {"version": "0.20.0", "stable": True}},
    ]

    golden["loaders"] = {
        # ⚠️ **输入也要存进去**：C# 那边拿同一份输入跑自己的解析器，
        #    再跟下面这些"Python 算出来的结果"比 —— 只存结果的话，
        #    C# 就得自己抄一份输入，抄着抄着两边就不是同一个用例了。
        "inputs": {
            "forge": forge_payload,
            "neoforge": neoforge_payload,
            "fabric": fabric_payload,
            "optifine": optifine_payload,
            "version_key_order": version_key_payload,
        },
        "order": list(L.LOADER_ORDER),
        "names": {k: L.LOADER_NAMES[k] for k in L.LOADER_ORDER},
        "supported": list(L.SUPPORTED),
        "urls": {k: L._url_for(k, "1.20.1") for k in L.LOADER_ORDER},
        "forge": L.parse_forge(forge_payload),
        "neoforge": L.parse_neoforge(neoforge_payload),
        "fabric": L.parse_fabric(fabric_payload),
        "quilt": L.parse_fabric(fabric_payload),      # 形状跟 Fabric 一样
        "optifine": L.parse_optifine(optifine_payload),
        # 回包形状不对时必须是**空列表**，不是异常
        "garbage": {
            "dict_unwrapped": L.parse_neoforge({"versions": neoforge_payload}),
            "number": L.parse_neoforge(42),
            "none": L.parse_neoforge(None),
            "string": L.parse_forge("nope"),
            "optifine_dict": L.parse_optifine({"a": 1}),
        },
        # 版本号比较：这些**必须**按分段数字比
        "version_key_order": [
            v["version"] for v in L.parse_fabric(version_key_payload)
        ],
        # 文件夹名拼法（PCL 约定：Fabric 空格、Forge/NeoForge/OptiFine 下划线）
        "folder_names": {
            "fabric": L.version_folder_name("1.21.4", "fabric", "0.19.5"),
            "forge": L.version_folder_name("1.12.2", "forge", "14.23.5.2864"),
            "neoforge": L.version_folder_name("1.20.1", "neoforge", "47.1.5"),
            "quilt": L.version_folder_name("1.20.1", "quilt", "0.20.0"),
            "optifine": L.version_folder_name("1.20.1", "optifine", "HD_U_I5"),
            "vanilla": L.version_folder_name("1.20.1", "", ""),
            "no_loader_version": L.version_folder_name("1.20.1", "fabric", ""),
        },
        "folder_parse": {
            text: list(L.parse_version_folder_name(text))
            for text in ("1.21.4-Fabric 0.19.5", "1.12.2-Forge_14.23.5.2864",
                         "1.20.1-OptiFine_HD_U_I5", "1.20.1", "weird-name-here",
                         "", "1.20.1-NeoForge_47.1.5")
        },
    }

    # ---------- 按版本覆盖参数（version_settings.py） ----------
    import tempfile
    from core.version_settings import VersionSettings

    with tempfile.TemporaryDirectory() as td:
        vs = VersionSettings(Path(td) / "versions.json")
        # 一次 update 把各种"要纠正的东西"都塞进去：
        #   前后空格的字符串、超范围的内存、空字符串、不认识的键
        vs.update("1.20.1", {
            "java_path": "  C:\\java\\bin\\javaw.exe  ",
            "max_memory": "4096",          # 字符串 → 要纠正成整数
            "min_memory": 8192,            # 比 max 还大 → resolve 时要夹回来
            "extra_jvm_args": "  -XX:+UseG1GC  ",
            "extra_game_args": "",         # 空 = 没设，不该存
            "icon": "",                    # 空 = 没设，不该存
            "unknown_key": "不该被留下",     # 不认识的键要丢掉
        })
        vs.update("1.20.2", {})            # 空的 = 恢复成全部跟随默认

        golden["version_settings"] = {
            "customized": sorted(vs.data.keys()),
            "stored_1_20_1": vs.get("1.20.1"),
            "stored_1_20_2": vs.get("1.20.2"),
            "is_custom": {
                f: vs.is_custom("1.20.1", f)
                for f in ("java_path", "min_memory", "max_memory",
                          "extra_jvm_args", "extra_game_args", "custom_info",
                          "server_address", "icon")
            },
            # ⚠️ 最小堆不能大于最大堆：默认 max 2048 + 版本自定义 min 8192
            #    不夹一下 JVM 会直接拒绝启动
            "resolved": vs.resolve("1.20.1", {
                "java_path": "", "min_memory": 512, "max_memory": 2048,
                "extra_jvm_args": "", "extra_game_args": "",
                "custom_info": "", "server_address": "", "icon": "",
            }),
            "resolved_defaults_only": vs.resolve("9.9.9", {
                "java_path": "/usr/bin/java", "min_memory": 512, "max_memory": 2048,
                "extra_jvm_args": "", "extra_game_args": "",
                "custom_info": "", "server_address": "", "icon": "",
            }),
        }

    # ---------- 安装器地址 / 输出节流（loader_setup.py） ----------
    from core import loader_setup as S

    golden["loader_setup"] = {
        "forge_maven": {
            # 1.13 起：`<mc>-<forge>`
            "1.20.1|47.4.0": S.forge_maven_url("1.20.1", "47.4.0"),
            # ⚠️ 老版本**多一个后缀**，不带后缀是 404
            "1.7.10|10.13.4.1614": S.forge_maven_url("1.7.10", "10.13.4.1614"),
            "1.8.9|11.15.1.2318": S.forge_maven_url("1.8.9", "11.15.1.2318"),
        },
        "installer_url": {
            "forge": S.installer_url("1.20.1", "forge", "47.4.0"),
            "neoforge_1_20_1": S.installer_url("1.20.1", "neoforge", "47.1.106"),
            "neoforge_1_21": S.installer_url("1.21", "neoforge", "21.0.167"),
            "optifine": S.installer_url("1.20.1", "optifine", "HD_U_I5",
                                        {"type": "HD_U", "patch": "I5"}),
        },
        "installer_file_name": {
            "forge": S.installer_file_name("forge", "1.20.1", "47.4.0"),
            "neoforge": S.installer_file_name("neoforge", "1.20.1", "47.1.106"),
            "optifine": S.installer_file_name("optifine", "1.20.1", "HD_U_I5"),
        },
        "installer_arguments": {
            "forge": S.installer_arguments("forge", "/mc"),
            "neoforge": S.installer_arguments("neoforge", "/mc"),
            # ⚠️ OptiFine 的安装器**不吃参数**（靠 cwd / APPDATA）
            "optifine": S.installer_arguments("optifine", "/mc"),
        },
        # 哪些行算"刷屏"（实测 3 万行里绝大部分是这些）
        "looks_like_listing": {
            text: S.looks_like_listing(text)
            for text in (
                "  com/mojang/blaze3d/Blaze3D.class",   # 是（裸路径）
                "net/minecraft/WorldlyContainer",       # 是
                "Downloading library from https://x",   # 不是（有空格，要显示）
                "java.net.preferIPv4Stack=true",        # 不是（有 =）
                "C:\\Users\\x\\file",                   # 不是（有 :）
                "",                                     # 不是
            )
        },
        "failure_hint": {
            # 套话信息量为零，真正的原因在上一行
            "generic": S.failure_hint(["Some real reason here",
                                       "There was an error during installation"]),
            "all_spam": S.failure_hint(["  Patching net/minecraft/A",
                                        "  com/x/Y.class",
                                        "There was an error during installation"]),
            "empty": S.failure_hint([]),
        },
    }

    # ---------- 补全缺失的库（repair.py） ----------
    from core import repair as R
    from core.launch import load_version as _lv

    vanilla_merged = _lv(MC_DIR, "1.20.1")
    golden["repair"] = {
        # ⚠️ 只有"JSON 里写了 url、本地又没有"的才算缺。另外三种**都不算**：
        #    · always：本地有那个文件
        #    · nativesonly：只有 classifiers，压根没有主 jar
        #    · maconly / lastwins：rules 在当前系统上不匹配
        "missing": [
            {"name": m["name"], "path": m["path"], "sha1": m["sha1"], "size": m["size"]}
            for m in R.missing_libraries(MC_DIR, vanilla_merged)
        ],
    }

    # ---------- mod 该装到哪（mods.py 后半段） ----------
    from core import mods as M

    golden["mods_dir"] = {
        "list_version_dirs": M.list_version_dirs(MODS_MC),
        "split": {n: list(M.split_version_folder(n)) for n in MODS_VERSION_DIRS},
        "loader_token": {
            tail: M.loader_token(tail)
            for tail in ("Fabric 0.19.5", "Forge_14.23.5.2864", "NeoForge_47.1.106",
                         "1.20.1-forge-51.0.0", "legacy-fabric-0.14.0", "", "OptiFine_HD_U_I5")
        },
        # (游戏版本, 加载器) → 匹配到的文件夹名 + 说明
        "match": {
            f"{gv}|{ld}": list(M.match_version_dir(MODS_MC, gv, ld))
            for gv, ld in (
                ("1.20.1", ""),              # 原版
                ("1.20.1", "fabric"),        # 两份 fabric → 取版本号高的
                ("1.20.1", "forge"),         # ⚠️ 不能命中 NeoForge
                ("1.20.1", "neoforge"),
                ("1.12.2", "forge"),
                ("1.20.4", "fabric"),        # 只有 legacy-fabric → 兜底
                ("1.21.4", "fabric"),        # 本地没有 → None
                ("", "fabric"),              # 不知道游戏版本
            )
        },
        "mods_dir_for": {
            f"{gv}|{ld}": M.mods_dir_for(MODS_MC, gv, ld).name
            for gv, ld in (("1.20.1", "fabric"), ("1.20.1", "forge"),
                           ("1.21.4", "fabric"), ("", ""))
        },
    }

    # ---------- 整合包（modpack.py） ----------
    from core import modpack as P

    # ⚠️ 名字这一列全是**真实整合包里出现过的**写法：方括号、斜杠、冒号、
    #    路径分隔符、结尾的点、Windows 保留名、超长。直接拿去建目录会失败。
    names = (
        "Cobblemon Official Modpack [Fabric]",
        "Better MC [FABRIC] - BMC2",          # 单个 `-` 是名字的一部分，**不能**被收掉
        "a/b:c", "x\\y", "  spaced  out  ",
        "trailing...", ".hidden", "CON", "con.txt", "NUL",
        "", "   ", "!!!", "a" * 100,
    )
    # ⚠️ 路径这一列是 **Zip Slip** 的现场：绝对路径 / 盘符 / 目录穿越 / NUL。
    #    任何一条漏过去，整合包就能往实例目录外面写文件。
    paths = (
        "mods/sodium.jar", "config/a/b.json",
        "../evil.txt", "mods/../../evil.txt", "/etc/passwd", "\\windows\\x",
        "C:/Windows/x.dll", "c:\\x", "a/./b", "a//b", "", "a\x00b",
        "./a", "mods/ok/../still-ok",             # 有 `..` → 不安全（哪怕最后落回来）
    )
    zip_names = [
        "modrinth.index.json",
        "overrides/config/a.json",
        "overrides/mods/x.jar",
        "overrides/",                              # 目录条目，跳过
        "overrides/../evil.txt",                   # 不安全，跳过
        "client-overrides/config/a.json",          # 要盖住 overrides 里那个
        "client-overrides/", "other/file.txt",
    ]
    nested_zip_names = ["pack/" + n for n in zip_names]

    golden["modpack"] = {
        "sanitize": {n: P.sanitize_instance_name(n) for n in names},
        "safe_relpath": {p: P.is_safe_relpath(p) for p in paths},
        "find_index": {
            "root": list(P._find_index(_FakeZip(["modrinth.index.json", "overrides/x"]))),
            "nested": list(P._find_index(_FakeZip(nested_zip_names))),
            "none": list(P._find_index(_FakeZip(["a.txt"]))),
            # 同时有根上那个和嵌套那个 → 取**最浅**的
            "both": list(P._find_index(_FakeZip(["modrinth.index.json", "p/modrinth.index.json"]))),
        },
        "find_overrides_root": [list(x) for x in P.find_overrides(zip_names)],
        "find_overrides_nested": [list(x) for x in P.find_overrides(nested_zip_names, base="pack/")],
    }

    # 一个真能跑的 .mrpack：C# 那边读**同一份字节**，验的是整条解析链
    mrpack_path = FIXTURES / "test.mrpack"
    build_mrpack(mrpack_path)
    golden["modpack"]["mrpack"] = {
        "bytes_b64": base64.b64encode(mrpack_path.read_bytes()).decode("ascii"),
    }
    plan = P.plan_from_bytes(mrpack_path.read_bytes())
    golden["modpack"]["plan"] = {
        "name": plan.name,
        "version_id": plan.version_id,
        "summary": plan.summary,
        "format_version": plan.format_version,
        "minecraft": plan.minecraft,
        "loader": list(plan.loader()),
        "dependencies": plan.dependencies,
        "files": [{"path": f.path, "url": f.url, "sha1": f.sha1, "size": f.size,
                   "client": f.client} for f in plan.files],
        "skipped": plan.skipped,
        "overrides": [list(x) for x in plan.overrides],
        "warnings": plan.warnings,
        "total_bytes": plan.total_bytes,
    }

    OUT.write_text(json.dumps(golden, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"写出 {OUT}")
    print(f"fixture 在 {MC_DIR}")
    print(f"扫描顺序：{golden['scan']['order']}")
    print(f"扫描告警：{golden['scan']['errors_count']} 条")
    print(f"加载器：fabric 排序 {golden['loaders']['version_key_order']}")
    print(f"缺的库：{[m['name'] for m in golden['repair']['missing']]}")
    print(f"mods 匹配：{ {k: v[0] for k, v in golden['mods_dir']['match'].items()} }")
    print(f"整合包：{plan.name} → {plan.minecraft} + {plan.loader()}，"
          f"{len(plan.files)} 个文件，跳过 {plan.skipped}，覆盖层 {len(plan.overrides)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
