# -*- coding: utf-8 -*-
"""把 Lucide 图标转成 WPF 的矢量路径（生成 `Mosslight.App\\Themes\\NavIcons.xaml`）

    python tools\\gen_nav_icons.py
    python tools\\gen_nav_icons.py --source "D:\\别处的\\lucide"

## 这些图标是哪儿来的

**Lucide**（<https://lucide.dev>，ISC 许可）。图标文件是 PCL2-CE 仓库里
带的那份（`PCL.Core/UI/Assets/IconPacks/lucide/*.svg`，`@license lucide-static - ISC`），
**用的是图标数据本身，不是 PCL2-CE 的代码**（那一层是 GPL-3.0，本仓库是 MIT）。
每个 svg 头一行 `<!-- @license lucide-static vX.Y.Z - ISC -->` 就是出处。

## 为什么要转

WPF **不能直接渲染 SVG**（没有内置的 SVG 解释器，装 SharpVectors 之类又要引 NuGet，
本工程一个包都不引）。但这些 lucide 图标全是"24×24 画布 + 纯描边 + 2px 线宽"，
几何形状就是一段 SVG path 的 `d` —— 而 WPF 的路径迷你语言跟 SVG 的 `d` 语法基本一样。
所以转出来的东西就是一段 `Geometry` 字符串，画的时候用 `Path` + `Stroke`，
**颜色跟着主题走**（`Stroke` 绑到行自己的 `Foreground`：悬停 / 选中 / 禁用自动变色）。

## ⚠️ 为什么不能直接把 `d` 抄进 XAML

Lucide 的 `d` 用的是 SVG 的**紧凑写法**：数字之间可以没有分隔符（`1.68-.474`、
`a1 1 0 0 0 ...`）。WPF 的解析器没这么宽容，遇到 `1.68-.474` 会直接报
"值不能为空/格式错误"，而且**报在 XAML 加载期**（窗口都起不来，堆栈里看不出是哪一段）。
所以这里做一遍正规的**分词 + 重新拼**：数字一律用空格隔开再输出。

## ⚠️ 元素类型要白名单

只认 `path` / `circle` / `polyline` / `line` / `rect` 五种（现用的图标只用到前三种）。
遇到不认识的就**抛出去**，不要静默跳过 —— 跳过的结果是那个图标少一块，
而界面上只是"看着有点怪"，没人会去查。
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "Mosslight.App" / "Themes" / "NavIcons.xaml"
DEFAULT_SOURCE = Path(r"D:\开发工程\PCL-CE-dev\PCL.Core\UI\Assets\IconPacks\lucide")

# (lucide 文件名, XAML 里那个 Geometry 资源的后缀)
#
# 映射关系**照 PCL2-CE 的导航栏**（`Plain Craft Launcher 2/Pages/PageDownload/PageDownloadLeft.xaml`
# 里每个 `MyIconTextButton` 的 `SvgIcon="lucide/xxx"`），这样从 PCL 过来的用户
# 看哪一行都眼熟 —— 这事儿没有"标准答案"，跟着一个用户量最大的启动器走最省心。
# 只有两处是我们自己的选择（PCL 那边没有对应行）：`quilt`（拼布 → 格子）和
# `labymod`（PCL 把它排在 Quilt 的位置用了 box）。
ICONS: tuple[tuple[str, str], ...] = (
    # ---- 主侧栏（启动 / 下载 / 个性化 / 设置）----
    ("play", "Play"),
    ("download", "Download"),
    ("palette", "Palette"),
    ("settings", "Settings"),
    # ---- 下载页左栏：版本 ----
    ("boxes", "Boxes"),
    # ---- 下载页左栏：社区资源 ----
    ("puzzle", "Puzzle"),
    ("package", "Package"),
    ("file-archive", "FileArchive"),
    ("layers", "Layers"),
    ("sparkles", "Sparkles"),
    ("globe", "Globe"),
    ("heart", "Heart"),
    # ---- 下载页左栏：安装包 ----
    ("gauge", "Gauge"),
    ("anvil", "Anvil"),
    ("cat", "Cat"),
    ("flask-conical", "FlaskConical"),
    ("scroll", "Scroll"),
    ("grid-2x2", "Grid2x2"),
    ("box", "Box"),
    ("egg", "Egg"),
)

SVG_NS = "{http://www.w3.org/2000/svg}"

NUMBER = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")
COMMANDS = set("MmLlHhVvCcSsQqTtAaZz")


def tokenize_path(d: str) -> list[str]:
    """把一段 SVG path 拆成 [命令, 数字, 数字, …]（数字统一成正则认出来的原文）"""
    tokens: list[str] = []
    i = 0
    while i < len(d):
        ch = d[i]
        if ch in COMMANDS:
            tokens.append(ch)
            i += 1
        elif ch in " \t\r\n,":
            i += 1
        else:
            match = NUMBER.match(d, i)
            if match is None:
                raise ValueError(f"看不懂的 path 片段：{d[i:i + 24]!r}")
            tokens.append(match.group())
            i = match.end()
    return tokens


ARG_COUNT = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


def expand_ops(d: str) -> list[tuple[str, list[float]]]:
    """`d` → [(命令, 参数)]，把"省略重复命令"那种写法展开成一条条

    SVG 允许 `M 1 2 3 4 5 6` 这种（后面几组坐标都按 moveto 的 **lineto** 变体算），
    所以展开时 `M` 之后的隐式重复要变成 `L`。不展开的话后面算当前点会错位。
    """
    tokens = tokenize_path(d)
    ops: list[tuple[str, list[float]]] = []
    i = 0
    command: str | None = None
    while i < len(tokens):
        token = tokens[i]
        if token in COMMANDS:
            command = token
            i += 1
            if command in "Zz":
                ops.append(("Z", []))
                command = None
            continue
        if command is None:
            raise ValueError(f"数字前面没有命令：{tokens[max(0, i - 3):i + 3]}")

        upper = command.upper()
        count = ARG_COUNT[upper]
        chunk = tokens[i:i + count]
        if len(chunk) < count or any(t in COMMANDS for t in chunk):
            raise ValueError(f"{command} 的参数不够：{tokens[max(0, i - 2):i + count + 2]}")
        ops.append((command, [float(t) for t in chunk]))
        i += count

        # 隐式重复：moveto 后面的坐标组是 lineto
        if upper == "M":
            command = "l" if command == "m" else "L"
    return ops


def absolutize(d: str) -> tuple[str, list[tuple[float, float]]]:
    """一段 path → **全绝对坐标**的 path（顺便把这几个点收集起来算包围盒）

    ## 为什么非要绝对化
    图标的 svg 里每个 `<path>` 是**独立的**，各自开头的小写 `m` 按 SVG 规范
    是相对 (0,0)（= 当绝对坐标用）。可是 WPF 的 `Geometry` 一条字符串只能有一个
    坐标流 —— 几段 path 一拼，第二段开头那个 `m 7 16.5` 就变成"相对上一段的终点"了，
    于是那几笔**飞到画布外面**（实测：Minecraft 下面飘出三道虚线、整合包旁边多一个 `﹀`）。
    转成绝对坐标之后，拼多少段都一样。

    ⚠️ 还有个坑：`Z` 之后当前点要回到**子路径起点**，不是回到上一笔的终点。
    """
    ops = expand_ops(d)
    out: list[str] = []
    points: list[tuple[float, float]] = []
    x = y = 0.0          # 当前点
    start_x = start_y = 0.0

    for index, (command, args) in enumerate(ops):
        upper = command.upper()
        relative = command.islower()
        if index == 0 and upper == "M":
            # 整段 path 的第一个 moveto：小写也按绝对算（SVG 规范明文）
            relative = False

        if upper == "Z":
            out.append("Z")
            x, y = start_x, start_y
            continue

        if upper == "H":
            target = args[0] + x if relative else args[0]
            out.append(f"L {fmt(target)} {fmt(y)}")
            points.append((target, y))
            x = target
            continue

        if upper == "V":
            target = args[0] + y if relative else args[0]
            out.append(f"L {fmt(x)} {fmt(target)}")
            points.append((x, target))
            y = target
            continue

        if upper == "A":
            # 弧：前五个参数（rx / ry / 旋转 / 大弧 / 方向）**不是坐标**，不跟着平移
            rx, ry, rotation, large_arc, sweep = args[:5]
            end_x, end_y = args[5], args[6]
            if relative:
                end_x += x
                end_y += y
            out.append(f"A {fmt(rx)} {fmt(ry)} {fmt(rotation)} "
                       f"{int(large_arc)} {int(sweep)} {fmt(end_x)} {fmt(end_y)}")
            points.append((end_x, end_y))
        else:
            # 其余命令：每一对数字都是坐标（控制点也算），相对命令要整体平移
            coords = list(args)
            if relative:
                coords = [v + (x if i % 2 == 0 else y) for i, v in enumerate(coords)]
            out.append(upper + " " + " ".join(fmt(v) for v in coords))
            for i in range(0, len(coords) - 1, 2):
                points.append((coords[i], coords[i + 1]))
            end_x, end_y = coords[-2], coords[-1]

        if upper == "M":
            start_x, start_y = end_x, end_y

        x, y = end_x, end_y

    return " ".join(out), points


def normalize_path(d: str) -> tuple[str, list[tuple[float, float]]]:
    """SVG 的 `d` → WPF 能认的绝对坐标写法（+ 这批点）

    ⚠️ 数字要**过一遍 fmt**（不能原样照抄）。SVG 允许省略前导零（`.474` / `-.474`），
       WPF 的路径解析器**不认**那种写法 —— 而它是在 XAML 加载期炸的，
       报错只说"某个值格式不对"，堆栈里看不出是哪段路径。
    """
    return absolutize(d)


def num(value: str | None, default: float = 0.0) -> float:
    return float(value) if value is not None else default


def fmt(value: float) -> str:
    """数字写法统一一下（去掉多余的零，别出现 1.5000000000000002 这种）"""
    text = f"{value:.5f}".rstrip("0").rstrip(".")
    return text if text not in ("", "-0") else "0"


def circle_path(cx: float, cy: float, r: float) -> str:
    """一个圆 → 两段弧（SVG 的 `<circle>` 在 WPF 路径里没有对应命令，只能自己拼）"""
    left, right, middle = fmt(cx - r), fmt(cx + r), fmt(cy)
    rr = fmt(r)
    return (f"M {left} {middle} A {rr} {rr} 0 1 0 {right} {middle} "
            f"A {rr} {rr} 0 1 0 {left} {middle}")


def rect_path(x: float, y: float, w: float, h: float, rx: float) -> str:
    """一个矩形（可能带圆角）→ 一段闭合路径"""
    if rx <= 0:
        return (f"M {fmt(x)} {fmt(y)} L {fmt(x + w)} {fmt(y)} "
                f"L {fmt(x + w)} {fmt(y + h)} L {fmt(x)} {fmt(y + h)} Z")
    return (f"M {fmt(x + rx)} {fmt(y)} L {fmt(x + w - rx)} {fmt(y)} "
            f"A {fmt(rx)} {fmt(rx)} 0 0 1 {fmt(x + w)} {fmt(y + rx)} "
            f"L {fmt(x + w)} {fmt(y + h - rx)} "
            f"A {fmt(rx)} {fmt(rx)} 0 0 1 {fmt(x + w - rx)} {fmt(y + h)} "
            f"L {fmt(x + rx)} {fmt(y + h)} "
            f"A {fmt(rx)} {fmt(rx)} 0 0 1 {fmt(x)} {fmt(y + h - rx)} "
            f"L {fmt(x)} {fmt(y + rx)} "
            f"A {fmt(rx)} {fmt(rx)} 0 0 1 {fmt(x + rx)} {fmt(y)} Z")


def element_path(element: ElementTree.Element) -> str:
    """一个 svg 子元素 → 一段路径（不认识的元素直接抛）"""
    tag = element.tag.replace(SVG_NS, "")
    if tag == "path":
        d = element.get("d")
        if not d:
            raise ValueError("<path> 没有 d")
        return normalize_path(d)
    if tag == "circle":
        cx, cy, r = num(element.get("cx")), num(element.get("cy")), num(element.get("r"))
        return circle_path(cx, cy, r), [(cx - r, cy), (cx + r, cy + r)]
    if tag == "polyline":
        points = [float(v) for v in re.findall(NUMBER, element.get("points", ""))]
        if len(points) < 4:
            raise ValueError(f"<polyline> 的点太少：{points}")
        pairs = " L ".join(
            f"{fmt(points[i])} {fmt(points[i + 1])}" for i in range(0, len(points) - 1, 2))
        return "M " + pairs, [(points[i], points[i + 1]) for i in range(0, len(points) - 1, 2)]
    if tag == "line":
        x1, y1 = num(element.get("x1")), num(element.get("y1"))
        x2, y2 = num(element.get("x2")), num(element.get("y2"))
        return (f"M {fmt(x1)} {fmt(y1)} L {fmt(x2)} {fmt(y2)}"), [(x1, y1), (x2, y2)]
    if tag == "rect":
        x, y = num(element.get("x")), num(element.get("y"))
        w, h = num(element.get("width")), num(element.get("height"))
        return rect_path(x, y, w, h, num(element.get("rx"), num(element.get("ry")))), \
            [(x, y), (x + w, y + h)]
    raise ValueError(f"不认识的 svg 元素：<{tag}>（要么补支持，要么换图标）")


def svg_to_geometry(path: Path) -> tuple[str, tuple[float, float, float, float]]:
    """一个 svg 文件 → (整段路径, 包围盒 ``(minx, miny, maxx, maxy)``)

    ⚠️ 包围盒是**校验用**的：图形一旦跑到 24×24 画布外面，说明坐标换算错了
       （实测过：几段 path 一拼、相对坐标没绝对化，碎片会飞到 y≈29）。
       那种错在界面上只是"图标旁边多了几笔"，不细看根本发现不了 —— 所以让它当场红。
    """
    root = ElementTree.parse(path).getroot()
    view_box = root.get("viewBox", "0 0 24 24")
    if view_box.split() != ["0", "0", "24", "24"]:
        # ⚠️ 不是 24×24 画布的话，路径坐标的比例就不对了（图标会大小不一）。
        #    现在这批全是 24×24，以后混进来的话要在这里补一个缩放矩阵。
        raise ValueError(f"{path.name} 的 viewBox 是 {view_box!r}，不是 0 0 24 24")

    parts: list[str] = []
    points: list[tuple[float, float]] = []
    for child in root:
        if not isinstance(child.tag, str):      # 注释节点
            continue
        text, pts = element_path(child)
        parts.append(text)
        points.extend(pts)
    if not parts or not points:
        raise ValueError(f"{path.name} 里没有图形元素")

    min_x = min(p[0] for p in points)
    min_y = min(p[1] for p in points)
    max_x = max(p[0] for p in points)
    max_y = max(p[1] for p in points)

    # 允许一点点出界（描边本身有半个线宽在外面），再多就是算错了
    if min_x < -1 or min_y < -1 or max_x > 25 or max_y > 25:
        raise ValueError(
            f"{path.name} 的图形跑到画布外面了：bbox = "
            f"({min_x:.2f}, {min_y:.2f}) - ({max_x:.2f}, {max_y:.2f})。"
            "多半是某段 path 的坐标没绝对化。")

    return " ".join(parts), (min_x, min_y, max_x, max_y)


def build(source: Path) -> tuple[str, list[tuple[str, tuple[float, float, float, float]]]]:
    lines = [
        '<!--',
        '  导航图标（矢量路径）。**这个文件是生成的，别手改** ——',
        '  改图标 / 改映射请改 `tools/gen_nav_icons.py` 再跑一遍：',
        '',
        '      python tools\\gen_nav_icons.py',
        '',
        '  图形数据来自 **Lucide**（https://lucide.dev ，ISC 许可），',
        '  生成时用的 svg 是 PCL2-CE 仓库里带的那份 `IconPacks/lucide/*.svg`',
        '  （用的是图标数据本身，不是 PCL2-CE 的代码 —— 那一层是 GPL-3.0）。',
        '',
        '  ⚠️ 这些图标是**描边**图形（24×24 画布、线宽 2）：画的时候用 `Path` +',
        '     `Stroke`，颜色跟着行的 `Foreground` 走（悬停 / 选中 / 禁用自动变色），',
        '     尺寸交给 `Viewbox` 缩（比例和 Lucide 自己在 16px 下画的一样）。',
        '     **别给它们设 Fill**：会变成一坨实心块。',
        '-->',
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
    ]

    missing: list[str] = []
    boxes: list[tuple[str, tuple[float, float, float, float]]] = []
    for name, key in ICONS:
        svg = source / f"{name}.svg"
        if not svg.is_file():
            missing.append(str(svg))
            continue
        geometry, bbox = svg_to_geometry(svg)
        lines.append(f'  <Geometry x:Key="Icon.{key}">{geometry}</Geometry>')
        boxes.append((name, bbox))

    lines.append('</ResourceDictionary>')
    lines.append('')

    if missing:
        raise SystemExit("缺这些 svg：\n  " + "\n  ".join(missing))

    return "\n".join(lines), boxes


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 Mosslight.App/Themes/NavIcons.xaml")
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE,
                        help=f"Lucide 的 svg 目录（默认 {DEFAULT_SOURCE}）")
    parser.add_argument("--check", action="store_true",
                        help="只检查生成结果和仓库里那份一不一样（CI 用），不写文件")
    args = parser.parse_args()

    if not args.source.is_dir():
        print(f"找不到图标目录：{args.source}")
        return 1

    text, boxes = build(args.source)

    # 每个图标的包围盒打出来：一眼能看出"有没有哪个图形的坐标跑偏了"
    # （24×24 的画布，正常的图标都在 1~23 之间）
    print("  图标包围盒（画布 0~24）：")
    for name, (min_x, min_y, max_x, max_y) in boxes:
        flag = "" if (max_x - min_x) >= 8 and (max_y - min_y) >= 8 else "  ⚠️ 太小了？"
        print(f"    {name:<14} ({min_x:5.2f}, {min_y:5.2f}) - ({max_x:5.2f}, {max_y:5.2f}){flag}")

    if args.check:
        current = OUT.read_text(encoding="utf-8") if OUT.is_file() else ""
        if current != text:
            print("NavIcons.xaml 跟图标源对不上，跑一遍 tools\\gen_nav_icons.py")
            return 1
        print(f"NavIcons.xaml 是最新的（{len(ICONS)} 个图标）")
        return 0

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(text, encoding="utf-8")
    print(f"写好 {OUT}（{len(ICONS)} 个图标，{len(text)} 字符）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
