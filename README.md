# Mosslight Launcher 2.0（C# / WPF）

这是 Mosslight Launcher 的 **2.0 重写版**，从 Python / PyQt6 换成了 C# / WPF。

> **界面风格照搬 1.0.0。**
> 不是重新设计，也不是"顺便改好看点"——配色、间距、圆角、文案都对着 1.0.0 的源码和截图逐条对。映射关系见 [`docs/风格对照.md`](docs/风格对照.md)。
>
> **核心功能（启动引擎）也是从 1.0.0 搬过来的**，而且是**逐值验过**的：拿 Python 版当金标准，同一个 fixture 喂给两边，连拼出来的命令行都逐项比。见下面「启动引擎的金标准怎么来的」。

1.0.0（权威风格源）：[Mosslight-Launcher](https://github.com/kongxia114/Mosslight-Launcher)

> ⚠️ 这个目录**取代**了原来放在同一个文件夹里的 VB.NET / WinForms 原型（`Mosslight-Launcher 2.0.vbproj`）。那套已经挪到上级的 `_legacy-vb\`，确认不需要就可以删。两套之所以不是"合并"而是"替换"：VB/WinForms 和 C#/WPF 是两套 UI 栈、两种语言，**合不到一个工程里**。

---

## 跑起来

```powershell
dotnet build "Mosslight-Launcher 2.0.slnx" -m:1

# 界面
.\Mosslight.App\bin\Debug\net10.0-windows\win-x64\Mosslight.exe

# 自检（颜色 / 配置纠正 / 多语言 / 动画预设与缓动曲线 / **入场动画轨迹** / **日志窗口后端** / **启动引擎**，逐值跟 Python 版比对）
.\Mosslight.Core.SelfCheck\bin\Debug\net10.0-windows\Mosslight.Core.SelfCheck.exe tests\theme_golden.json

# 自检 + 联网冒烟（真去 Mojang 拉清单、算一遍安装计划、真下一个文件校验 sha1）
.\Mosslight.Core.SelfCheck\bin\Debug\net10.0-windows\Mosslight.Core.SelfCheck.exe tests\theme_golden.json --net
```

### 启动引擎的金标准怎么来的

`tests/launch_golden.json` 不是手写的，是**拿 1.0.0 的 Python 版当权威跑出来的**：

```powershell
python tools\gen_launch_golden.py
```

它会在 `tests/fixtures/mc` 造假一个 `.minecraft`（刻意埋了 OptiFine 的补丁 jar、只有 natives 的库、坏掉的 version json、版本隔离的特征目录…），然后把**同一批文件**喂给 Python 版，把结果落成金标准；C# 自检再读同一批文件，逐项比——命令行的**每一项**都比，不是只看"能不能跑"。

⚠️ 改 `LaunchPlanBuilder` / `VersionScanner` 之前先跑这个脚本再跑自检。启动命令拼装是这个项目里最容易"看着对、其实错"的地方：rules 的"最后一条匹配生效"、`${version_type}` 的取值、老版本的 `-Djava.library.path`、log4j 配置的路径替换——每一条都是编译能过、跑起来也不报错、但游戏就是起不来（或者起来以后行为不对）的那种错。

### 入场动画的金标准怎么来的

`tests/slide_anim_golden.json` 同理，也是拿 1.0.0 跑出来的（脚本在开发机的实验目录里，不进仓库）：

```powershell
python D:\deepseek\实验\tools\gen_slide_golden.py     # 默认写到 tests\slide_anim_golden.json
```

它 import 的是**真的** `ui/widgets/slide_in.py`，用 `QAbstractAnimation.setCurrentTime()` 把真动画对象逐点定位到某一毫秒，读 `SlideInRow._slide_offset` 和孩子被 `move()` 到的坐标；C# 自检再拿 `SlideInTrajectory` 按同一组时刻算一遍，逐点比。

⚠️ 改 `SlideInTrajectory` / `AnimationCurves`（以及 `EasingFactory` 的曲线名解析）之前先跑这个脚本再跑自检。入场动画是典型的"编译能过、看着也顺、但形状根本不是那一回事"：弹簧档在搬过来之前只是换了时长和缓动，三段回弹和 PCL 双位移**根本没有实现** —— 只有逐点比轨迹才抓得住。

自动化截图（离屏渲染，**不弹窗口**，渲染完就退出，用于跟 1.0.0 的截图对比）：

```powershell
Mosslight.exe --page home        --screenshot D:\shots\launch.png
Mosslight.exe --page personalize --screenshot D:\shots\personalize.png
```

---

## 发版（GitHub Actions）

两个工作流，`.github/workflows/` 下：

| 文件 | 什么时候跑 | 干什么 |
|------|-----------|--------|
| `ci.yml` | push / PR / 手动 | 构建 + 自检 + 界面冒烟 + 试发布**一个**包。**不发 Release** |
| `release.yml` | 推 `v*` tag / 手动 | 上面那套 + 打**两个** zip + 建 Release |

发一个版本就是：

```powershell
git tag v2.0.0
git push origin v2.0.0
```

想重发、或者从分支直接发而不打 tag：**Actions → Release → Run workflow**，填版本号。两种触发走的是**同一套步骤**（刻意没拆成两个文件——拆开之后"改了一个忘了另一个"是迟早的事）。

### 发两个包（这是故意的）

产物是**两个 zip**，每个里都**只有一个文件** `Mosslight.exe`。两个包的差别**只有 `--self-contained` 这一个发布参数**：

| 包 | 发布参数 | 实测大小 | 给谁 |
|---|---|---|---|
| `Mosslight-2.0-<tag>-win64.zip` ← **主推** | `--self-contained false` | exe 2.0 MB / zip **1.3 MB** | 装过 **.NET 10 桌面运行时**的人 |
| `Mosslight-2.0-<tag>-win64-selfcontained.zip` | `--self-contained true` | exe 60.2 MB / zip **54.6 MB** | 不想装任何东西的人（零依赖） |

为什么两个都发：**"下载体积"和"零依赖"没法同时满足**。只发大包会让装过运行时的那部分人多下 58 MB；只发小包会把没装运行时的人挡在门外。与其替用户猜，不如两个都放在 Release 里，正文第一句就写"**不确定就下小的；打不开、提示要装 .NET 就下大的**"，并附上运行时的官方下载页 <https://dotnet.microsoft.com/download/dotnet/10.0>（要的是 **.NET Desktop Runtime 10.0** x64，不是 SDK）。

小包跑不起来时 Windows 自己会弹一个提示框，里面有官方的安装指引链接（.NET 的 apphost 自带这个行为）—— 所以"没装运行时"这个坑不会变成一句没有出路的话。
⚠️ **这一条没有实测过**：本机装了 .NET 10 桌面运行时（跑不了"没装"的情形），而这台机器上也不该去卸运行时来试。所以"弹框、框里有链接"是按 .NET apphost 的既有行为写的，不是量出来的。真要确认，得拿一台干净的 Windows 试。

发布流程里两个包走的是**同一段代码**（`发布` / `单文件产物冒烟` / `打 zip` 三步都是"一张表 + 循环 + 一个验证函数"），硬检查完全一致：顶层恰好 1 项 `Mosslight.exe`、没有 `Assets\`、没有散装 dll、没有 pdb。**大小门槛是两个包各自的**，见下面第 5 条。

### 五个容易踩的点（都写在 workflow 的注释里了）

1. **语言数据是内嵌资源，不是 exe 旁边的文件。** 5 个 `Assets/lang/*.json` 由 `Mosslight.Core.csproj` 用 `<EmbeddedResource>` 编进程序集（**不是** WPF 的 `<Resource>`——Core 是普通类库，没有 `<UseWPF>`），`Localizer` 走「① exe 旁边的 `Assets\lang\xx.json` 优先 → ② 回落到内嵌那份」两层加载。所以**两个包**的发布产物顶层都**恰好一个 exe**，发版流程里有一条硬检查盯着这一点（`Assets\` 出现就算回归）。用户想改翻译仍有口子：在 exe 旁边放一份 `Assets\lang\zh_CN.json` 盖住那几条即可，**是可选的**。
2. **版本号只有一处：仓库根 `Directory.Build.props` 的 `<Version>`。** 构建时它变成程序集的 `AssemblyInformationalVersion`（= exe 属性里的「产品版本」，`FileVersion` 是纯数字那份），界面上的版本号由 `AppInfo.AppVersion` **从程序集属性读**，所以"界面上写的"和"exe 属性里写的"不可能对不上。发版时 `dotnet publish` 带 `-p:Version=<tag 去掉 v>` + `-p:FileVersion=<纯数字前四段>` 覆盖它，**源码一个字都不用动**（2026-10 之前那一步"改 `AppInfo.cs` 再读回来断言"已经删掉了）。⚠️ 预发布后缀只能给 `Version`、不能给 `FileVersion`，理由见 `tools/check_version_norm.py`。
3. **`shell: pwsh` 不能省。** PowerShell 5.1 的 `>>` 写 GITHUB_OUTPUT 是 UTF-16，GitHub 按 UTF-8 读会**静默**解析不出任何 output。GitHub 的 runner 上 `pwsh` 就是 7，所以写 `shell: pwsh` 即可——别改成 `powershell`。
4. **发行说明（`RELEASE_BODY.md`）是"仓库里一份兜底 + 发版时现场覆盖"。** 仓库根目录那份是提交进版本库的通用说明（不写死版本号、大小只写"约"值），`body_path` 永远指向它——`softprops/action-gh-release` 读不到 `body_path` 是**直接报错**，不会回退到空正文，所以这个路径不能悬空。正常发版时"组装发行说明"那一步会把它覆盖成带两个 zip 名 / **现场量出来的真实大小** / compare 链接 / `CHANGELOG.md` 对应小节的正文。想加这一版的更新内容，就在 `CHANGELOG.md` 里开一个小节（版本号或「未发布」都行）。
5. **`EnableCompressionInSingleFile` 只能给大包。** 给小包（框架依赖）加上是 **`error NETSDK1176` 直接失败**："仅在发布独立应用程序时才支持在单个文件捆绑包中进行压缩"。所以这个参数是**按包给**的。同理，大小门槛也是**两个包各一条**：原来那条"zip 至少 10 MB"只对大包成立（小包实测 1.3 MB，拿 10 MB 卡它每次发版都会红）；小包是「下限 1 MB + **上限 10 MB**」，上限拦的是"小包被改成自带运行时、悄悄变成 55 MB 但名字还叫小包"。

### 推之前先在本机跑一遍

推上去才发现 YAML 里的 PowerShell 写错了，一轮就是几分钟 + 一次红色的提交。`tools/local_run_workflow.py` 把 workflow 在**仓库的一份临时副本**里真跑一遍（只跑 `run:` 步骤，`uses:` 跳过）：

```powershell
python tools\local_run_workflow.py .github\workflows\ci.yml
python tools\local_run_workflow.py .github\workflows\release.yml --tag v2.0.0-test
```

它模拟了 `GITHUB_OUTPUT` / `GITHUB_ENV` / `RUNNER_TEMP` 这些上下文，所以步骤之间传值传错也抓得住。**这个脚本当初就是这么抓出一个真 bug 的**：PowerShell 里逗号的优先级比 `+` 高，`'a' + $x + 'b', 'c', 'd'` 会被解析成 `'a' + $x + ('b','c','d')`，整段发行说明塌成一行——而且不报错。

⚠️ 本机只有 Windows PowerShell 5.1 时，脚本会退回复用它。语法错、变量名写错这些两边一样能抓；但 `Set-Content -Encoding UTF8` 在 5.1 是**带 BOM**、7 是不带，涉及编码的地方以 CI 为准。

⚠️ **`dotnet run` 在这台机器上会失败**（隐式还原撞上并行还原崩溃：`_GetAllRestoreProjectPathItems` 直接"生成失败"、还不给错误信息）。用上面的 `build` + 直接跑 exe，或者加 `-m:1` / `--no-restore`。同理，**改完源码请用 `-t:Rebuild`**——这台机器的 MSBuild 增量判定会误判成"已是最新"，跑出来的还是旧二进制。

### 环境上的两个坑

- **一个 NuGet 包都不能引**（`api.nuget.org` 在这台机器上不通）。所以自检是个普通控制台程序，不是 MSTest；JSON 用框架自带的 `System.Text.Json`。
- `dotnet` 默认往 `C:\Users\<用户>\.dotnet` 写东西，被沙箱挡住的话用环境变量绕：
  ```powershell
  $env:DOTNET_CLI_HOME="D:\deepseek\.dhml-tests\.tmp\dotnet_home"
  $env:NUGET_PACKAGES="D:\deepseek\.dhml-tests\.tmp\packages"
  ```

### ⚠️ 它跟 1.0.0 **共用同一份 config.json**

配置目录名两边都是 `%APPDATA%\Mosslight`，键名也是**逐字对齐**的（`theme` / `accent_color` / `bg_*` / `card_opacity` / `anim_*` …），所以：**2.0 会读到 1.0.0 的设置，在 2.0 里改设置也会写回那份文件。**

这是刻意的（同一套设置，两边看起来一致，用户不用重设一遍），而且两个版本都遵守"只动认识的键、不认识的键原样保留"，所以来回切不会丢东西。但要知道两件事：

1. **别同时开着两个版本改设置**——最后保存的那份赢。
2. 想完全隔离（比如截图、试配色），在 exe 旁边放一个空的 `portable.txt`，配置就会落在 exe 旁边而不是 `%APPDATA%`。`D:\deepseek\.dhml-tests\wpf_shots.py` 就是这么跑的（它把 exe 拷到临时目录再截图）。

---

## 工程结构

```
Mosslight.Core\            类库，**没有 <UseWPF>**
  Theme\                   调色板 + 颜色运算（逐字移植 core/theme.py）
  Settings\                config.json 的读写与类型纠正
  Localization\            i18n：Localizer（磁盘覆盖优先 → 内嵌回落）
    lang\*.json            5 种语言的数据，**<EmbeddedResource> 编进程序集**
  Animation\               入场动效的预设表 + 轨迹引擎（纯函数，不依赖 WPF）
  Appearance\              背景铺法那些常量
  Logging\                 日志那一层：级别 / 续行 / 噪音判定、log4j XML 还原、进程级日志总线
  Services\                数据模型 + 服务接口 + **真实现**（Real*Service.cs）
    Engine\                启动引擎（对应 1.0.0 的 core/*.py）
      LaunchRules.cs         rules / natives key / maven 坐标 / 离线 UUID / 参数拆分
      VersionJson.cs         version.json 的读取 + inheritsFrom 合并
      VersionScanner.cs      本地版本扫描（加载器识别、版本隔离、排序）
      LaunchPlanBuilder.cs   版本 JSON + 参数 → 完整命令行（**纯函数**）
      NativesExtractor.cs    解压原生库
      JavaScanner.cs         Java 探测（注册表 / 常见目录 / MC 自带运行时 / 缓存）
      SystemMemory.cs        系统内存查询与建议
      Branding.cs            `--versionType` 那个品牌串
      DownloadManager.cs     下载引擎（镜像 / 重试 / 熔断 / sha1 / 断点跳过）
      VersionManifest.cs     远程版本清单（拉取 / 缓存 / 分组）
      GameInstaller.cs       版本 JSON → 要下载哪些文件
      LoaderCatalog.cs       五家加载器的版本列表（解析 + 三态）
      LoaderInstaller.cs     Forge/NeoForge/OptiFine 的安装器：地址 / 跑 / 校验 / 改名
      VersionSettings.cs     按版本覆盖参数（versions.json）
      ModrinthApi.cs         模组数据层（搜索 / 项目 / 版本 / 筛选 / 分类与游戏版本的候选）
      ModsDirectory.cs       mod 该装到哪（版本隔离 / 反查版本文件夹）+ 本地 mod 增删启停
      GameRepair.cs          补全"JSON 说该有、本地却没有"的库
      ModpackInstaller.cs    整合包解析 / **路径安全** / 覆盖层展开
      ServiceFactory.cs      装配点（界面只认接口）
Mosslight.App\             WPF 界面
  Themes\                  主题字典（生成的）+ 控件样式表 + 外壳样式
  Infrastructure\          主题应用、背景画布、动效、标记扩展
  Views\                   页面 + 对话框（含独立日志窗口 LogWindow）
Mosslight.Core.SelfCheck\  控制台自检（相当于测试工程）
tools\gen_launch_golden.py 用 Python 版生成启动引擎的金标准
docs\风格对照.md            1.0.0 → 2.0 的映射表
```

### 几条约定（不是风格洁癖，是编译期能兜住的）

1. **`Mosslight.Core` 不引 WPF。** 1.0.0 那边靠"约定"保证 `core/` 不 import PyQt6，这里直接用 `.csproj` 里的 `<UseWPF>` 缺省来**让编译器兜住**——想在 Core 里 `using System.Windows` 会直接编译不过。
2. **颜色只在主题里。** XAML 里不许出现 `#rrggbb`——写死了浅色主题就漏掉那条。用 `{DynamicResource BgCardBrush}` 这种键。⚠️ 必须是 `DynamicResource`：`StaticResource` 在换主题时不会更新。
3. **文案都过 i18n。** XAML 里 `Text="{loc:Tr 启动游戏}"`，代码里 `Localizer.Tr("…")`。key 就是**中文源文案**（1.0.0 的取舍）——漏翻只会退回中文，永远不会把 key 糊到用户脸上。
4. **主题色是生成的**，别手改 `Theme.Dark.xaml` / `Theme.Light.xaml` / `ThemeData.g.cs`，改 `core/theme.py` 再跑 `gen_theme_xaml.py`。
5. **注释写"为什么"。** 尤其是那些"看着可以简化、但简化了就会错"的地方（比如卡片为什么不能复用 `BgCardBrush`、Python 的 `round()` 为什么是银行家舍入）。

---

## 现在的状态

**界面**：窗口外壳、启动页、个性化页、主题切换、自定义强调色、背景图五种铺法、卡片透明度、入场动效（7 档风格 × 5 档速度 + 总开关，**参数逐档调过手感**）、**独立日志窗口**（分级过滤 / 噪音过滤 / 导出 / 结束进程，接真实游戏 stdout），5 种界面语言。

**启动引擎**（1.0.0 的 `core/`，两万多行的主体）**已经搬过来了**，`Mosslight.Core.Services` 里挂的是 `Real*Service.cs`，不再是 `Stubs.cs` 的假数据：

| 功能 | 对应 1.0.0 | 状态 |
|------|-----------|------|
| 版本扫描（加载器识别 / 版本隔离 / 完整性 / 排序） | `core/versions.py` | ✅ |
| 启动命令拼装（rules / 占位符 / natives / log4j） | `core/launch.py` | ✅ |
| Java 探测（注册表 + 常见目录 + MC 自带运行时 + 缓存） | `core/java.py` | ✅ |
| 离线账户（uuid / accounts.json） | `core/accounts.py` | ✅ |
| natives 解压 | `core/natives.py` | ✅ |
| 内存查询与建议 | `core/memory.py` | ✅ |
| 下载引擎（镜像 / 重试 / 熔断 / sha1） | `core/download.py` | ✅ |
| 版本清单 + 原版安装 | `core/versions_remote.py` + `install.py` | ✅ |
| `--versionType` 品牌串 | `core/branding.py` | ✅ |
| 加载器版本列表（五家，三态） | `core/loaders.py` | ✅ |
| 加载器安装：Fabric / Quilt（profile JSON，不用 Java） | `core/loader_install.py` | ✅ |
| 加载器安装：Forge / NeoForge / OptiFine（跑安装器） | `core/loader_setup.py` | ✅ |
| 按版本覆盖参数（内存 / Java / JVM 参数 / 自动进服） | `core/version_settings.py` | ✅ |
| Modrinth 数据层（搜索 / 项目 / 版本 / 筛选 / 分类与游戏版本的候选） | `core/modrinth_api.py` | ✅ |
| 补全缺失的库（只补"JSON 说该有、本地却没有"的） | `core/repair.py` | ✅ |
| mod 该装到哪（版本隔离 / 按游戏版本+加载器反查版本文件夹） | `core/mods.py` | ✅ |
| 本地 mod 增删启停 + 从 Modrinth 装（**2.0 补的**，1.0.0 只有下载） | — | ✅ |
| 整合包安装 `.mrpack`（含 **Zip Slip 防护** / 覆盖层顺序） | `core/modpack.py` | ✅ |

验证方式不是"能编译"，是**拿 1.0.0 的 Python 版当金标准逐值比**：`tests/launch_golden.json` 里连命令行、加载器解析、安装器地址、mod 目录反查、整合包解析都逐项存着，自检 **739 项全过**（联网那几十项要 `--net`）。

**还没搬的**：游戏目录管理（`core/mc_dir.py` 剩下那部分）。

### 界面进度

| 页面 | 状态 |
|------|------|
| 启动 | ✅ 已接真引擎 |
| 个性化 | ✅ |
| 账户 | ✅ 新建 / 双击切换 / 删除离线档案 |
| 设置 | ✅ 游戏目录 / Java / 内存联动 / 下载线程 / 启动选项 |
| 版本管理 | ✅ 三段分组 + 筛选 + 版本详情 + 补全文件 + 换游戏目录 |
| 版本设置 | ✅ 内存 / Java / JVM 参数 / 游戏参数 / 自定义信息 / 自动进服（**三态**：跟随默认 or 自己定） |
| 模组 | ✅ 搜索卡（关键词 + 游戏版本 / 加载器 / 分类 / 排序 四个下拉 + 搜索 / 重置）/ 结果卡（**滚到底自动续取下一页**）/ 项目详情页（**全部版本按「游戏版本 × 加载器 × 正式/预览」分组**，匹配的置顶展开，**滚到底自动续铺**；点某一行就下载，行尾不再挂按钮；**点下载先问「装到哪儿？」**——选已装版本或自选文件夹，= PCL 那种先选目录再下载）/ 安装 / 已装模组的启用·禁用·删除 |
| 社区资源五栏 | ✅ 模组 / 整合包 / 资源包 / 光影包 各一页（同一套页面，按类型换 `project_type` 和文案）；数据包是占位页（Modrinth 没有它、要指定存放，见那页说明） |
| 本地整合包 | ✅ **把 .zip / .mrpack 拖到窗口任意位置就能装**（PCL 那种手感；卡片平时隐藏，拖进来才出现）：自动认 Modrinth `.mrpack`、CurseForge 的 zip、没有清单的覆盖包（`PackArchive`）。CurseForge 的 mod 要按 projectID 换下载地址，这一步还没接，界面上如实说明 |
| 下载进度窗口 | ✅ 装原版 / 装加载器 / 下模组 / 装整合包都会弹（左边总进度·速度·剩余文件，右边**每个文件一行**，底部保存路径 + 取消下载）。数据走 `DownloadMonitor`（引擎每次快照顺手广播一份，窗口 200ms 轮询），离屏截图入口是 `--page downloadwindow` |
| 下载 | ✅ 原版（**只列"能下到的"**：正式版 / 快照 / 远古版本；本地已装的在「版本」页）/ 加载器（五家）/ 整合包 三个分段 |

社区资源那五栏共用 `ModsPage`：差异摆在一张表里（`ModContentTypes.cs` 的 `ModContentType`），
加第六栏只动那张表。⚠️ 那个搜索卡的四个下拉**不是所有栏都有**：加载器只有模组 / 整合包有
（资源包 / 光影包在 Modrinth 上没有这一维，塞 `categories:fabric` 会静默返回 0 条），
分类则按项目类型各一份（拿模组的分类去筛资源包同样会静默 0 条）。候选值问接口要
（`/v2/tag/category`、`/v2/tag/game_version`），拿不到就退回 `ModFilters` 里那张核对过的兜底表。

### 导航图标

左侧导航每一行前面那个小图标是**矢量**的（`Path` + `Stroke`，跟着行的 `Foreground` 变色，
悬停 / 选中 / 禁用都自动一致），图形数据在 `Themes\NavIcons.xaml` ——
**那是生成物，别手改**：改图标或改映射请改 `tools\gen_nav_icons.py` 再跑一遍。

```powershell
python tools\gen_nav_icons.py            # 重新生成 NavIcons.xaml
python tools\gen_nav_icons.py --check    # 只比对仓库里那份是不是最新（要能读到图标源目录）
```

图形数据来自 **Lucide**（ISC 许可）。用的是**图标数据本身**，不是 PCL2-CE 的代码
（那一层是 GPL-3.0）。哪个 key 配哪个图标是照 PCL2 的导航栏选的；
「为什么不能直接把 SVG 的 `d` 抄进 XAML」也写在生成器的文档注释里。
「关于」对话框的「开源许可」里同样列了这一条。

### 自检怎么跑

```powershell
$exe = '.\Mosslight.Core.SelfCheck\bin\Release\net10.0-windows\Mosslight.Core.SelfCheck.exe'

& $exe tests\theme_golden.json              # 离线：873 项
& $exe tests\theme_golden.json --net        # + 联网：发 HEAD、拉清单、真下一个文件（936 项）
& $exe tests\theme_golden.json --net-forge  # + 真下 8.4 MB 安装器、真跑一次 Forge 安装器
```

`--net-forge` 单独一个开关是因为它重得多（下 8.4 MB、跑 33 秒）。它会验：安装器下得下来 → `IsInstallerJar` 认得出来 → 读得出它自己要的 36 个库 → 命令行拼得对 → 进程真跑起来 → **输出节流生效**（实测 34110 行只喂给界面 471 行）→ 多出一个 `1.20.1-forge-47.4.26` → 通过「装完校验」→ 带 `inheritsFrom` → **扫版本列表看得见**。

⚠️ **加页面时记得把新页加进冒烟清单** —— 现在它只有**一处**：两个 workflow 顶部的 `env.SMOKE_PAGES`（`ci.yml` / `release.yml` 各一份，改完记得同步；`python tools\check_workflows.py` 会当场告诉你两份一不一样）。

那个门禁是真抓过东西的：账户页外层 Grid 少了一行声明导致页脚叠在标题上、设置页的 `Slider.ValueChanged` 在 XAML 解析期就触发——两个都是"编译 0 警告、跑起来才炸"，而且都不在首页上。清单里现在有 35 项，含 `logwindow`（独立日志窗口）、`settings:aboutdialog`、`personalize:anim`、`download:mods`、`mods:detail` 这些"点出来才看得到"的东西。

⚠️ 顺手也跑一下 `python tools\check_workflows.py`：它会把两个 workflow 的每个 `run:` 块丢给 PowerShell 自己的解析器过一遍语法，并检查两份 `SMOKE_PAGES` 是否还一致。

分段页面用 `页面:子段` 那种写法（`--page download:loader`）——离屏截图只会停在默认那一段，别的段的 XAML 就永远没人验过。

**正版（微软）登录暂时做不了**：Azure 应用注册是免费的，但 Minecraft Services 会返回 `403 Invalid app registration`，`XboxLive.signin` 需要加入 Xbox / ID@Xbox 开发者计划。所以 2.0 先只做离线账户，界面上也如实禁用那一项。