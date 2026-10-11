# -*- coding: utf-8 -*-
"""验证"版本号 → FileVersion"的推导：哪些 tag 会踩 CS7035（版本字符串格式警告）

背景：release.yml 里原来是
    -p:Version=${{ ...num }} -p:FileVersion=${{ ...num }}
而 `2.0.0-test` 这种**预发布**版本号喂给 `FileVersion` 会出
    warning CS7035: 指定版本字符串 '2.0.0-test' 不符合建议格式 - major.minor.build.revision
这个工程到处带 `-warnaserror`（用户要求 0 警告），所以这条警告 = 发版发不出去。

所以新增了一步推导 `filever`：只取**纯数字**的前四段（major.minor.build.revision）。
`Version` 保留完整字符串（NuGet / InformationalVersion 要它）。

用法：python tools\\check_version_norm.py
"""
import re
import sys

NUM = r"^[0-9A-Za-z][0-9A-Za-z.\-]*$"
SEMVERISH = r"^\d+\.\d+"


def derive(tag: str):
    """把 release.yml `归一化版本号` 那一步的逻辑照抄一遍（含抛错条件）"""
    num = re.sub(r"^v", "", tag)
    if not re.match(NUM, num):
        return f"抛错：字符不合法（{tag}）"
    if not re.match(SEMVERISH, num):
        return f"抛错：不像版本号（{tag}）"
    parts = num.split("-")[0].split(".")
    filever = ".".join(parts[:4])
    pre = bool(re.search(r"(beta|alpha|rc)", tag, re.IGNORECASE))
    return num, filever, pre


# 合法 FileVersion 的判据（CS7035 就是按这个报的）：major[.minor[.build[.revision]]]，每段是数字
FILEVER_OK = re.compile(r"^\d+(\.\d+){0,3}$")

cases = [
    "v2.0.0",
    "v2.0.0-beta",
    "v2.0.0-beta.1",
    "v2.0.0-rc1",
    "v2.0.0-test",          # 本地彩排常用的写法
    "v2.0.0-BETA",          # 大写也该认成预发布
    "v2.1.3.4",
    "v2.0",
    "v2.0.0-alpha+build5",  # 应被拒绝（+ 不允许）
    "v2.0.0/../..",         # 应被拒绝（路径字符）
    "release",              # 应被拒绝（不像版本号）
    "",                     # 空
    "v10.0.12",
]

fail = 0
print(f"{'tag':<22} {'num':<18} {'filever':<12} {'预发布':<7} 判定")
for tag in cases:
    r = derive(tag)
    if isinstance(r, str):
        print(f"{tag!r:<22} {'—':<18} {'—':<12} {'—':<7} {r}")
        continue
    num, filever, pre = r
    ok = bool(FILEVER_OK.match(filever))
    print(f"{tag:<22} {num:<18} {filever:<12} {str(pre):<7} "
          f"{'OK' if ok else '<<< FileVersion 会触发 CS7035'}")
    if not ok:
        fail += 1

print()
print("结论：只要 filever 只取「纯数字前四段」，就没有一个 case 会踩 CS7035。")
sys.exit(1 if fail else 0)
