# 设计说明（从源码注释里搬出来的）

> 这里的内容**原来写在 .cs 的注释里**。2026-10 按用户要求搬出来：
> 源码只留一行 `设计说明见 docs/design-notes.md#<锚点>`，细节（为什么这么写、
> 踩过什么坑、用户当时的原话）都放在这边。
>
> 留在源码里的是**即时陷阱警告**（⚠️ 那类）和 API 契约（`<param>` 之类）——
> 那些离开现场就没用了。

## 目录

- [`Mosslight.App/App.xaml.cs`](#mosslight-app-app-xaml-cs)
- [`Mosslight.App/Infrastructure/BackgroundHost.cs`](#mosslight-app-infrastructure-backgroundhost-cs)
- [`Mosslight.App/Infrastructure/EasingFactory.cs`](#mosslight-app-infrastructure-easingfactory-cs)
- [`Mosslight.App/Infrastructure/ModIcons.cs`](#mosslight-app-infrastructure-modicons-cs)
- [`Mosslight.App/Infrastructure/PageAnimator.cs`](#mosslight-app-infrastructure-pageanimator-cs)
- [`Mosslight.App/Infrastructure/ParametricEase.cs`](#mosslight-app-infrastructure-parametricease-cs)
- [`Mosslight.App/Infrastructure/PlayerAvatars.cs`](#mosslight-app-infrastructure-playeravatars-cs)
- [`Mosslight.App/Infrastructure/ScrollLoader.cs`](#mosslight-app-infrastructure-scrollloader-cs)
- [`Mosslight.App/MainWindow.xaml.cs`](#mosslight-app-mainwindow-xaml-cs)
- [`Mosslight.App/Views/AboutDialog.xaml.cs`](#mosslight-app-views-aboutdialog-xaml-cs)
- [`Mosslight.App/Views/AccountsPage.xaml.cs`](#mosslight-app-views-accountspage-xaml-cs)
- [`Mosslight.App/Views/AnimSettingsDialog.xaml.cs`](#mosslight-app-views-animsettingsdialog-xaml-cs)
- [`Mosslight.App/Views/BackgroundSettingsDialog.xaml.cs`](#mosslight-app-views-backgroundsettingsdialog-xaml-cs)
- [`Mosslight.App/Views/CollapsibleGroup.xaml.cs`](#mosslight-app-views-collapsiblegroup-xaml-cs)
- [`Mosslight.App/Views/DownloadPage.xaml.cs`](#mosslight-app-views-downloadpage-xaml-cs)
- [`Mosslight.App/Views/DownloadWindow.xaml.cs`](#mosslight-app-views-downloadwindow-xaml-cs)
- [`Mosslight.App/Views/LaunchPage.xaml.cs`](#mosslight-app-views-launchpage-xaml-cs)
- [`Mosslight.App/Views/LoadingBar.xaml.cs`](#mosslight-app-views-loadingbar-xaml-cs)
- [`Mosslight.App/Views/LogWindow.xaml.cs`](#mosslight-app-views-logwindow-xaml-cs)
- [`Mosslight.App/Views/MemoryBar.cs`](#mosslight-app-views-memorybar-cs)
- [`Mosslight.App/Views/ModFilters.cs`](#mosslight-app-views-modfilters-cs)
- [`Mosslight.App/Views/ModsPage.xaml.cs`](#mosslight-app-views-modspage-xaml-cs)
- [`Mosslight.App/Views/PersonalizePage.xaml.cs`](#mosslight-app-views-personalizepage-xaml-cs)
- [`Mosslight.App/Views/SettingsPage.xaml.cs`](#mosslight-app-views-settingspage-xaml-cs)
- [`Mosslight.App/Views/VersionSettingsPage.xaml.cs`](#mosslight-app-views-versionsettingspage-xaml-cs)
- [`Mosslight.App/Views/VersionsPage.xaml.cs`](#mosslight-app-views-versionspage-xaml-cs)
- [`Mosslight.Core/Animation/AnimationCurves.cs`](#mosslight-core-animation-animationcurves-cs)
- [`Mosslight.Core/Animation/AnimationPresets.cs`](#mosslight-core-animation-animationpresets-cs)
- [`Mosslight.Core/Animation/AnimationRebound.cs`](#mosslight-core-animation-animationrebound-cs)
- [`Mosslight.Core/Animation/SlideInTrajectory.cs`](#mosslight-core-animation-slideintrajectory-cs)
- [`Mosslight.Core/AppInfo.cs`](#mosslight-core-appinfo-cs)
- [`Mosslight.Core/Appearance/BackgroundModes.cs`](#mosslight-core-appearance-backgroundmodes-cs)
- [`Mosslight.Core/Localization/Localizer.cs`](#mosslight-core-localization-localizer-cs)
- [`Mosslight.Core/Logging/LogLine.cs`](#mosslight-core-logging-logline-cs)
- [`Mosslight.Core/Services/Engine/Branding.cs`](#mosslight-core-services-engine-branding-cs)
- [`Mosslight.Core/Services/Engine/CleanroomCatalog.cs`](#mosslight-core-services-engine-cleanroomcatalog-cs)
- [`Mosslight.Core/Services/Engine/DownloadManager.cs`](#mosslight-core-services-engine-downloadmanager-cs)
- [`Mosslight.Core/Services/Engine/DownloadSourceMode.cs`](#mosslight-core-services-engine-downloadsourcemode-cs)
- [`Mosslight.Core/Services/Engine/GameInstaller.cs`](#mosslight-core-services-engine-gameinstaller-cs)
- [`Mosslight.Core/Services/Engine/GameLocales.cs`](#mosslight-core-services-engine-gamelocales-cs)
- [`Mosslight.Core/Services/Engine/JavaScanner.cs`](#mosslight-core-services-engine-javascanner-cs)
- [`Mosslight.Core/Services/Engine/JsonIO.cs`](#mosslight-core-services-engine-jsonio-cs)
- [`Mosslight.Core/Services/Engine/LabyModCatalog.cs`](#mosslight-core-services-engine-labymodcatalog-cs)
- [`Mosslight.Core/Services/Engine/LaunchPlanBuilder.cs`](#mosslight-core-services-engine-launchplanbuilder-cs)
- [`Mosslight.Core/Services/Engine/LiteLoaderCatalog.cs`](#mosslight-core-services-engine-liteloadercatalog-cs)
- [`Mosslight.Core/Services/Engine/LoaderCatalog.cs`](#mosslight-core-services-engine-loadercatalog-cs)
- [`Mosslight.Core/Services/Engine/LoaderCompat.cs`](#mosslight-core-services-engine-loadercompat-cs)
- [`Mosslight.Core/Services/Engine/LoaderInstaller.cs`](#mosslight-core-services-engine-loaderinstaller-cs)
- [`Mosslight.Core/Services/Engine/ModMetadata.cs`](#mosslight-core-services-engine-modmetadata-cs)
- [`Mosslight.Core/Services/Engine/ModVersionGroups.cs`](#mosslight-core-services-engine-modversiongroups-cs)
- [`Mosslight.Core/Services/Engine/ModrinthApi.cs`](#mosslight-core-services-engine-modrinthapi-cs)
- [`Mosslight.Core/Services/Engine/VersionJson.cs`](#mosslight-core-services-engine-versionjson-cs)
- [`Mosslight.Core/Services/Engine/VersionManifest.cs`](#mosslight-core-services-engine-versionmanifest-cs)
- [`Mosslight.Core/Services/Engine/VersionScanner.cs`](#mosslight-core-services-engine-versionscanner-cs)
- [`Mosslight.Core/Services/Models.cs`](#mosslight-core-services-models-cs)
- [`Mosslight.Core/Services/RealJavaService.cs`](#mosslight-core-services-realjavaservice-cs)
- [`Mosslight.Core/Services/RealLaunchService.cs`](#mosslight-core-services-reallaunchservice-cs)
- [`Mosslight.Core/Services/RealLoaderService.cs`](#mosslight-core-services-realloaderservice-cs)
- [`Mosslight.Core/Services/RealModpackService.cs`](#mosslight-core-services-realmodpackservice-cs)
- [`Mosslight.Core/Services/RealVersionSettingsService.cs`](#mosslight-core-services-realversionsettingsservice-cs)

## Mosslight.App/App.xaml.cs

<a id="mosslight-app-app-xaml-cs"></a>

```text
`--pack 文件`：跟"把整合包拖到窗口上"走**同一条路**（见 MainWindow.OpenLocalPack）。
拖放是鼠标动作，离屏截图点不了 —— 不给入口的话那张卡就只有人肉开程序才看得见。
   排在前面的话会被后面的 `--page home` 一把掀掉（实测过：截出来还是首页）。
```

```text
<summary>
等界面画稳定了再截图然后退出。

⚠️ 两个坑，都是实测踩出来的：
  1. **不能用 `Thread.Sleep`** —— 那会把渲染也一起停掉，截出来是白板。
     要用 `DispatcherTimer`（消息泵照常转，动画和布局继续走）。
  2. **要等入场动效铺完**。列表页的卡片是错峰滑入的（默认 420ms + 95ms×n），
     刚 Show 完就截会截到"标题半透明、卡片还没出现"的中途状态。
     所以默认等 <see cref="DefaultSettleMs"/>，也可以用 `--settle &lt;毫秒&gt;` 调。
     （当前「启动」「个性化」两页本身没有入场动画，但版本/模组列表马上就会有，
     这个等待是为它们准备的，不是白等。）
</summary>
```

## Mosslight.App/Infrastructure/BackgroundHost.cs

<a id="mosslight-app-infrastructure-backgroundhost-cs"></a>

```text
<summary>
浅色主题下蒙层强度的折算系数（见 <see cref="Apply"/> 里那段注释）。
白蒙层比黑蒙层刺眼得多，1:1 用会把图推成一片发白 —— 实测 0.5 才接近
"深色主题 90 档"那种强度。
</summary>
```

```text
有图 → 底下铺页面色（给"适应/居中"的留白用）；没图 → 用纯色，纯色也没设就用页面色

   `(Brush)Application.Current.Resources["BgPageBrush"]`：
   后者拿到的是**当时那个刷子对象**，而换深浅主题时 ThemeApplier 是
   `resources["BgPageBrush"] = new SolidColorBrush(...)` —— 换了个新对象。
   抓死的引用不会跟着变，表现就是：切到浅色之后**只有内容区还是深色那层底**
   （实测残留 #1B1C1F），而侧栏 / 标题栏 / 卡片都变了（它们用的是
   DynamicResource）。2026-10 用户报的"浅色背景是黑的"就是它。
   SetResourceReference 绑的是**资源键**，替换后自动刷新。
```

```text
   写死黑色的话，浅色主题 + 一张偏亮的图 = 深色文字压在图上，
   又花又难读（2026-10 用户报的）。
   这里用**页面色**当蒙层：深色主题下它等同于"压暗"，
   浅色主题下自动变成"提亮"。

   白蒙层把整张图推向页面色，观感是"发白、亮得晃眼"（用户 2026-10 原话
   "改过后更亮瞎眼了"）。实测浅色 dim=90 会把内容区平均色从 #839594
   推到 #ABB6B7（+40），对"文字能读"这个目标来说远远过头。
   所以浅色主题按 0.5 折算（90 → 45，约 18%），深色主题保持 1:1 ——
   那个档位是 1.0.0 一直用的，手感已经定型。
```

## Mosslight.App/Infrastructure/EasingFactory.cs

<a id="mosslight-app-infrastructure-easingfactory-cs"></a>

```text
<summary>
缓动曲线名 → WPF 的缓动函数。

Python 那份（`core/anim_prefs.py` 的 EASE_* 常量 + `ui/widgets/pcl_ease.py`）里，
曲线名是**字符串**，因为 Qt 只认它自带的几条，参数化的过冲得自己写。

C# 这边分两路：
  · `cubic` / `inout` —— 语义跟 WPF 内置的**逐值一致**（OutCubic = 1-(1-t)³、
    InOutCubic 的分段也一样），直接用内置的，少一个类；
  · 其余全部走 <see cref="ParametricEase"/> 里那些类 —— 它们只是外壳，
    公式统一在 Core 的 <see cref="AnimationCurves"/> 里（**和轨迹引擎同一份实现**）。

## 名字的解析规则 = `slide_in._ease_curve()`
支持 `cubic` / `quint` / `inout`、带参数的 `back:0.18` / `spring:0.14` / `power:5`、
以及不带参数的 `back` / `elastic` / `spring`。认不出来**退回 OutCubic，绝不抛** ——
预设表是跟着 Python 走的，那边加一条新曲线名时这里应该退化成普通缓动，
而不是让整个界面在动画开始时崩掉。

⚠️ 两处跟 Python 对齐的细节（以前 C# 这边是错的）：
  1. `"back:"` / `"back:abc"` 这种**参数写坏了**的，Python 那边头名会变成带冒号的整串、
     一个分支都匹配不上 → 退回 OutCubic。以前这里会当成"`back` 用默认值"处理。
  2. 名字**不转小写**：`"BACK:0.18"` 在 Python 那边认不出来 → OutCubic。
</summary>
```

```text
`EASE_QUINT` 在 Python 那边走 Qt 内置 OutQuint = 1-(1-t)^5。

   0.5 处只有 0.94921875"。本轮实测把这个说法推翻了：
   WPF 的 QuinticEase（EaseOut）**就是** 1-(1-t)^5，逐点差 1e-16
   （自检里有这条断言，拿它当哨兵）。所以换成自实现是**行为等价**的重构，
   换的理由不是"原来算错了"，而是"公式只留一份"：Core 的
   AnimationCurves.PowerOut 同时喂给轨迹引擎（PCL 那条快轨用的也是 OutQuint），
   两边共用一份实现才不会再出现"曲线的注释跟实际不符"这种事。
```

```text
过冲曲线：**必须**用自己实现的 ParametricBackEase。
WPF 的 BackEase.Amplitude 不是过冲比例、只是公式系数（Amplitude=1
实测过冲才约 1.7%），表达不出 Python 的"过冲 18%"。
参数是过冲比例，照传；不带参数时 Python 用的是 0.10（不是预设表里那个 0.18）
```

## Mosslight.App/Infrastructure/ModIcons.cs

<a id="mosslight-app-infrastructure-modicons-cs"></a>

```text
<summary>
这个项目类型在图没下来时该用哪张内置图。

⚠️ 名字必须对上 `Assets\icons\version\` 里真实存在的文件 ——
   拼错了 <see cref="VersionIcons.Load"/> 会返回 null，卡片上就是一个空洞。
   映射按用户 2026-10 指定的那批：模组→铁砧、整合包→箱子、
   资源包→草径、光影→红石灯；数据包用户没点名，用红石块（同一批方块图里
   最中性的一个，不像"地图"那样会让人以为点进去看世界）。
</summary>
```

## Mosslight.App/Infrastructure/PageAnimator.cs

<a id="mosslight-app-infrastructure-pageanimator-cs"></a>

```text
   理由不是"截图不好看"，是那边根本播不出来：截图模式窗口从不 Show，
   没有 PresentationSource、没有消息泵，WPF 的动画时钟不会 tick
   （实测：`--settle 400` 截动效预览，三张示意卡片停在起手状态 ——
     半透明 + 偏移，看上去就是"空的"）。挂着动画的元素会**永远停在起点**，
   截出来的列表就是一片空白。动效对话框那边是靠"跑完主动 Reset 一次"绕过去的，
   页面级没有那个收尾点，所以在这里统一挡住。
   真实运行时（窗口 Show 出来）ScreenshotMode 是 false，动画照常。
```

```text
   `OffsetPx` 的语义是"起点离终点多远"（见 AnimationPresets 的注释）：
   水平 = 起点在终点的**左边**、垂直 = 起点在终点的**下边**。
   WPF 的 TranslateTransform.X 正数往右，所以水平那一档要写成 -offset，
   否则「左侧滑入」会变成从右边滑进来 —— 跟预设名字、跟 1.0.0 的
   `SlideInRow._apply()`（那边是 `child.move(-offset, 0)`）正好相反。
   换算统一走 SlideInTrajectory.ToCoordinate，别在这儿再抄一遍正负号。
```

## Mosslight.App/Infrastructure/ParametricEase.cs

<a id="mosslight-app-infrastructure-parametricease-cs"></a>

```text
<summary>
「Python 的缓出曲线」→ WPF 缓动函数的适配基类。

## ⚠️ 为什么不能直接把公式塞进 EaseInCore

WPF 的 <see cref="EasingFunctionBase.Ease"/> 会**按 EasingMode 再加工一次**
<see cref="EasingFunctionBase.EaseInCore"/> 的返回值，而我们的场景全是 EaseOut：

    Ease(t) = 1 - EaseInCore(1 - t)

所以若把 Python 的缓出公式 f 直接写进 EaseInCore，实际播放出来的会是
`1 - f(1-t)` —— 曲线被**左右镜像再上下翻转**，形状完全变了。这种错编译能过、
跑起来也"挺顺"，但方向是反的，只有逐值比对才抓得住。

因此这里统一做一次**反演**：子类照抄 Python 的 f（缓出），基类负责把它变成
EaseInCore(u) = 1 - f(1-u)。这样 `Ease(t)` 出来的数就跟 Python 一模一样，
自检里比的就是同一件事。

（自检里有一条断言专门盯着这个约定：拿**内置** CubicEase 实测
`Ease(t) == 1-(1-t)^3`，也就是确认 WPF 确实做了上面那次翻转。）

## 公式住在哪

**全部公式都在 <see cref="AnimationCurves"/>（Mosslight.Core）里**，这里的类只做
"包一层 WPF 外壳"。这么分是因为同一批公式还有第二个消费者：Core 的
<see cref="SlideInTrajectory"/>（回弹要分段、PCL 要两条叠加，塞不进一条缓动曲线）。
两边共用一份实现，才不会出现"缓动曲线对一个样、轨迹采样对另一个样"。

## 参数

参数都在构造函数里给，对象是**不可变**的（<see cref="EasingFunctionBase"/> 本身
是 Freezable，但这里没有依赖属性，随便冻结都行）。曲线对象每次动画现造，
不共享 —— 见 EasingFactory 的说明。
</summary>
```

```text
<summary>
参数化 Back（过冲）曲线 —— 对应 `pcl_ease.make_back_out(s)`。

    f(t) = u·u·((s+1)·u + s) + 1,   u = t - 1

## 为什么不用 WPF 自带的 <see cref="BackEase"/>

WPF 的 Amplitude **不是过冲比例**，只是曲线公式里的一个系数：
`Amplitude = 1` 时实测过冲只有约 1.7%，而 Python 那边 `back:0.18` 要的是
**实打实过冲 18%**。两者对不上，所以这一条必须自己实现。

`s` 也不是写死的：Python 用 `s_for_overshoot()` **二分 40 次**反推
（峰值位置要解超越方程，没解析解）。C# 这边照抄同样的反推过程，
否则会从小数点后第 5 位开始跟 Python 分叉 —— 那是逐点比对能抓到、肉眼绝对看不出的差别。
参考值：overshoot = 0.18 → s ≈ 2.424242（WPF BackEase 的等效 Amplitude 是 1.0）。
</summary>
```

```text
<summary>
阻尼正弦（弹簧）曲线 —— 对应 `pcl_ease.make_damped_sine(overshoot_ratio, cycles, decay)`。

    t_peak = 1/(2π·cycles) · atan(2π·cycles / decay)
    peak   = e^(-decay·t_peak) · sin(2π·cycles·t_peak)
    scale  = overshoot_ratio / peak                （把过冲归一化到给定比例）
    shaped = 1 - (1-t)^2.2                         （前段用幂次加速起步）
    f(t)   = 1 - scale · e^(-decay·shaped) · sin(2π·cycles·shaped)

参数 `cycles = 1.15` / `decay = 5.0` 就是 `slide_in.py` 的
`SPRING_CYCLES` / `SPRING_DECAY`，照抄。

⚠️ 两个容易搞混的地方：
  1. `slide_in.py` 里那条弹簧**动画**用的是同一个包络，但形状是 0→0 的 bump
     （`-scale·e^(-λt)·sin(...)`，t=0 时已就位、往负方向冲出去再收敛回 0）。
     这里要的是**缓动曲线**，是 0→1 的那条。别把两者对调
     （0→0 的那条在 <see cref="SlideInTrajectory.SpringBump"/>，轨迹引擎用它）。
  2. `overshoot_ratio = 0.14` **不等于**最终过冲 14%：`scale` 是按"**未加 shaped
     整形前**的包络峰值"归一化的，而实际曲线走的是 shaped 后的时间轴，
     实测峰值过冲只有约 1.6%。这是 1.0.0 的原样行为（不是这次移植的误差），
     C# 这边照抄，自检里也把这个峰值记下来了。
</summary>
```

```text
<summary>
幂次缓出曲线 —— 对应 `pcl_ease.make_power_out(power)`：f(t) = 1 - (1-t)^p。

## 为什么需要它（以及一个被实测推翻的旧说法）
`core/anim_prefs.py` 里的 `EASE_QUINT` 走的是 **Qt 内置的 `OutQuint`**，
而 Qt 的 OutQuint **就是** `1-(1-t)^5`。

⚠️ 工程里原来说"WPF 的 `QuinticEase` 不是这条公式，0.5 处只有 0.94921875"——
   **实测是错的**：WPF 的 `QuinticEase`（EaseOut）逐点都等于 `1-(1-t)^5`，
   最大偏差 1e-16（自检里有一条断言专门盯着这件事）。所以本类**不是**在修一个数值错误，
   它的价值是"公式只留一份"：PCL 那条"位移 A"（`_track(a0, 0, fast_ms, OutQuint)`）
   和 `EASE_QUINT` 用的是同一条公式，现在都出自
   <see cref="AnimationCurves.PowerOut"/>，不会再出现"两处各写一遍、注释还对不上"。
</summary>
```

## Mosslight.App/Infrastructure/PlayerAvatars.cs

<a id="mosslight-app-infrastructure-playeravatars-cs"></a>

```text
<summary>
把皮肤里的脸抠出来放大成头像。

   倍数取整（8 → 40 是 5 倍），所以不会出现"有的像素宽有的像素窄"。
   半径直接给边长的一半 = 正圆，跟外壳那两个 32px 圆形的头像位对得上。
   （1.0.0 那边是 0.28 的圆角、装在 QSS 的圆形容器里，四角会露出来一点。）

   原先两边都按物理像素算（125% 的屏上画 40×40、位图也 40×40），
   于是内容被裁掉右下角（40 逻辑像素的内容要塞进 40 像素的 120 DPI 位图，
   那张位图实际只装得下 32 逻辑像素 → 少了五分之一），放大到 40 物理像素显示，
   看着就是**又糊又缺一块**（实测截图确认过）。
   现在画布按 `pixels/dpr` 逻辑像素画、位图按 `pixels` 物理像素存，
   拿到的图逻辑尺寸永远是 32（跟容器一致），每物理像素都对得上、不会缩糊。

📌 这台开发机就是 125% 的屏（`GetDpi` 读出来 1.25，虽然
   非 DPI 感知进程的 `GetDpiForSystem` 会报 96），所以 dpr≠1 这条路是常态、
   不是边角情况。
</summary>
⚠️ 用**最近邻**放大：8×8 是像素画，平滑放大只会糊成一团。
⚠️ 圆角**在画的时候裁**（容器是 32px 的圆，`Border` 不会裁剪子元素）：
⚠️ <b>画布坐标是「逻辑像素」，位图尺寸是「物理像素」</b>，两者差一个 dpr。
```

```text
③ 整数倍**最近邻展开**：在内存里复制像素，一个源像素摊成 scale×scale 的同色块。

      那条路对 **RenderTargetBitmap.Render** 不生效 —— 实测 32×32 的头像里
      水平相邻像素完全相同的只有 19%（真正最近邻应该是 ~75%），也就是
      被双线性插值糊成一片了（用户 2026-10：头像看着是模糊的）。
      自己复制像素就完全不经过 WPF 的采样器，结果确定、与 DPI 无关。
```

```text
4. 按 UUID 挑默认那 9 张。
   （`LaunchRules.OfflineUuid`），别在这儿再抄一遍 MD5，抄了就会跟游戏对不上。
```

## Mosslight.App/Infrastructure/ScrollLoader.cs

<a id="mosslight-app-infrastructure-scrollloader-cs"></a>

```text
<summary>
"滑到底就续取下一页"那个判断（详情页的版本分组 + 搜索结果列表共用）。

## 为什么单独抽出来
它是纯算术，但**错了不会报错**：阈值算反了表现成"永远不加载"、
少了"视口还没量出来"那一条表现成"一打开就把 400 多组全铺出来"（实测踩过）。
抽成一个纯函数，自检就能把几种边界直接钉住；界面那边只剩"挂事件 + 调它"。
（公开也是为这个：自检项目直接引用 App，要能调到它才钉得住。）

## 视口高度是 0 的时候为什么**不能**加载
详情页刚建出来、或者面板还没显示时，`ScrollViewer.ViewportHeight` 是 0。
这时候"内容 ≤ 视口"恒成立，按补满一屏的逻辑就会一路铺到最后一组 ——
那正是"打开详情页卡一下"的原因。
</summary>
```

```text
<summary>
离底部还有这么远（逻辑像素）就开始取下一页。

⚠️ **故意留得很小**（2026-10-18 用户反馈"滑到底加载有点暴躁"）：
   原来给的是 160，等于还剩一个半分组就开加载 —— 用户只是滚了一下
   想看看下面有什么，列表就自己长出来了，看着像界面在抢操作。
   现在压到 **64**（差不多一个折叠标题的高度）：**真滑到底了**才续。
   宁可让用户多滚半屏，也不要在没到底的时候自己动。
</summary>
```

## Mosslight.App/MainWindow.xaml.cs

<a id="mosslight-app-mainwindow-xaml-cs"></a>

```text
<summary>
导航项：(页面 key, 图标, 中文文案)。

图标全用**几何符号**而不是 emoji：emoji 在缺字体的机器上会变成豆腐块，
这几个符号在 Segoe UI Symbol 里一定有（1.0.0 的注释里专门写了这条）。

⚠️ 「版本」「账户」都不在导航里：启动面板上的「选择版本」和底部那张档案卡片
   就是入口，同一件事不留两个门（用户 2026-09 明确要求去掉）。
</summary>
```

```text
品牌名不再拼 `\u26cf`（镐那个 emoji）。图标本身在 XAML 里（BrandHost），
因为它必须用 `{DynamicResource AccentBrush}` 才能在换强调色时跟着变 ——
代码里 `FindResource` 拿到的是当时那个 brush 对象，换色后不会更新。
```

```text
<summary>
右上角冒一条提示，几秒后自己淡出。

为什么不用页面里那条 `NoticeBanner`：那一块是**排在布局里**的，
一出现就把下面的内容顶下去；而且得有人负责收掉，忘了收就一直挂着
（用户 2026-10：收藏的提示怎么直接丢页里了？）。
这里三层都是浮的：`ToastHost` 不参与布局 → 不顶页面；自动淡出 → 不用收。

⚠️ 动画跟着设置的「入场动画」总开关走：关掉就**直接出现 + 直接消失**，
   不是"时长为 0"（那会闪一下）。
</summary>
```

```text
图标是矢量（见 NavIcons）：跟文字一起变色，选中态不用另做一套。
   靠的是等宽字形；换成真图标之后靠 StackPanel 排，别再退回空格。
```

## Mosslight.App/Views/AboutDialog.xaml.cs

<a id="mosslight-app-views-aboutdialog-xaml-cs"></a>

```text
<summary>
「关于」对话框。从设置页的「关于」卡片里弹出来。

移植自 1.0.0 的 `ui/dialogs/about_dialog.py` + `ui/widgets/about_content.py`。

## 跟 1.0.0 的差别（都是**内容**上的，不是版式上的）
  1. 「开源许可」那一栏：1.0.0 列的是 PyQt6 / Python / PyInstaller（GPL 那一家）。
     2.0 是 WPF + .NET，**一个第三方包都没引**（见 Mosslight.App.csproj 的说明），
     所以这一栏只有 .NET / WPF 一行，整体许可也从 GPL-3.0 换成 MIT。
     照抄 PyQt6 那一行才是错的 —— 那会对着用户说假话。
  2. 「查看源代码」按钮：仓库地址跟 1.0.0 用同一个（用户 2026-09 定的）。
     没填地址时**禁用**按钮并在提示里说明，而不是留个死链（1.0.0 的处理方式）。
</summary>
```

## Mosslight.App/Views/AccountsPage.xaml.cs

<a id="mosslight-app-views-accountspage-xaml-cs"></a>

```text
   1.0.0 的教训：连了选中变化就写盘，刷新时会重复触发。
   切换是双击的事。
```

## Mosslight.App/Views/AnimSettingsDialog.xaml.cs

<a id="mosslight-app-views-animsettingsdialog-xaml-cs"></a>

```text
<summary>
左边那列预设。⚠️ 用**代码**建而不是 XAML 里写死七项：色块要从当前主题的调色板取
（<see cref="PresetColorToken"/>），写死色值的话浅色主题和自定义强调色都不会跟着走
（1.0.0 的 `_swatch` / `_preset_color` 就是为这个）。

⚠️ 顺序**照 <see cref="AnimationPresets.PresetOrder"/> 走**（那是 Core 给界面用的唯一
   一份顺序表，别在这儿再抄一份）。已知它跟 1.0.0 的 `PRESET_ORDER` 有**一处出入**：
   Python 那边第 5 档是 `slide_left_back`、第 6 档是 `slide_up_elastic`，
   Core 这边两个写反了（自检里那条"顺序跟 Python 一致"的期望值是照着 C# 手抄的，
   所以两边一起错、全绿也抓不住）。按"不动 Mosslight.Core"的约定这里不就地纠正 ——
   要改的话应该连 Core 的 PresetOrder 和自检的期望值一起改。
</summary>
```

```text
---------- 详情第二段：这一轮新加的参数（同样**全是算出来的**） ----------

为什么这一句是**拼的**而不是走 Localizer 的共用 key：1.0.0 那份
`_update_detail` 只有上面那一句（它没有"过冲多少 / 淡入多久"这些概念，
过冲判据还写死成 `style == ANIM_BOUNCE`，七档里永远显示"无回弹"）。
这一轮加了 OvershootPx 的实测值和淡入时机，中文原文直接写在下面，
英文另给一条 key；zh_TW / ja_JP / ru_RU 没有这条 key 时**原样退回中文**
（Localizer 的行为）—— 数字本身是通用的，比硬塞一条机翻更像人话。
```

## Mosslight.App/Views/BackgroundSettingsDialog.xaml.cs

<a id="mosslight-app-views-backgroundsettingsdialog-xaml-cs"></a>

```text
   1.0.0（Qt）那条是 `图片 (*.png *.jpg);;所有文件 (*)` —— 语义一样、语法不同。
   照抄过来会在 `new OpenFileDialog { Filter = ... }` 直接抛 ArgumentException，
   而且是**构造时**抛：程序当场崩，连"点浏览没反应"都看不到（2026-10 用户踩到）。
```

```text
<summary>
选颜色：弹一个自己的 RGB 取色层（三个滑块，拖着实时生效）。

⚠️ 不用系统取色器：WPF **没有**内置的，`Microsoft.Win32` 里只有文件对话框；
   而 `System.Windows.Forms.ColorDialog` 要求 csproj 开 `<UseWindowsForms>`，
   那会把 `System.Windows.Forms` 灌进**全项目的隐式 using** ——
   `Application` / `UserControl` / `Panel` / `Brush` / `IDataObject`
   在几十个文件里立刻变成二义（实测一开 build 就红 10 条）。
   与其为了一个取色器去全项目加限定名，不如自带一个 —— 零依赖，
   而且拖着就能看到背景变，比系统那个点确定的更好用。
</summary>
```

```text
<summary>
让主窗口把背景重画一遍。
⚠️ 走 <c>Owner</c> 而不是 `Window.GetWindow(this)` —— 对话框自己就是顶层窗口，
   后者返回的永远是它自己（1.0.0 那份注释里踩过同样的坑）。
</summary>
```

## Mosslight.App/Views/CollapsibleGroup.xaml.cs

<a id="mosslight-app-views-collapsiblegroup-xaml-cs"></a>

```text
<summary>
这一组的**完整数据**（不是"已经建出来的行"）。

⚠️ 需要"换个缩放重画"的地方（见 `DownloadPage.OnLoadedOnce`）必须用它，
   **不能**用 <see cref="Rows"/> 反推：行是**懒建**的，一次只建一页
   （<see cref="PageSize"/> = 60），没展开过的组一行都没有 ——
   拿 Rows 做 `SetItems(...)` 等于把"103 条"缩成"60 条"、把"755 条"清成"0 条"。
   2026-10 用户报的"下载页首屏只剩 60 个、点刷新才全出来"就是这么回事。

⚠️ 返回的是**快照**，不是内部的 `_items`：调用方常把它直接喂回
   <see cref="SetItems"/>，而那里会先清空内部列表。给快照最省心。
</summary>
```

```text
<summary>
把标题栏整个藏起来，只留行（= 一列普通列表）。

什么时候要：mod 详情页照 PCL 的排版铺版本（2026-10 用户要求）——
那边是**一列版本行**，行上面没有"分组标题"这种东西。行还是懒建的
（一页 60 行 + 底部「加载更多」），所以只是把标题藏掉，分页逻辑不动。
</summary>
```

```text
<summary>
按 id 找一条数据（**不用它已经被建出来**）。

⚠️ 不能用 `Rows` 找：行是懒建的（`SetExpanded` 之后才建第一页），
   在"远古版本"那种默认收起的分组里，`Rows` 是空的 ——
   拿它找 `1.8` 永远找不到，于是 `--mc 1.8` 会静默退回最新正式版
   （2026-10-18 实测踩到）。
</summary>
```

## Mosslight.App/Views/DownloadPage.xaml.cs

<a id="mosslight-app-views-downloadpage-xaml-cs"></a>

```text
<summary>
「安装包」那一段列哪几家加载器。顺序 = 引擎那张表（`LoaderCatalog.LoaderOrder`）。

⚠️ **别在这张表里加引擎不认识的 key**：左栏会出现一个点进去"取不到版本列表"
   的假入口（而其实是"我们没这个接口"）。两边的 key 集合在自检里对着
   （见 `CheckLoaderKeyParity`），加一家要两处一起加。
⚠️ 文案是**专有名词**，不翻译（Forge / NeoForge / Cleanroom / Legacy Fabric …）。
</summary>
```

```text
<summary>
左栏那张表。「安装包」那一段**由 <see cref="LoaderOrder"/> 生成**：
以前是手抄一遍，加一家加载器就得记着改两处（漏改的那次就是
左栏有 LiteLoader、点进去却是"这一栏还没做"）。
</summary>
```

```text
   （版本 → 社区资源 → 收藏夹 → 安装包），用户的肌肉记忆已经是这个了，
   别为了"让它更显眼"往上挪 —— 2026-10 挪过一次，当场被指出来。
```

```text
<summary>
三个折叠组的顺序（正式版在最上面）。

⚠️ 以前这里还有第四组「本地版本」（本地装着、但官方清单里没有的那些：
   Forge / Fabric / 整合包实例）。2026-10 按用户要求**去掉了** ——
   那属于「版本」页的活，「我已经有什么」不该出现在"我要下什么"的页面上。
</summary>
```

```text
「安装包」里那几家加载器：点左栏直接进**这家自己的安装页**，并把那一家展开选中。
（以前这些 key 全掉到占位页 —— 用户说的"只下载文件、不走安装流程"就是
  走到这儿看见一句"这一栏还没做"。后来改成复用原版的"游戏安装"页，
  用户 2026-10 又说："加载器要有自己的安装页（标题就是 Fabric、先选游戏版本、
  只列 Fabric 的版本）" —— 所以现在是 OpenLoaderPageAsync。）
```

```text
   我原来写的是 `ModCardTitle` / `SecondaryButton`，这两个 key **不存在**，
   `FindResource` 拿到的是"未解析的引用"，强转成 Style 就抛
   「Unable to cast MS.Internal.NamedObject to System.Windows.Style」
   —— 进收藏夹那一栏直接崩（2026-10-18 用户点到就报）。
```

```text
<summary>
「社区资源 → 模组 / 资源包 / 光影包」：挂共用页（`ModsPage`）的**对应类型那一份**。

⚠️ 2026-10 之前这里只挂模组那一栏，理由是"那一页的搜索写死了 projectType=mod，
   给光影包挂上搜出来的还是 mod，那是在骗人"。现在 `ModsPage` 认类型参数了
   （搜索带对应的 `project_type` facet、标题/提示/落点都跟着走），
   所以三栏都能挂了 —— 当时的顾虑已经不成立。
</summary>
```

```text
   最后一次刷新就发生在任务完成之前（回调里刷的），所以界面会一直停在
   "正在获取版本…"上（实测，Quilt 那页就是这样）。用一个显式的"拉过了"标记。
```

```text
<summary>
自动拼出来的名字撞上已装版本时，往后加 `-2` / `-3`。

⚠️ 为什么加这个：用户 2026-10 的情形是"26.3-Fabric_0.20.0 早就装过了"，
   点进安装包 → Fabric 直接红字 + 按钮变灰，看着就像"点不动"。
   PCL 那边是让人自己改名，但那也得先知道该改成什么。
⚠️ **只对自动拼的名字生效**：用户手改过的名字照旧按规则校验，绝不偷偷改。
</summary>
```

```text
<summary>
名字栏左边那个图标多大（逻辑像素）。

⚠️ 跟右边那个输入框**看起来一样高**就行：输入框是 14px 字 + `Padding=10,6` +
   1px 边框，实测整行约 34 高，图标给 32 时视觉上正好齐平。
   （用户 2026-10："这个图标显示其实能高大一些的但是不确定大小" ——
   先按这个数定死，以后觉得小了改这一个常量就够，XAML 里那个也一起改。）
</summary>
```

```text
<summary>
名字栏左边那个图标 —— 跟着**当前选中的东西**变（用户 2026-10："然后自动显示图标？"）。

· 选了加载器 → 用那家的图标（Forge 铁砧 / NeoForge 狐狸 / Quilt 拼布 / LabyMod…）；
· 只选了 OptiFine → OptiFine 的图标；
· 什么都没选 → 原版那个方块（发布版草地 / 快照命令方块 / 远古圆石）。

⚠️ 图标名走 `VersionIcons.NameFor(loader: key)`，认不出来的加载器会退成空串 ——
   那时候就用原版的图标，**不要**留一块空白（空图标比错的图标更难看）。
⚠️ 尺寸按 DPI 重画：位图是按物理像素生成的，缩放变了必须重新 Load
   （见 `VersionIcons.Load` 的注释，1280×820 那套截图对比靠它）。
</summary>
```

```text
<summary>
一家的版本行 —— **排法跟模组详情页一样**（用户 2026-10 拿模组页的截图提的）：
标题是 `[26.3] Fabric 0.20.0`（`[游戏版本] 加载器 版本号`），第二行小字是
「正式版 / 测试版 · 发布日期（有才写）· 安装器文件名（有才写）」。

⚠️ 文件名只给**真的要下安装器**的那几家（Forge / NeoForge / OptiFine / Cleanroom）：
   Fabric / Quilt / Legacy Fabric 走 profile JSON，没有安装器文件；
   LiteLoader 数据里的 `file` 字段跟服务器上的真名字对不上（见 LiteLoaderCatalog），
   写上去是错的。宁可不写。
⚠️ 日期只有这家的接口给了才有（LiteLoader / LabyMod 有，Fabric 那几家没有）——
   没有就不写，别编一个。
</summary>
```

```text
有的接口给的是 ISO 原文（Forge：`2026-10-16T13:44:10.000Z`），
有的已经是 `2026-09-15 11:23`（LiteLoader）—— 统一过一遍格式化器
```

```text
   单独建的（存在 `_fabricApiGroup`）。所以这里不能直接 `_loaderGroups[key]` ——
   点它那行的叉会抛 KeyNotFoundException（2026-10-18 用户点到：报的就是这个）。
```

```text
<summary>
「安装包 → Fabric / Forge / …」：进**这一家自己的安装页**。

用户 2026-10 的原话："加载器要有自己的安装页（标题就是 Fabric、先选游戏版本、
只列 Fabric 的版本）"。之前这里是把原版那套"游戏安装"页整个复用过来、
底下多挂五家加载器行 —— 界面上连一句 Fabric 都看不见，看着就像
"只是多下了一个 jar"，所以现在单独一页：
  · 页头写这一家的名字（图标 + 名字 + 一句说明）；
  · 顶上是**游戏版本**选择器，默认挑这家支持的最新正式版
    （见 `LoaderCompat.DefaultGameVersion`：Cleanroom 只有 1.12.2，
     不这么挑的话一进去就是空的）；
  · 下面**只列这一家**的版本（别家的行由 `RefreshInstallRows` 收起来）；
  · 原版没装会先补上原版，**装过的不会重下**（`RealLoaderService.EnsureVanillaAsync`
    看 `versions/&lt;mc&gt;/&lt;mc&gt;.json` 在不在）—— 也就是"只下加载器那几个 jar"。
</summary>
```

```text
<summary>
一个游戏版本的分组：标题就是版本号（`26.3`），**展开才去拉**这一家的版本列表。

这就是用户说的"一列游戏版本的标签展开后就是 mod 发布的版本"——
加载器接口是按游戏版本查的，所以只能展开哪一组拉哪一组（顺手缓存）。
</summary>
```

```text
<summary>
一组里的版本行：`[26.3] Fabric 0.20.0` + `release / snapshot · 日期 · 安装器文件名`。

⚠️ 类型标签**不翻译**（用户 2026-10："测试版的名字不挂中文直接写 snapshot"）：
   这里就写 `release` / `snapshot`，不进语言文件。
</summary>
```

```text
<summary>
主窗口切到本页时调。

原来这里是"把游戏版本下拉和加载器列表备好"；重做之后**没有那个下拉了**
（版本从版本列表里点），所以这里只负责把清单先拉起来 ——
用户切过来的时候列表已经在路上了。
</summary>
```

```text
<summary>
这一组的**完整数据**。

⚠️ 以前这里写的是 `group.Rows.Select(r => r.Item)` —— Rows 只包含**已经建出来的行**
   （懒建，一次一页 60 行），于是 <see cref="OnLoadedOnce"/> 那次"按新 DPI 重建"
   会把 103 条的正式版缩成 60 条、把从未展开过的快照/远古清成 0 条，
   首屏就变成"只剩 60 个版本、点刷新才对"（2026-10 用户报的那次）。
   重建要看的是数据，不是屏幕上已经摊开的那几行。
</summary>
```

## Mosslight.App/Views/DownloadWindow.xaml.cs

<a id="mosslight-app-views-downloadwindow-xaml-cs"></a>

```text

   装整合包是**多批**下载：包里的文件 → **原版**（几百上千个库和资源）→ 加载器。
   每一批都是独立的 `DownloadManager`，跑完各自 `Finished = true`。
   定时器一停，后面那批就再也没人刷新窗口 —— 用户看到进度条卡在 100%、
   剩 0 个文件，可机器还在下 686 MB（2026-10-18 用户报的
   「下载游戏文件没走下载窗口」就是这个）。

   定时器只在 `Finish()`（整件事结束）和窗口关闭时停。
   空转的代价很小：`Row.Refresh` 有"值没变就不动控件"的短路，
   `SyncRows` 只是比一遍任务表。
```

## Mosslight.App/Views/LaunchPage.xaml.cs

<a id="mosslight-app-views-launchpage-xaml-cs"></a>

```text
扫描告警条：真引擎会把"哪几个版本读不了"记在 ScanErrors 里
（1.0.0 的 _update_banner）。以前这里没数据源，现在有了就接上。
```

```text
<summary>
启动按钮上那两行：`▶    启动游戏` + 版本名。对应 1.0.0 的 `_apply_launch_text()`。

⚠️ 2026-10 按用户要求**极致简化**（照 PCL2 的主面板）：面板上只剩这一个按钮
   + 「选择版本 / 版本设置」，原来那三行（版本名、Java/内存/加载器、当前档案）
   全删了。理由（用户提的）：按钮副标题**已经是版本名了**，下面再写一遍纯重复；
   账号在左侧栏底部有同一份；Java 数量在设置 → Java 页有完整列表。
   ⚠️ 信息没丢：原来那三行的内容现在挂在**按钮的 ToolTip** 上
      （版本名 / Java · 内存 · 加载器 · 隔离 / 版本目录）——
      "一眼看不到"不等于"没了"，留着给"这版到底用的哪个 Java"这种追问。
   ⚠️ 按钮里**不放图标**（用户明确说"纯按钮，logo 不做进去"）。
</summary>
```

```text
<summary>
空闲时的状态行。

⚠️ 2026-10 面板简化（照 PCL2）之后，"检测到 N 个可用的 Java"**不再常驻**：
   那个数字看一眼没有任何可做的事（要改去设置 → Java），却常年占着面板一行。
   现在只有**一个 Java 都没找到**时才说话 —— 那时候它是有用的（说明为什么启动不了）。
</summary>
```

```text
---- 那个"死开关"接活的地方 ----

设置页的「启动游戏后自动打开日志窗口」写的是 `show_log_window`
（1.0.0 core/config.py 的 DEFAULT_CONFIG 键名，默认 true）。
2.0 之前这个开关**存了没人读** —— 用户勾上、写了盘、然后什么都不发生。

   这几行之后游戏输出才开始进来；反过来的话开窗那一刻
   会先漏掉进程启动的头几行（"游戏已启动"这类最该看到的）。
```

```text
   镜像进 LogBus 了。这里再自己 LogBus.Write 一次的话，
   窗口里每一行游戏输出都会出现两遍（踩过）。
```

## Mosslight.App/Views/LoadingBar.xaml.cs

<a id="mosslight-app-views-loadingbar-xaml-cs"></a>

```text
<summary>
可复用的加载状态条（「正在获取版本列表…」+ 来回跑的不确定进度条）。

移植自 1.0.0 的 `ui/widgets/loading_bar.py`，连两个"踩过坑才有的"细节一起搬：

## 一、最少显示 180ms（<see cref="MinVisibleMs"/>）
网络快的时候（命中缓存 / 本地网络好）请求可能 40ms 就回来了。一来一回就立刻
收起的话，加载条会**闪一下**再消失，比不显示还难看。所以 <see cref="HideWithDelay"/>
会保证至少显示这么久再收。

## 二、显示状态**自己记**，不看 <c>Visibility</c>
父页面还没被切过来（或者窗口还没 Show）时，子控件的 `IsVisible` 永远是 false。
1.0.0 那边就因为这个撞过 bug：`hide_now()` 以为"本来就没显示"直接 return，
显示标记留着，等用户真翻到这一页看到的是**一条永远转下去的加载条**。
所以这里用 <see cref="_shown"/> 记，<see cref="IsLoading"/> 也看它。

## 用法
<code>
Loading.ShowState(Localizer.Tr("正在获取版本列表…"));
try { ... await 网络 ... }
finally { Loading.HideWithDelay(); }   // 失败也要收：错误信息有告警条负责说
</code>
⚠️ 一定要放 finally：漏一条分支就会留一条永远转下去的条。
</summary>
```

```text
<summary>
收起：高度动到 0（<see cref="HideAnimMs"/>），时长一到就 Collapsed。

⚠️ **状态切换挂在定时器（<see cref="_hideDoneTimer"/>）上，不挂动画的 Completed**
   —— 这是实测踩出来的：最初用 `animation.Completed` 收尾，结果它在这个控件上
   **根本没触发**（加载条做完搜索就永远挂在页面上，截图里看得清清楚楚）。
   高度动画只负责"好看"，"该收起来了"这件事必须有一个一定会发生的信号；
   定时器的间隔就是动画时长，视觉上完全对得上。
</summary>
```

## Mosslight.App/Views/LogWindow.xaml.cs

<a id="mosslight-app-views-logwindow-xaml-cs"></a>

```text
<summary>
独立的游戏日志窗口。移植自 1.0.0 的 <c>ui/dialogs/log_window.py</c>。

## 它跟 1.0.0 那份的关系
布局、交互、"点计数就把那一级藏起来"这些**来历清楚**的设计照抄了；
内容来源换成了 2.0 的 <see cref="LogBus"/> —— 那边是"首页持有窗口、
`append_log()` 直接往里写"，这边是"日志是进程级的、窗口只是订阅者"。
差别在效果上的好处：窗口**后开也有历史**，而且下载页 / 版本页发生的事也在里面。

## 三处**有意**跟 1.0.0 不一样的地方
  1. **正文用 RichTextBox 而不是 TextBox**。1.0.0 是 QPlainTextEdit +
     `QTextCharFormat` 一段段上色（QSS 管不到单独几行）。WPF 里对等的做法是
     <see cref="FlowDocument"/> + 每行一个 <see cref="Run"/> 带 Foreground。
  2. **多了一个「过滤噪音」开关**。1.0.0 正式版压根没有噪音过滤
     （那份 `LOG_NOISE_PATTERNS` 只在实验版里，见 <see cref="LogLine.NoisePatterns"/>），
     所以这里做成了可关的 —— 宁可让用户自己关，也不要替他决定滤掉什么。
  3. **「结束游戏进程」在没有游戏在跑时是禁用的**。1.0.0 那个按钮永远能点，
     点了只是发一个信号（那边首页收到再判断）。这边直接看 `IsRunning`，
     点不动的按钮比"点了没反应"诚实。

## 性能上做过的三件事（都是实测踩出来的）
  · 同一级别的**连续行合并成一次写入**（<see cref="Rebuild"/>）：日志是一串一串来的，
    这样几万行也就几十毫秒；点一下过滤开关就整篇重建，省得维护增量状态。
  · **流式追加**（<see cref="Append"/>）而不是"每来一行重建一次"：重建要
    清空整个 FlowDocument，游戏跑起来一秒几十行，那样会闪。
  · 超过行数上限时**从头删段落**（<see cref="Trim"/>）：FlowDocument 删中间的块很慢，
    删最前面那段是 O(1) 的（它就是个链表）。

## 主题
每行的级别颜色是写进 <see cref="Run.Foreground"/> 的，DynamicResource 管不到 ——
所以窗口订阅了 <see cref="ThemeApplier"/> 那个换主题事件，换主题时把缓存扔掉重画
（1.0.0 的 `refresh_theme()` 同样的道理）。
</summary>
```

```text
<summary>
关窗：**退订**所有事件。

⚠️ 不退订的话 <see cref="LogBus"/> 会一直攥着这个窗口 ——
   它是静态的，订阅者活到进程结束，表现就是"关掉的窗口还在后台吃内存、
   而且每次开一个新窗口就多一份"。

⚠️ 每个成员都要判空/判可用：**构造期抛异常时 WPF 照样会走 OnClosed**
   （Application.DoShutdown 那条路），那一刻字段还是 null、
   <c>App.Services</c> 可能也没建起来 —— 不判的话"开窗口失败"
   会被第二个 NullReferenceException 盖掉，真正的原因就查不出来了（踩过）。
</summary>
```

## Mosslight.App/Views/MemoryBar.cs

<a id="mosslight-app-views-memorybar-cs"></a>

```text
   手拼的版本踩过一个大坑：上下两条直边如果也用 ArcTo 连接，
   端点间距（≈宽度）远大于给定的半径（= 高度/2）—— 按 SVG 的圆弧规则，
   半径会被**自动放大**到能连上，于是画出一个半径几百像素的巨大鼓包，
   把整页都盖住了（截图里表现为卡片右上一大片被"吃掉"）。
   RectangleGeometry 直接吃 RadiusX/RadiusY，没有这个歧义。
```

## Mosslight.App/Views/ModFilters.cs

<a id="mosslight-app-views-modfilters-cs"></a>

```text
<summary>
游戏版本的兜底表（= 1.0.0 的 `MC_VERSIONS`，当时核对过的那 23 个）。

只在 <see cref="ModrinthApi.ReleaseVersionsAsync"/> 拿不到东西时用 ——
这张表会越来越旧，所以它**不是**主路径（新版本它里面没有）。
</summary>
```

```text
<summary>
分类的**兜底表**（按项目类型分；2026-10 照 `/v2/tag/category` 里
`header == "categories"` 那一组逐条核对过）。

⚠️ **一栏一张表**，不能共用：拿 mod 的分类去筛资源包会**静默返回 0 条**
   （`categories:technology` 对资源包是个不存在的组合），理由见
   `ModContentType` 的类注释。
⚠️ **数据包和光影包没有分类这一维**（数据包是 Modrinth tag 表里就是空的；
   光影包按用户要求跟 PCL-CE 对齐，不做分类筛选 —— 光影包的"分类"其实是
   风格/性能档那些标签，拿它们当筛选维对上架量很小的光影包来说只在添乱）。
   这两栏的「分类」下拉会被整格收起来（按 `Count == 0` 判，见 `ModsPage.InitFilters`）。
</summary>
```

## Mosslight.App/Views/ModsPage.xaml.cs

<a id="mosslight-app-views-modspage-xaml-cs"></a>

```text
<summary>
「社区资源」那几栏共用的页面（模组 / 整合包 / 数据包 / 资源包 / 光影包）。
移植自 1.0.0 的 `ui/pages/mods_page.py`（收窄版，见 XAML 注释）。

## 类型参数（2026-10）
这一页认一个 <see cref="ModContentType"/>：搜索时带对应的 `project_type` facet，
标题 / 副标题 / 搜索框提示 / 空结果文案 / 图标兜底 / 下载落点全跟着它走。
差别只有五处，全在 `ModContentTypes.cs` 那张表里 —— **这里没有**
"如果是资源包就…"这种分支。参数不传就是模组（老调用点一行不用改）。

## 装到哪个目录
一律问引擎（`IModService.ContentDirFor`）—— 它跟**启动时**用的是同一套判断
（版本隔离的版本装进版本目录，否则装共用的目录；资源包/光影只是最后那段
目录名不同）。自己在这里拼路径迟早会跟引擎判得不一样，那种 bug 表现为
"东西装上了但游戏里没有"。

## 整合包为什么是另一条路
整合包不落进 `mods/`：它自带 mod + 配置 + 加载器，装成一个**独立实例**
（`versions/&lt;实例名&gt;/`）。所以它走工程已有的 <see cref="IModpackService"/>
（`.mrpack` 那条流水线），这里只负责把 Modrinth 上那个 `.mrpack` 的地址交给它。

## 排序（页顶那排标签 2026-10 删掉了）
只剩一条规则：**有关键词按相关度、关键词为空按下载量**（<see cref="SortFor"/>）。

## 网络不通要说清楚
1.0.0 专门有一句「网络环境不佳」的告警条。这里照做 ——
"搜不到结果"和"网络连不上"是两回事，混在一起用户只会以为这个项目不存在。

## 卡片上的图标
结果卡的布局照 `parts/80-mods.qss` 的 #ModCard 一族重建：图标 + 名称 +
作者 + 两行简介 + 底部元信息。图标从 `hit["icon_url"]` **异步下载 + 内存缓存**
（<see cref="ModIcons"/>），没下来之前和失败之后都显示内置的兜底方块图
（**按类型挑**：模组→forge、整合包→pack、资源包→GrassPath、光影→RedstoneLampOn）。
</summary>
```

```text
<summary>
这一次搜索实际带哪个加载器 = **用户在下拉里选的那个**。

⚠️ 没有加载器这一维的栏（数据包 / 资源包 / 光影包）一律「全部」：Modrinth 给
   资源包 / 光影包的 `loaders` 是 `["minecraft"]`，塞 `categories:fabric`
   会**静默**把结果筛没（实测 36437 → 16、983 → 5）—— 那种"搜什么都没结果"
   最难查。（这几栏的加载器下拉本来就被收起来了，见 <see cref="InitFilters"/>。）
</summary>
```

```text
<summary>
这一次搜索用哪个排序 = 用户选的那个（Modrinth 的 `index` 参数原文）。

2026-10 之前页顶有一排「相关度 / 下载量 / 关注数 / 最新 / 最近更新」的标签，
后来整排删掉、只剩一条"有关键词按相关度、空查询按下载量"的硬规则。现在这一维
回到搜索卡的「排序」下拉里，用户自己挑。

⚠️ 空查询配 `relevance` 仍然**没有意义**（没有关键词就没有"相关"可言，
   Modrinth 会返回一串看不出规律的东西），所以引擎里留着同一层兜底：
   `ModrinthApi.SearchProjectsAsync` 遇到空查询 + relevance 会换成 downloads。
   两处都在是故意的：万一以后有别的地方直接调服务，行为也一致。
</summary>
```

```text
<summary>
结果列表滚到**快见底**就续取下一页（= 1.0.0 的 `_maybe_load_more`）。

1.0.0 就是这么干的：`竖着拖到底 → 再抓 20 个`，没有"下一页"按钮。
我们之前只取第一页 20 条，底下那句"显示前 20 个"就永远停在 20 —— 用户 2026-10 报的。
</summary>
```

```text
提前量压到 96（≈ 一张结果卡的高度）：真滑到底才续取。
原来给 240 = 还剩两三张卡就开始下一批，用户只是往下瞄一眼列表就自己长出来，
看着像"界面在抢操作"（2026-10-18 反馈）。
```

```text
----- 行尾：一个「›」 -----

这里原来是一个绿色的「安装」按钮，2026-10 删掉。理由（用户提的，两条都成立）：
  · 二级页面（列表 → 详情）做好之后它是**第二个安装入口**：版本是在详情页里挑的，
    列表上这个按钮只会"拿最新匹配的一版直接装"，跟详情页的行为重叠；
  · 它挨着"点卡片进详情"的整块热区，想点开详情的人很容易误点到它、直接装了个东西。

删掉之后行尾空出一大块、卡片看着不像能点，所以补一个**低调的**指向符：
  · 不是控件、不可点、不抢焦点 —— 图标、名称、作者、描述、徽章、
    "支持 N 个游戏版本 · 下载 x · 关注 y" 一个都没少，只是右侧多了个"还有一层"的暗示；
  · 样式走 Controls.xaml 的 ModCardChevron：卡片里"中灰"那一档，**不引入任何强调色**
    —— 卡片上唯一允许抢眼的东西仍然是 hover 时的强调色描边；
  · 也**没有**用"点开选版本"这种小字：`›` 是个符号，不用翻译，
    5 个语言文件不会因为它多出一条只有中文的文案。
卡片"能点"这件事主要还是靠 hover（浮起底 + 强调色描边，见 Controls.xaml 的 ModCard），
鼠标指针也是手型（root.Cursor）。
```

```text
<summary>
底部那一行元信息：类型徽章 + 加载器徽章（左）、支持版本数 + 下载量 + 关注数（右）。

分左右两组是 1.0.0 的排法（`meta.addStretch()` 把两边顶开）：
左边是"这个东西是什么"，右边是"它有多热 / 能不能装上" —— 扫一眼就分得开。

## 「支持 N 个游戏版本」为什么又加回来了
上一版**故意没放**：那时右边还占着 320px 的「已装的模组」栏，
卡片实测放不下（四个加载器徽章 + 统计数字已经贴着内沿，再加这串 118px
的小字，最后那个徽章必然被裁掉一半）。
那块面板 2026-10 删掉之后结果栏是整页宽，地方够了 —— 加回来。

⚠️ 加回来就必须**保证它不被截断**：所以它自己占一个 **Auto 列**
   （见下面三列的排法）。WPF 的 Grid 在宽度不够时先压 `*` 列，Auto 列
   拿到的永远是内容宽度 —— 于是徽章先被裁，这行小字反而是安全的。
   反过来把它塞进左边那个 `ClipToBounds` 的徽章条里，它就会跟着被裁。

⚠️ 右边那组的容器**不能**设 HorizontalAlignment=Right —— 那是 WPF 的经典坑：
   在 Grid 的 `*` 列里，右对齐的子元素会把整列位置都算成"它自己要的地方"，
   于是 Auto 列量出来是满宽、`*` 列被压成 0，左边的徽章只剩半个
   （实测撞到过：卡片上只剩"模组"和一个"F"）。
   要贴右边就让**它所在的列**是 Auto：Auto 列自己缩到内容宽度，
   而左边那列是 `*`，剩下的宽度自然都归徽章。
</summary>
```

```text
   不裁的话会画到卡片外面、叠在统计数字上（实测撞到过）。
   裁掉最后一个徽章比叠上去强。
```

```text
   （`OnInstallClick` + `MatchingVersionsAsync`，配列表卡右侧那个绿色「安装」按钮），
   2026-10 整个删掉了。删的理由有两层：

   一、界面上是重复入口：二级页面做好之后，版本是在**详情页**里挑的，
       列表上那个按钮只会"挑最新一版直接装"，跟详情页的「下载」重叠、还容易误点。

   二、它留下了本页唯一一处"没人负责收尾"的状态：那条忙行
       「正在获取 {name} 的版本…」是**写在列表页状态行**上的，可收尾
       （进度 / 报错 / 成功）却由 `ReportProgress` / `ReportError` 按
       "当前看得见哪个面板"分流 —— 只要用户在这期间点开过详情页再返回，
       这句忙行就**永远留在列表页上**（看着像卡住了）。排查记录见
       `SearchAsync` 里那段"忙行不许活过 finally"的注释。

   现在安装只剩一条路：详情页每一行右侧的绿色「下载」→ `InstallVersionAsync`。
```

```text
<summary>换一个项目 → 把版本铺出来。返回铺了多少个版本（去重之后的）。</summary>

<remarks>
**一个收缩标签装全部**（2026-10 用户要求，照 PCL 的做法）。

以前是「游戏版本 × 加载器 × 正式/预览」各挂一个标题 —— Fabric API 有 **415 个组**，
滚下去满屏都是标题，真要找的那一版得翻好几屏；而且每组里往往只有一两个版本，
标题本身才是主要内容，看着很碎。

现在合成一组，顺序仍然沿用 <see cref="ModVersionGroups.Group"/> 算出来的规则
（匹配筛选的在前 → 游戏版本新的在前 → 正式版在预览版前面），只是不再分段显示。
行还是**懒建**的（一页 60 行 + 底部「加载更多」），所以不用担心一次铺几百行。
</remarks>
```

```text
PCL 的排版：**一列版本行，顶上没有"分组标题"**（用户 2026-10 指着 PCL 的截图要的）。
标题藏掉之后，靠「加载更多」分页那条路还在（一页 60 行），不会一次建 1200 行。
```

```text
<summary>
续铺分组 —— **现在是个空壳**，留着是因为滚动回调还在调它。

2026-10 起详情页只有**一个**收缩标签（`ApplyDetailGroups` 一次铺完），
不再有"415 个分组分批铺"这回事，所以这里直接返回。
</summary>
```


## Mosslight.App/Views/PersonalizePage.xaml.cs

<a id="mosslight-app-views-personalizepage-xaml-cs"></a>

```text
2026-10 用户：泥土那个棕色「有点丑」，换成紫水晶。
顺带补上一个色相空缺：原来 6 档全在蓝/青/绿/黄/红那条弧上，没有任何紫。
```

```text
<summary>
主窗口。**每次现找**，不要在构造时抓。

⚠️ 2026-10 用户实测「切 English 没反应」就是这里：MainWindow 是**先 new 页面、
   再塞进 `PageHost.Content`**，构造那一刻这一页还没挂进可视树，
   `Window.GetWindow(this)` 返回 null；抓下来存成 readonly 字段就永远是 null，
   于是 `RebuildPage()` 每次都"拿不到主窗口"直接返回 —— 下拉框显示 English，
   界面还是中文。切页时页面会重建，所以这里每次用的时候现找就行。
</summary>
```

```text
<summary>
主题**自带**的强调色（深色主题 #5ec269、浅色主题 #2f8f45）。

⚠️ 这里曾经用的是 `ThemeApplier.ColorOfBrush("AccentBrush")`，那是**当前生效**
   的强调色 —— 用户点「钻石」之后 AccentBrush 就变成青色了，于是「默认」那块
   色板也跟着变青，看起来像"默认色被改掉了"（2026-10 用户报的 bug）。
   正确做法是走 `ThemeService.Palette(mode)`（不传 accent）：那条路径**不经过**
   AccentVariants 的覆盖，拿到的才是主题原配的那一个。
</summary>
```

```text
侧边栏 / 档案区是**构造时贴一次**的文字，只重建本页会让它们停在旧语言
（用户报的"语言系统坏了"有一半是这里）。ApplyLanguage 一次做完：
写配置 → 换词典 → 重贴主窗口 chrome → 重建当前页。
```

## Mosslight.App/Views/SettingsPage.xaml.cs

<a id="mosslight-app-views-settingspage-xaml-cs"></a>

```text
<summary>
刷新界面时用来挡住"值变了 → 回写配置"的循环。

⚠️ **初值必须是 true**，不能是 false。`Slider.ValueChanged` 在
   `InitializeComponent()` **解析 XAML 的过程中就会触发**（Maximum /
   Value 一被赋值就触发），那时候后面那些 `x:Name` 控件**还没建出来** ——
   处理器里一碰 `MaxSlider` 就是 NullReferenceException，而且是在
   窗口构造期抛，崩得莫名其妙（实测：`OnMinChanged` 里炸的）。
   初值给 true，构造期间的抖动就全被挡住了。
</summary>
```

```text
   CheckBox / Button），switch 表达式自己推不出共同类型。以前是靠 `dir` 那一支的
   `(FrameworkElement)` 转型顺手定的型，删掉 `dir` 之后就没人定了（CS8506）。
```

## Mosslight.App/Views/VersionSettingsPage.xaml.cs

<a id="mosslight-app-views-versionsettingspage-xaml-cs"></a>

```text
<summary>
刷新界面时挡住"值变了 → 回写文件"的循环。

⚠️ **初值必须是 true**，不能是 false：<c>Slider.ValueChanged</c> 在
   `InitializeComponent()` **解析 XAML 的过程中就会触发**（Maximum / Value
   一被赋值就触发），那时候后面那些 `x:Name` 控件**还没建出来**，
   处理器里一碰就是 NullReferenceException，而且是在窗口构造期抛。
   设置页踩过这个坑（见 SettingsPage._loading 的注释），这一页同一个坑。
</summary>
```

```text
============================================================
Mod 管理（2026-10 接上；1.0.0 到这页只有一句"Mod 列表还没做"）
============================================================
```

```text
   这一页建好之后会一直缓存着，而换深浅主题时 ThemeApplier 是把
   `BgElevatedBrush` / `TextSecondaryBrush` **换成新对象**。
   FindResource 抓到的是当时那个刷子，换主题后这块头像片会停在
   旧主题的颜色上 —— 跟 BackgroundHost 那个"浅色背景还是深的"同一类。
```

## Mosslight.App/Views/VersionsPage.xaml.cs

<a id="mosslight-app-views-versionspage-xaml-cs"></a>

```text
<summary>
路径归一化后比较（1.0.0 的 `_norm_dir`）。

⚠️ 为什么必须统一：文件对话框返回的是**正斜杠**（`D:/MC/foo`），
   而配置里、预设里都是反斜杠（`D:\MC\foo`）。不统一的话两边对不上，
   代码会以为"当前用的目录不在列表里"，于是**多冒出一项「当前使用」**
   （1.0.0 的用户报过这个）。大小写同理（Windows 不敏感）。
</summary>
```

```text
<summary>
目录列表里的一行：名字（13 加粗）+ 路径（**11px 的小字**）。

1.0.0 特意不用 `QListWidgetItem("名字\n路径")`：一个 item 只有一个字体，
两行只能一样大，而路径通常很长，跟名字一样大会把名字压下去。

⚠️ 上下内边距是 **3** 而不是 1.0.0 的 6（`contentsMargins(10, 6, 10, 6)`）：
   Qt 和 WPF 量同一档字号的"一行"高度不一样 —— Qt 的 13px 是 16、11px 是 14，
   WPF 是 17.3 和 14.6（拿 TextBlock 实测出来的）。照抄 6 的话这一行会变成
   50.4 高，而 1.0.0 是 43：四行就差 28px，目录列表还会因此冒出滚动条、
   下面那排按钮整体下移。改成 3 之后实测 42.9 ≈ 43，**行距对上了**，
   高亮框的高度也就跟着对上了。
</summary>
```

```text
<summary>
列表下面那行：当前用的是哪个目录 / 它不存在时怎么办（1.0.0 的 `_refresh_dir_hint`）。

⚠️ 「新建 .minecraft 文件夹」**只在"现在这个游戏目录根本不存在"时才出现**。
   1.0.0 改过一次（用户报的）：原来判据是"启动器旁边那个目录在不在"，
   结果用户选好了官方目录、游戏能跑，这个按钮还一直杵在那儿 ——
   既多余，又像在催他"再建一个"，点下去还会把游戏目录切到启动器旁边那个去。
   判据应该是"当前这个目录能不能用"，跟启动器旁边没关系。
</summary>
```

```text
   列表里有「启动器目录旁边」这种**算出来的、可能还不存在**的项，
   原来点它会悄悄变成"当前游戏目录" —— 于是下载就装到启动器旁边那个
   目录去了，用户还以为自己选的是官方目录（1.0.0 用户报的就是这个：
   界面高亮和实际在用的目录不一致）。
```

```text
<summary>
挑一个 .minecraft 目录（1.0.0 的 `_browse_dir`）。

⚠️ 起始目录**必须是个真实存在的目录**：1.0.0 原来直接拿当前游戏目录当起点，
   而"官方启动器目录"（`%APPDATA%\.minecraft`）在没装过官方启动器的机器上
   根本不存在 —— 那种情况下 Windows 的文件对话框会当场退出来，
   表现就是"点了浏览…没反应"（用户报过这个）。
</summary>
```

```text
<summary>
`--page versions:&lt;子段&gt;`。给离屏截图用的入口。

为什么需要它：这些状态都是**鼠标点出来**的，而离屏截图（App.SaveWindowPng）
只做 Measure/Arrange、点不了鼠标 —— 不给入口的话这几条分支永远没人验过。
传：
  `filter:all|vanilla|loader|pack`  换筛选（等于点那个下拉）
  `search:&lt;关键词&gt;`                往搜索框里敲字
  `emptydir`                        把游戏目录指到一个**不存在**的路径，
                                    露出「新建 .minecraft 文件夹」按钮和 ⚠ 提示
  `dirnext`                         真的走一遍"切换游戏目录"（选列表里的下一项）
  `activate`                        真的走一遍"双击一个版本"（会切回启动页）
  `geometry`                        把关键控件的**布局实测**打到标准输出

⚠️ 可以用 `+` 拼：`dirnext+geometry` 就是"切完目录之后再把布局量一遍" ——
   光看 PNG 只能靠颜色边界猜，量完才知道差在哪。

⚠️ `emptydir` / `dirnext` 会**写沙箱那份 config.json**（minecraft_dir）。
   截图脚本一律在带 `portable.txt` 的目录里跑，碰不到用户的 %APPDATA%。
</summary>
```

## Mosslight.Core/Animation/AnimationCurves.cs

<a id="mosslight-core-animation-animationcurves-cs"></a>

```text
<summary>
参数化缓动曲线 —— **逐值移植** 1.0.0 的 `ui/widgets/pcl_ease.py`（323 行那份）。

## 为什么这些公式住在 Core 而不是界面层
Python 那边它们住在 `ui/widgets/`（只有动画用）。C# 这边放在 Core 是因为**有两个消费者**：
  · `Mosslight.App` 的 <c>EasingFactory</c> —— 把曲线包成 WPF 的 <c>EasingFunctionBase</c>；
  · 旁边的 <see cref="SlideInTrajectory"/> —— 轨迹引擎按 t 直接取样（回弹要分段、PCL 要两条叠加，
    这些都没法塞进一条 WPF 缓动曲线里）。
两边共用同一份实现，才不会出现"缓动曲线对一个样、轨迹采样对另一个样"这种最难查的分叉。

## 为什么不用 Qt / WPF 内置的曲线
Qt 内置的 `OutBack` 过冲写死 10%（系数 1.70158）、`OutElastic` 的振幅/周期也写死；
WPF 的 `BackEase.Amplitude` 更不是过冲比例（Amplitude=1 实测才约 1.7%）。
而预设要的是"过冲就是 18%"「过冲 28px」这种**能照着填**的参数，所以 1.0.0 自己实现了一套，
这边照抄 —— 包括 `s_for_overshoot()` 那个"二分 40 次反推"的过程，因为峰值位置要解超越方程，
直接写死一个 s 会从小数点后第 5 位开始分叉（逐点比对抓得住、肉眼绝对看不出）。

## 移 plant 的三条规矩
  1. 常数一个不改（<see cref="BackS"/>、elastic 的 amplitude=1/period=0.3、damped sine 的
     cycles=1.15/decay=5.0、PCL 旧版那三个幂次）；
  2. 端点早退（`if t &lt;= 0 / if t &gt;= 1`）照抄 —— 那些不是优化，是**语义**：
     不写的话 `2^(-10t)` 这类式子在端点会留尾数，动画结束时卡片会差一像素没归位；
  3. 原函数**不夹** t，只有 <see cref="Evaluate"/> 夹（对应 Python 的 `as_curve` 里那层
     `_clamp01`）—— Python 的 `make_*` 本身也是不夹的。
</summary>
```

## Mosslight.Core/Animation/AnimationPresets.cs

<a id="mosslight-core-animation-animationpresets-cs"></a>

```text
<summary>
所有预设。**插入顺序就是 Python 里 PRESETS 的顺序**（.NET 的 Dictionary
不保证顺序，但实测按插入序枚举；要顺序请一律走 <see cref="PresetOrder"/>）。

============================================================
这一轮的参数是**重新调的**（1.0.0 那套手感用户评价是"太鸡肋"）
============================================================

轨迹算法一个字没动（<see cref="SlideInTrajectory"/> 仍然跟 1.0.0 逐值对齐，
`tests/slide_anim_golden.json` 那一节照旧全绿），改的只是这七行数值。
判据、量法、扫描过程写在实验目录的 `HandProbe` 探针里，结论落在这张表上，
七条硬指标（每一条都是"能算出数来"的，不是形容词）：

  ① **位移/时长** 落在 0.22~0.55 px/ms —— 低了显拖沓，高了"啪"一下；
  ② **起步速度** ≥ 位移的 1.1%/ms（缓出曲线的起步速度正比于位移，
     所以只能按自己的位移比，不能用一个绝对像素数去卡）；
  ③ **视觉落位**（位移第一次 ≤2px）在 80% 时长之内；
  ④ **亚像素尾巴**（最后一次 >0.5px 之后剩下的时间）≤25% 时长
     （25%~35% 算"绵长"，七档里只允许一档走这条）；
  ⑤ **淡入配合**：透明度到 0.9（看着已经清楚了）之后，剩下的位移要在
     180ms 内走完 —— 否则就是"淡完了卡片还在飞"；
  ⑥ 回弹档：过冲 ≤25px（按**绝对像素**判，不按百分比：百分比对小位移的档不公平）、
     且可见摆动 ≥1 次（0.5px 以下的不算"看得见"）；
  ⑦ 整列 12 张铺完 0.6~1.4 秒 —— 短了眼睛来不及捕捉"依次"，长了难受。

调完的七档（"为什么它跟别的不一样"都写在每一档的注释里）：
  · slide_left      — 120px / 330ms / cubic ／ 基准档：一步到位，收尾不黏
  · slide_up        —  84px / 380ms / quint ／ 唯一"绵长"的一档（亚像素尾巴 130ms）
  · slide_left_bounce — 120px / 460ms / 阻尼正弦 ／ 过冲 18px 后自己收住，**一个峰**
  · pcl_like        —  26px / 300ms / quint ／ 位移最小、单张最慢、整列最快
  · slide_up_elastic — 52px / 560ms / 弹性 ／ 过冲 19.4px 后还**回摆**，七档里唯一两个峰
  · slide_left_back — 120px / 360ms / back:0.10 ／ 冲过头 12px 再退回来，收得最干脆
  · pcl_recipe      —  24px / 300ms / PCL 双轨 + 100ms 淡入 ／ 先快后慢"软着陆"

⚠️ 七档的**名字和性格**是用户点名要保住的，所以 Key / Name 一个没改；
   改的只有数值（外加新增的 FadeRatio）。对话框里的"时长 / 位移 / 错峰 / 有无回弹"
   一律**从这张表现算**，不写死 —— 以后再调参，界面自己会对上。
</summary>
```

```text
---------- 2/7 唯一"绵长"的一档 ----------
1.0.0：460ms / 80px / 100ms / quint。
改：位移 80→84、时长 460→380、错峰 100→70。
这一档**故意保留 quint 的长尾巴**：七档里得有且只有一档是"慢慢收"的
（quint 自带的长尾巴正好是那个性格，实测亚像素尾巴 130ms，是七档里最长的）。
但 460ms 配 80px 是 0.17px/ms —— 太拖，所以时长收到 380、位移提到 84
（0.22px/ms，刚好压在下限上；再慢一档就真成拖沓了）。
跟 cubic 那档的分开点有两处：尾巴长度（130ms vs 53ms）+ **淡入最晚**（0.65：
透明度到 0.9 是 120ms，那一刻位移只剩 8.4px —— "淡着淡着就到位了"）。
```

```text
---------- 5/7 弹性 ----------
1.0.0：900ms / 110px / 95ms / `elastic` 缓动（走 Simple 分支）。
改：时长 900→560、位移 110→52、错峰 95→55。
**缓动曲线没换**（还是那条 `elastic`：amplitude=1.0、period=0.3）——
中途试过换成 `spring:0.12`，量出来才发现走不通，记录在这儿免得下次再试：
  `spring:` 那条在 Simple 分支里走的是 `AnimationCurves.DampedSine`，
  而它**前 1/8 用 `1-(1-t)^2.2` 重整了时间轴**、scale 又是按重整前的包络峰值
  归一化的 —— 实测算出来的过冲只有设定值的 **13%**（填 12% 只有 1.6%），
  等于没有回弹。`elastic` 那条没有这层重整，过冲是实打实的。
1.0.0 真正的问题是**时长和位移**，不是曲线：
  · 900ms 是七档里最长的一档，位移 110px 又最大 → 0.12px/ms，拖；
  · 过冲 41px 绝对值太大。这条曲线的过冲**比例**写死在公式里
    （amplitude 夹了 ≥1 的下限，调小会让曲线反相），
    所以只能靠**缩小位移**来压过冲的绝对像素 —— 110→52 之后
    过冲从 41px 掉到 19.4px，屏幕上从"跳出去了"变成"弹了一下"。
这一档跟另外两档回弹的**分界**（探针按 0.5px 阈值数出来的，不是猜的）：
  · 弹簧（slide_left_bounce）：冲过头 **1** 次 + 回摆 **1** 次；
  · 这一档：冲过头 **2** 次 + 回摆 **2** 次 —— `elastic` 的 2^(-10t) 在前
    三分之一里就走完两次摆动，是七档里唯一"弹完还荡"的一档；
  · 过冲（slide_left_back）：冲过头 1 次、**没有回摆**（退回来就停）。
位移 52px 也是七档里最小的（比两档 PCL 还小），配上最大的过冲比例 ——
看着就是"没挪多远但弹得很欢"，跟别的档一眼分得开。
```

## Mosslight.Core/Animation/AnimationRebound.cs

<a id="mosslight-core-animation-animationrebound-cs"></a>

```text
<summary>
「这一档有没有回弹」—— **量出来的**，不是查表查出来的。

## 为什么不能照抄 1.0.0 的判断
1.0.0 的对话框写的是 `tr("有回弹") if base.style == ANIM_BOUNCE else tr("无回弹")`
（`ui/dialogs/anim_settings_dialog.py` 的 `_update_detail`），而 1.0.0 的 7 档预设里
**一档 ANIM_BOUNCE 都没有** —— 于是那句判断在 7 档里永远显示「无回弹」，
连真会过冲的「弹簧」「过冲 18%」「弹性」「PCL 原味」也一起被说成"无回弹"。
那是 1.0.0 自身的缺陷（判据写死成了形态枚举里的一个值），照抄没有意义。

## 现在怎么算
直接**按时间扫一遍轨迹引擎**（<see cref="SlideInTrajectory.DisplacementAt"/>）取最小值：
偏移是"正 = 还没到位、负 = 冲过头"，所以最小值越过 0 越深、回弹越明显。
这样判据跟"真正播出来的动画"是同一份数 —— 以后加形态、改参数，
判断自动跟着走，不会再出现"加了形态忘了改文案"。

阈值 <see cref="ReboundEpsilonPx"/> = 0.5px：亚像素的过冲在屏幕上看不见，
说它有回弹是骗人。PCL 那档实测过冲约 **1.95px**（16px 位移 × 0.69 × 17.77%），
刚过阈值 —— 它是**真的**会过冲，只是很轻。
</summary>
```

## Mosslight.Core/Animation/SlideInTrajectory.cs

<a id="mosslight-core-animation-slideintrajectory-cs"></a>

```text
<summary>
细分最多做几轮（每轮把不满足误差的区间对半切）。

⚠️ 这个数是**这一轮调手感时从 6 提到 12 的**，理由是实测：
   弹簧那档的分段比从 0.5 调到 0.6 之后，"滑到位 → 回弹"那个拐点上的速度断得更狠，
   6 轮跑完还差 0.045px（自检的判据是 &lt; 0.03px）。
   根因是下面那个循环里 `i++` 会**跳过刚切出来的右半段**，同一轮里每个区间
   其实只被检查了一次，收敛得比"对半切"该有的速度慢一倍 ——
   与其动循环（那会改变关键帧的分布，属于改算法），不如把轮数提上去：
   多几轮只在**真正不满足误差**的地方加点，关键帧数量涨幅很小
   （弹簧那档实测 59 → 69 个点，多 10 个）。
</summary>
```

```text
<summary>
某一时刻的透明度。

  · PCL —— `_build_pcl()` 里那条 `QPropertyAnimation(self._fade, b"opacity")`：
    0 → 1，时长 `max(60, min(100, fast_ms))`，OutCubic，跟位移同时开始；
  · 其余形态 —— 1.0.0 **不淡入**（见类注释第 3 条），2.0 保留"所有形态都淡入"，
    时长 = <c>整条轨迹 × 预设的 FadeRatio</c>、曲线 = 该档自己的缓动。

⚠️ 下面那句 `<c>× FadeRatio</c>` 是**这一轮调手感新加的**（见
   <see cref="AnimationPreset.FadeRatio"/> 的注释）：以前这里写的是"时长 = 整条轨迹"，
   等于让透明度跟位移共用一条曲线 —— 缓出曲线在前 10% 就把透明度推到 0.27~0.65
   （淡入形同虚设），过冲曲线更是让透明度冲到 1.25 之后再"淡"完。
   改成按比例之后，淡入有了自己的时间轴；而 <c>FadeRatio = 1.0</c>（默认值）
   仍然精确等于改动之前的行为，自检里那些拿金标准造出来的对照用例不受影响。
</summary>
```

## Mosslight.Core/AppInfo.cs

<a id="mosslight-core-appinfo-cs"></a>

```text
<summary>
应用元信息（对应 1.0.0 的 core/app_info.py）。

版本号**只有一处来源**：仓库根 `Directory.Build.props` 里的 `&lt;Version&gt;`。
构建时它会变成程序集的 `AssemblyInformationalVersion`（= exe 属性里的「产品版本」），
这里再把它读出来给界面用 —— 于是"界面上写的"和"exe 属性里写的"**不可能对不上**。

## 为什么不再用 const（2026-10 改的）
1.0.0 的教训是反过来的：旧代码把 "v0.1.0-dev" 硬编码在侧边栏里，而实际发布的
tag 早就到 v0.0.5-alpha 了，界面上显示的是假的。2.0 一开始改成"只在
`AppInfo.AppVersion` 这一处维护"，但发版时**要改源码**才能把它换成 tag 的版本
（release.yml 里去正则替换 + 读回来断言，正则写歪了就发版失败）。
现在版本号是构建参数：`dotnet build -p:Version=2.1.0-beta` 一传，
exe 属性和界面**一起**变，源码一个字都不用动。

## 两个名字是**故意分开**的
  AppName        界面上自己叫的名字：侧边栏左上角、关于页。短、干净
  AppDisplayName Windows 那边看到的：窗口标题、任务栏、exe 属性
</summary>
```

## Mosslight.Core/Appearance/BackgroundModes.cs

<a id="mosslight-core-appearance-backgroundmodes-cs"></a>

```text
<summary>
背景图的铺法。命名和顺序**对齐 Windows 的"选择适合度"**：
填充 / 适应 / 拉伸 / 居中 / 跨区 —— 用户在系统里怎么挑的，在这儿照着挑就行，
不用重新学一套说法。

⚠️ 这些是**界面文案**：翻不翻由界面层调 <c>Localizer.Tr()</c> 决定，
这一层保持中文原样，别在这儿翻两遍。
</summary>
```

## Mosslight.Core/Localization/Localizer.cs

<a id="mosslight-core-localization-localizer-cs"></a>

```text
<summary>
用得到的语言 = **内嵌的 ∪ 磁盘上多出来的**，按 <see cref="LanguageNames"/>
的顺序排（未知的排后面）。

⚠️ 这里**必须**并上内嵌那份。改内嵌之前这个方法是"枚举磁盘目录"，
   现在磁盘目录通常**不存在**（发布包只有一个 exe），只列磁盘的话
   语言下拉框里会只剩兜底的中文 —— 而"能切 5 种语言"正是用户要保住的东西。
   磁盘上多出来的（用户自己加的 `xx.json`）也要列，那是覆盖机制的一部分。
</summary>
```

## Mosslight.Core/Logging/LogLine.cs

<a id="mosslight-core-logging-logline-cs"></a>

```text
<summary>
游戏日志里的噪音片段。

## 这份清单的出处（先说清楚，免得以后被当成"抄错了"）
用户提到的是 1.0.0 <c>core/config.py</c> 里的 <c>LOG_NOISE_PATTERNS</c>。
实测那份代码**只存在于 <c>experiments/MC_LuncherTEST-4.0/core/config.py</c>**
（以及更早的 <c>MC_LuncherTEST-3.0.py</c>），**1.0.0 正式版里没有**：
  · <c>D:\开发工程\DHML-src\core\config.py</c> 里搜不到 <c>LOG_NOISE</c>；
  · 1.0.0 的 <c>ui/dialogs/log_window.py</c> 也没有任何过滤逻辑。
所以这一份是**照那个实验版抄的**（用户指的就是它），并且做成了可关的
（见 <c>AppSettings.IsLogNoise</c> 和窗口工具栏上的「过滤噪音」开关）——
1.0.0 那边根本没有这个开关，属于**新增能力**，不是"照抄"。

## 为什么只留了三条
实验版那份清单里还有 <c>"Time elapsed"</c> 之类；这里只保留**确实会刷屏、
且没有任何信息量**的三条。宁可少滤几条（用户还能自己关），
也不要滤掉一条有用的 —— 发问题报告时缺了关键行是查不出来的。
</summary>
```

## Mosslight.Core/Services/Engine/Branding.cs

<a id="mosslight-core-services-engine-branding-cs"></a>

```text
============================================================================
 启动器品牌：让游戏主界面那行显示启动器名字
 （移植自 1.0.0 的 core/branding.py）

     Minecraft 1.20.1/Mosslight/Fabric（已修改）
                 └版本名─┘└─版本类型─┘└游戏自己的"改过"标记

 ## 游戏那行字是怎么拼的（拆 1.20.4 的 TitleScreen 字节码读出来的）

     s = "Minecraft " + 版本名
     if (是 Demo 世界)                        s += " Demo"
     else if (--versionType 不是 "release")    s += "/" + --versionType
     if (checkModStatus().shouldReportAsModified())  s += 翻译("menu.modded")   // 「（已修改）」


 ## 走过的弯路（别再试了）

 第一版是"另写一个只含 version.json 的小 jar、插在 classpath 最前面"，
 靠"类加载器按顺序取第一个 version.json"来改**版本名**。**原版有效、Fabric 无效**：
 Fabric 的 KnotClassLoader 自己重写了 getResource / getResources / getResourceAsStream，
 资源查找走它自己的 URL 集合（里面就有游戏 jar），系统 classpath 上那一份轮不到。
 改游戏 jar 更是不能做：40 MB、会变指纹、还跟自己的 sha1 校验打架。
 `--versionType` 才是正路 —— 它是游戏给启动器留的口子，原版和加载器都认。

 ## Forge / NeoForge 那边为什么看不见（2026-10 实测，别再试了）

 Forge / NeoForge 主界面左下角**不是原版那行**，是加载器自己的品牌块：
     net/neoforged/neoforge/internal/BrandingControl（Forge 是 net/minecraftforge/internal/）
     "NeoForge " + NeoForgeVersion.getVersion()
     "Minecraft " + NeoFormVersion.getMCVersion()
     I18nExtension.parseMessage("fml.menu.loadingmods", ModList.size())  // 「N 个模组已加载」
 打过补丁的 TitleScreen 调 forEachAboveCopyrightLine 把它画在原版那行的位置，原版那行被顶掉。
 所以这两个加载器下 `--versionType` 照传、日志里能看到，用户就是看不见 —— PCL2 / HMCL 同理。

 试过两条强行把名字塞进去的路，**都失败**（1.20.5-NeoForge 20.5.21-beta 实测）：
   · 资源包覆盖 `fml.menu.loadingmods`：包确实启用了（日志
     "Reloading ResourceManager: …, file/mosslight-brand.zip"），那行字没变；
   · lowcodefml 纯语言模组（841 字节，只有 lang json、没有代码）：
     模组列表里 State: loaded、模组数 +1，那行字还是没变。
     原因：I18nExtension.loadLanguageData(IModInfo) 只认加载器和模组 jar 里的语言表，
     后加载的包/模组覆盖不了它。真要改只能动 NeoForge 自己的 jar —— 那不能做。
============================================================================
```

## Mosslight.Core/Services/Engine/CleanroomCatalog.cs

<a id="mosslight-core-services-engine-cleanroomcatalog-cs"></a>

```text
============================================================================
 Cleanroom：版本列表 + 从官方安装器里把版本 JSON 取出来

 ## 它是什么

 Cleanroom 是 **Forge 1.12.2 的分支**（`top.outlands.foundation.boot.Foundation`
 当主类，自带 LWJGL 3.4.3 那一套），只支持**一个**游戏版本：1.12.2。

 ## 安装方法（跟 Forge 不一样）

 **它其实有无人值守命令行**（0.7.x 实测 `--help`：
 `java -jar cleanroom-<ver>-installer.jar client -d <游戏目录> --no-profile -y --json`；
 更早的 0.2.4-alpha 干脆就是 Forge 的 `SimpleInstaller`，`--installClient` 那套）。

 但我们**故意不跑它**，理由有两条：
   1. 跑安装器要先等它自己下几百 MB（它自己那套下载器没有我们的镜像 / 重试 /
      并发，国内网络下经常卡死），而且我们拿不到它的进度，下载窗口只能一直转圈；
   2. 它没有额外要做的事 —— 安装器里那份 `version.json`（121 KB）已经是**完整的
      版本 JSON**：`mainClass` / `minecraftArguments` / `assetIndex` / 217 条库 /
      `downloads.client` 全都在，`install_profile.json` 里 `processors: []`、
      `data: {}` 说明**没有任何要打补丁的步骤**。

 所以我们的装法 = 下安装器 → 抽出 `version.json` → 补上空的库地址 → 落盘 → 用
 自己的下载器下文件（有镜像、有进度、断点可重试）。**唯一**比跑安装器多花的
 是那个 15 MB 的安装器包（拿完就删）。

 ## 三个坑

 1. **`version.json` 里 Cleanroom 自己那条库的 `url` 是空串**
    （`com.cleanroommc:cleanroom:<ver>:universal`，带 classifier）。不补的话
    下载任务会拿着空地址发请求，报一个看不出原因的错。这里统一补成 maven。
 2. **它对 Java 的要求按版本分档**：0.5.0 起要 **Java 25**（`version.json` 的
    `javaVersion.majorVersion`），0.5.0 之前要 Java 21。安装本身不需要 Java，
    **启动**才需要 —— 所以安装不拦，只是把话说明白（见 <see cref="JavaNoteFor"/>）。
 3. **列表是 XML 不是 JSON**（maven-metadata.xml）。照 JSON 解析会得到 0 个版本，
    而界面上看起来就是"这个版本没有可用的 Cleanroom"，很难往"格式看错了"上想。
============================================================================
```

## Mosslight.Core/Services/Engine/DownloadManager.cs

<a id="mosslight-core-services-engine-downloadmanager-cs"></a>

```text
<summary>
（官方前缀, 镜像前缀）。顺序有意义：长的、更具体的放前面。

实测记录：libraries / resources 那两条测过；piston-* 没实测，
失败会自动回退到官方，所以先留着。
</summary>
```

```text
<summary>
开始下载**所有还没提交过的任务**。

⚠️ 这里**不能**写成"已经开过就直接 return"。那样的话"一批任务"这个假设
   被写死了：第一次 Start 之后再 <see cref="Add"/> 进来的新任务
   **永远不会被提交**，界面上的表现是它们全都停在「等待」、
   进度条再也不动，而且**不报任何错**。
   （实测踩到：.mrpack 自己一批、里面的文件又一批，第二批就卡死在「等待」。）
   靠 `_submitted` 记住哪些提交过，所以反复调 Start 是安全的。
</summary>
```

```text
<summary>
记一次镜像的成功/失败；连续失败够了就熔断一段时间。

⚠️ 关键：熔断的是"再试镜像"这件事，不是下载本身 ——
   失败的文件照样会走官方源下下来，用户不会因此少文件。

为什么需要它：BMCLAPI 抽风时（实测过一次清单接口读超时），如果每个文件都
"先试镜像、等超时、再回退官方"，764 个文件能拖到几十分钟，
用户看到的就是"进度条卡住不动"。
</summary>
```

## Mosslight.Core/Services/Engine/DownloadSourceMode.cs

<a id="mosslight-core-services-engine-downloadsourcemode-cs"></a>

```text
============================================================================
 「下载源」策略：设置 → 下载 里那四个选项（2026-10 用户要求加的）

 | 值       | 行为 |
 |----------|------|
 | auto     | **默认**：镜像（BMCLAPI）优先、失败换官方；镜像连续失败会熔断 60 秒 |
 | official | 只用官方源（Mojang / Forge / NeoForge 直连） |
 | mirror   | 只用 BMCLAPI；**它没镜像的地址**（Modrinth / CurseForge / Quilt meta 等）照旧直连 |
 | dual     | 双流并发：同一个文件同时从镜像和官方下，谁先完成用谁 |

    以后在枚举中间插一个值，序号型的配置就会静默变成另一个策略。
    没有镜像的地址它退化成普通单源，不会凭空多出一条流。
============================================================================
```

## Mosslight.Core/Services/Engine/GameInstaller.cs

<a id="mosslight-core-services-engine-gameinstaller-cs"></a>

```text
<summary>
把版本 JSON 落到 `&lt;版本目录&gt;/&lt;名字&gt;.json`。

⚠️ 这一步**必须做**，而且要在下载之前做。少了它，装完就只有 jar、
没有 json —— 版本列表是靠 `&lt;目录&gt;/&lt;目录&gt;.json` 认版本的，
于是"下载明明成功了，列表里却找不到这个版本"（用户 2026-09 报的）。

注意：**不是**从 jar 里那个 `version.json` 取 —— jar 内那个只有
id / pack_version / java_version 之类的元数据，是游戏自己用的；
启动器要的是版本清单（piston-meta）给的这份（downloads / libraries /
mainClass / assetIndex…），我们本来就下到了，只是以前没落盘。

文件名跟目录名保持一致（用户改过版本名时用改后的名字）；
必要时把 JSON 里的 id 也改成新名字，保持"目录 = 文件名 = id"三者一致
—— 否则启动时 `--version &lt;id&gt;` 和目录对不上。
</summary>
```

```text
<summary>
这份 JSON 是"启动器要的那份"吗（而不是 jar 里那个 422 字节的元数据）。

⚠️ 用户真踩过：版本目录里只有 jar，于是从压缩包里把 jar 内的
`version.json` 拖出来当版本 JSON 用。那个文件只有
id / pack_version / java_version，**没有** mainClass / libraries / downloads，
拿它启动必然崩，而且"补全"也救不了 —— 补全得先知道该下什么。

判据：有 mainClass，并且有 libraries / downloads / inheritsFrom 之一。
</summary>
```

## Mosslight.Core/Services/Engine/GameLocales.cs

<a id="mosslight-core-services-engine-gamelocales-cs"></a>

```text
============================================================================
 「首次启动时写进游戏的默认语言」——设置 → 其他 → 游戏语言

 为什么要单独一个设置（2026-10 用户要求）：
 启动器自己的界面语言（设置 → 个性化 → 语言）是给**启动器**看的；
 进游戏之后游戏说哪种语言是 `options.txt` 里那行 `lang:` 决定的。
 两个是**两件独立的事** —— 有人界面用英文、游戏想用中文，反之也行。

 行为约定（都是刻意选的）：
   · 只在这个版本**还没有 `options.txt`** 时写一次（= 第一次启动）；
     已经有就一个字都不动 —— 用户在游戏里改过语言，不能被启动器覆盖；
   · 写进去的文件只有一行 `lang:<代码>`，其余交给游戏自己补默认值；
   · 选「自动」= 跟启动器的界面语言走（zh_CN → zh_cn 这种直接对应）。

    （实测 26.3 的 assets 索引里 `minecraft/lang/zh_hk.json` 有），别当成笔误删掉。
============================================================================
```

## Mosslight.Core/Services/Engine/JavaScanner.cs

<a id="mosslight-core-services-engine-javascanner-cs"></a>

```text
<summary>
这条失败记录还在"先别再试"的窗口里吗。

时间戳读不出来（手改坏了 / 老格式）就当不在窗口里 —— 宁可多探一次，
也别让某个 Java 永远不出现在列表里。
</summary>
```

## Mosslight.Core/Services/Engine/JsonIO.cs

<a id="mosslight-core-services-engine-jsonio-cs"></a>

```text
============================================================================
 读 JSON —— **统一在这里处理 BOM**

    它内部走的是 `Utf8JsonReader`，而那个 API 明确不支持跳过字节序标记 ——
    喂进去一个 UTF-8 with BOM 的文件，第一句就是
    「0xEF is an invalid start of a value」。

    而 1.0.0 那边**到处都在防这件事**：`core/versions.py` 读版本 JSON 用的是
    `encoding="utf-8-sig"`、`core/config.py` 也是读字节让解析器自己认 BOM，
    注释里还专门点名"记事本存出来的就是这种"。所以这不是理论问题 ——
    用户手改一下 `1.20.1.json`（记事本默认就带 BOM），那个版本就会从列表里
    消失，只在扫描告警里留一句「json 格式错误」。

    `JsonSerializer.Deserialize<byte[]>` 倒是会自己认 BOM，所以
    Localizer 那边读字节是对的 —— 但引擎这边用的是 JsonNode.Parse。
    两边行为不一样，所以统一收到这里，别再各写各的。

    基本见不到（记事本存 UTF-16 是"Unicode"选项，很老的用法），
    真碰上了会报"格式错误"，比猜错编码强。
============================================================================
```

## Mosslight.Core/Services/Engine/LabyModCatalog.cs

<a id="mosslight-core-services-engine-labymodcatalog-cs"></a>

```text
============================================================================
 LabyMod 4：版本列表（能读），安装（**还没接，见文件末尾的说明**）

 ## 为什么只做了列表

 和别家不一样：LabyMod **没有安装器、也没有可用的 profile JSON**，
 它给的是两样东西：

   1. 一个 manifest（production / snapshot 两个频道），列出「LabyMod 版本号 +
      这个版本支持哪些游戏版本 + 每个游戏版本对应的一份"版本 JSON"地址」；
   2. 每个游戏版本一份**几乎就是原版的版本 JSON**（`mainClass` 是
      `net.minecraft.launchwrapper.Launch`、67/25 条普通库、`downloads.client`、
      `assetIndex`、参数里多一句 `--dummyMinecraftClass net.minecraft.client.main.Main`），
      **不含任何 LabyMod 自己的库、也没有 tweakClass**。

 真正的 LabyMod 客户端是另一个 37 MB 的 jar
 （`/api/v1/download/labymod4/{channel}/{commitReference}.jar`），以及 6 个资源包
 （`common` / `fonts` / `i18n` / `shader` / `vanilla-theme` / `fancy-theme`，
 实测 `common` 里只有 `assets/labymod/**`，**没有 class**）。

 ## 怎么装（照 PCL2-CE 的 `ModDownloadLib.cs` 抄的，不是猜的）

 那份源码 2026-10 从 GitHub API 拿到（`ModDownloadLib.cs`，213 KB / 4617 行，
 `hasLabyMod` 那一段在 4490 行附近），它的步骤见
 <see cref="LabyModCatalog.BuildVersionJson"/> 的注释：合并这份版本 JSON →
 剔掉跟 LabyMod 隔离库重名的 LWJGL → 追加 LabyMod 自己的 108 条库
 （**里面有他们 fork 的 `net.minecraft:launchwrapper:4.1.3`**，`mainClass`
 能解析全靠它）→ 追加 37 MB 客户端 jar → 写 `labymod_data`。

 也就是说 LabyMod **能自动装**，不需要它的启动器；之前"卡在启动注入"的结论
 是因为没拿到这份源码（当时找的是 `.vb` 文件名，仓库里其实是 `.cs`）。
============================================================================
```

```text
<summary>
一份 manifest → **这个游戏版本能用的** LabyMod 版本行。

manifest 的形状（实测）：
  `{ "labyModVersion": "47.3", "commitReference": "1bf7a24b", "sha1": …, "size": …,
     "assets": {"common": "<sha1>", …},
     "minecraftVersions": [{"tag": "26.3", "version": "26.3", "index": 4, "type": "release",
                            "runtime": {"name": "java-runtime-epsilon", "version": 25},
                            "customManifestUrl": "…/production/26.3/1bf7a24b.json"}, …] }`

⚠️ 挑版本要看 `version`（真游戏版本）**不是** `tag`（显示名）：
   1.8 那一条是 `tag: "1.8"` / `version: "1.8.9"`，按 tag 比会对不上 API 给的版本号。
⚠️ `type` 也有假的（26.3 是 `release`，但有的频道里是 `snapshot`），
   这里按**频道**决定"稳定 / 测试"，不按条目里的 `type`。
</summary>
```

## Mosslight.Core/Services/Engine/LaunchPlanBuilder.cs

<a id="mosslight-core-services-engine-launchplanbuilder-cs"></a>

```text
<summary>
QuickPlay（`--quickPlayMultiplayer`）是 1.20 引入的。按发布时间判断而不是按
版本号：1.20 的正式版发布日是 2023-06-07，往前留一点余量给快照
（发布日之前的快照也可能已经带上了）。这跟 PCL 的判断方式一致。
</summary>
```

```text
<summary>
这个版本该用 QuickPlay 还是老的 `--server/--port`。

releaseTime 是 ISO8601（"2023-06-07T12:00:00+00:00"），前 10 位就是日期，
字典序比较即可。读不出来（老版本 JSON 没这个字段）就当作不支持。
</summary>
```

```text
`"Minecraft " + 版本名 + (type == "release" ? "" : "/" + type)`（1.20.4 实测）。
用户填过「自定义信息」就用他的；没填就给一份品牌默认值（`Mosslight/Fabric`），
这就是"启动器名字进游戏"。填 `release` 可以关掉它（游戏那边按原样不显示）。
```

## Mosslight.Core/Services/Engine/LiteLoaderCatalog.cs

<a id="mosslight-core-services-engine-liteloadercatalog-cs"></a>

```text
============================================================================
 LiteLoader：版本列表 + 自己拼版本 JSON

 ## 为什么不像 Forge 那样跑安装器

 LiteLoader 的安装器是**纯 Swing GUI**：主类 `com.mumfrey.liteloader.installer.Installer`，
 `main(String[] args)` 压根不看 args，直接 `new InstallerPanel(...)` 然后死等用户点按钮
 （官方源码 Mumfrey/LiteLoaderInstaller）。在启动器里弹一个要用户自己点的窗口，
 就等于"一键装好"变成"你自己去点"。

 而且**它也不需要安装器**：LiteLoader 是"launchwrapper + 一个 tweakClass"的加载器，
 安装器干的事就是往版本 JSON 里写 `mainClass` / `minecraftArguments --tweakClass …`
 再加几条库。我们照抄这份 JSON 就行 —— PCL 上游（VB 版 `ModDownloadLib.vb`）也是这么装的：
 `arguments.game = ["--tweakClass", tweakClass]`、`mainClass = net.minecraft.launchwrapper.Launch`、
 `inheritsFrom` + `jar` 指原版，库 = versions.json 里那几条 + 追加一条 liteloader 自己的 jar。

 ## versions.json 的形状（实测 `https://dl.liteloader.com/versions/versions.json`）

     { "meta": {...},
       "versions": {
         "1.8": {
           "repo": {"stream":"RELEASE","type":"ivy","url":"http://dl.liteloader.com/versions/"},
           "artefacts": {                       // ← 正式版（有它就代表这个 MC 版本发过正式版）
             "com.mumfrey:liteloader": {
               "6e9028…":  {"tweakClass":"com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                            "libraries":[{"name":"net.minecraft:launchwrapper:1.11"}, …],
                            "stream":"RELEASE","file":"liteloader-1.8.jar",
                            "version":"1.8","md5":"6e9028…","timestamp":"1463585254"},
               "latest":   { …同上… }
             }
           },
           "snapshots": {...},                  // ← 只有测试版（1.12.2 只有这一边）
           "dev": {...}
         }, …
       } }

 ## 三个坑

 1. **只有 `artefacts`（正式版）能用**。`snapshots` 那批（1.8.9 / 1.9 / 1.10 / 1.11 /
    1.12 / 1.12.1 / 1.12.2）的库列表里带着 `org.spongepowered:mixin:0.7.4-SNAPSHOT`，
    而 spongepowered 那个快照仓库现在 404 —— 装了也起不来。所以列表里只列正式版。
 2. **jar 的真实路径是 `{repo.url}com/mumfrey/liteloader/{version}/liteloader-{version}.jar`**
    —— 用 LiteLoader **自己那个版本号**，两段用同一个值。
       `file: "liteloader-1.7.10.jar"`，可服务器上那个目录里只有
       `liteloader-1.7.10_04.jar`（实测 `…/1.7.10_04/liteloader-1.7.10.jar` 是 **404**）。
       `file` 只在 1.8 / 1.9.4 / 1.10.2 这几个"版本号刚好等于游戏版本"的地方巧合地对得上。
       `dl.liteloader.com` 对**别的目录**里的任意文件名都会 200（实测），
       所以只能按上面那条规则拼，拼完再验的那一步说明不了什么。
 3. **1.8+ 的安装器源（jenkins.liteloader.com）已经死了**（301 → HTTPS 超时），
    redist 目录只覆盖到 1.7.x。反正我们不跑安装器，这条只是"别去找它"的备注。
============================================================================
```

```text
数据里没给 url 的按名字猜一个主仓库（launchwrapper 在 Mojang 那儿，
asm / jopt-simple 在 Maven Central 上，两处都是实测能下的）
```

```text
   数据里给的 md5 不是 sha1；size 写 0 又会被 `GetValue<long>()`
   读成 "Int32 转 Int64 失败"（真踩到过：整条安装直接失败）。
   没有就是没有，别塞个占位值进去。
```

## Mosslight.Core/Services/Engine/LoaderCatalog.cs

<a id="mosslight-core-services-engine-loadercatalog-cs"></a>

```text
============================================================================
 模组加载器数据（下载页第二页用）
 （移植自 1.0.0 的 core/loaders.py）

 五家：Forge / NeoForge / Fabric / Quilt / OptiFine。
 每一行有三种状态，界面必须照实显示：

     Ok             能列出并安装
     NotImplemented 我们还没做
     Error          有接口但这次拉不到（网络问题）—— 这个**不能**说成"还没做"，
                    否则用户以为功能不存在，而其实只是网络抖了一下

 ## 接口（都实测过）

 | 加载器    | 接口                                        |
 |-----------|---------------------------------------------|
 | Forge     | BMCLAPI `/forge/minecraft/<mc>`             |
 | NeoForge  | BMCLAPI `/neoforge/list/<mc>`               |
 | Fabric    | BMCLAPI `/fabric-meta/v2/versions/loader/<mc>` |
 | Quilt     | 官方 `meta.quiltmc.org/v3/versions/loader/<mc>` |
 | OptiFine  | BMCLAPI `/optifine/<mc>`                    |

    （`COMMON_NO_SUCH_OBJECT`），所以走官方 meta。
============================================================================
```

```text
<summary>
⚠️ Fabric / Quilt 的 meta 是两套不同的服务：Fabric 有 BMCLAPI 镜像
（国内稳），Quilt **没有**（实测 404），只能走官方。
</summary>
```

```text
<summary>
Legacy Fabric（Fabric 团队维护的"老版本"分支，1.8 ~ 1.14 那批游戏）。
它的 meta 跟 Fabric 是**同一个 schema**（实测 `/v2/versions/loader/1.12.2`
与 Fabric 官方返回的字段、`profile/json` 的 `inheritsFrom` / `mainClass`
都对得上），所以列表和 profile 两条路都能复用 Fabric 的代码。
⚠️ BMCLAPI 没有镜像它（`/legacyfabric-meta` 是 404），只能走官方。
</summary>
```

```text
<summary>
第二页从上到下显示哪些加载器。顺序照 PCL（常用的在前）。

⚠️ 这张表跟界面上「安装包」那一段是**同一套 key**（那边由它生成左栏
   和安装选项页的分组，见 `DownloadPage.BuildNavTable` / `BuildLoaderGroups`），
   自检里对着两边是否一致（`CheckLoaderKeyParity`）。
⚠️ 表里有一家、<see cref="Supported"/> 里没有它 = 界面上老实显示"安装还没接"。
   别为了"看着干净"把没做的从这张表里删掉：用户会以为启动器根本不支持。
</summary>
```

```text
<summary>
OptiFine 里**不算正式发布**的类型 —— 这些列在列表里会把"最新版"顶掉，
而用户要装的多半是正式版（实测 1.21.1：不滤的话 latest 变成
`preview_OptiFine_1.21.1_HD_U_J1_pre15`）。
</summary>
```

```text
<summary>
BMCLAPI `/optifine/&lt;mc&gt;` → 统一行。

字段：`{"filename": "OptiFine_1.20.1_HD_U_I5.jar", "type": "HD_U",
       "patch": "I5", "forge": "Forge 47.0.35", "mcversion": "1.20.1"}`。

版本号从 **filename** 里切（`OptiFine_1.20.1_HD_U_I5.jar` → `HD_U_I5`）：
  1. 去掉 `.jar`
  2. 去掉 `OptiFine_&lt;mcversion&gt;_` 前缀
  3. **再取最后两段**（下划线分段）→ `HD_U_I5`
第 3 步不能省：预览版的文件名是
`preview_OptiFine_1.20.1_HD_U_I6_pre6.jar`，只做前两步会得到
`OptiFine_1.20.1_HD_U_I6_pre6` 这种怪东西（踩过）。
</summary>
```

```text
Legacy Fabric 是 Fabric 的「老版本分支」，meta 结构一模一样
（实测 `/v2/versions/loader/1.12.2` 返回 75 条，字段 loader.version /
 loader.stable / intermediary 全在）—— 解析器直接复用 ParseFabric。
```

## Mosslight.Core/Services/Engine/LoaderCompat.cs

<a id="mosslight-core-services-engine-loadercompat-cs"></a>

```text
<summary>
Fabric API / Legacy Fabric API 是不是**自动带上**的（PCL：选了 Fabric
就连 Fabric API 一起选上，选的是列表里最新的那一条）。

⚠️ Quilt 这里也自动带（用户 2026-10："Quilt 也许能和 fabric 一样自动选 API？"）：
   PCL 有 QFAPI / QSL 可以带，**我们没有**（还没接），而 QFAPI 本身就是
   Fabric API 的 Quilt 移植 —— 带不了 QFAPI 的时候，带 Fabric API 是
   唯一能让人装完就能玩的选择。红字警告里会说明"现在勾的是 Fabric API"。
</summary>
```

## Mosslight.Core/Services/Engine/LoaderInstaller.cs

<a id="mosslight-core-services-engine-loaderinstaller-cs"></a>

```text
<summary>安装器默认超时（秒）。</summary>

⚠️ 这个数字按"最坏情况的网络"给：实测 Forge 1.20.1 顺利时 200 秒就完，
   但网络抽风时**安装器自己要下 36 个库**，我们这边只能干等。
   给 2 小时是为了"宁可慢也别被我方掐死"（正常根本用不到）。
```

```text
<summary>
OptiFine 那个 jar 是**双入口**的（实测 MANIFEST + 字节码）：
    Main-Class: optifine.InstallerFrame     ← `java -jar` 走这个 = **弹窗口**
    optifine.Installer                      ← 这个的 main 直接装，不开窗口
所以 OptiFine **不能**用 `-jar`：在启动器里弹一个要用户自己点按钮的窗口，
等于让"一键装好"变成"你自己去点"。
</summary>
```

```text
<summary>
Forge 安装器在**官方 maven** 上的地址（主地址的备用地址）。

⚠️ 产物版本号有两套写法（实测）：
  · 1.13 起（含 1.16.5 / 1.20.1）：`&lt;mc&gt;-&lt;forge&gt;`，如 `1.20.1-47.4.0`
  · 老版本（1.7.10 / 1.8.9 那批）：**多一个后缀** `&lt;mc&gt;-&lt;forge&gt;-&lt;mc&gt;`，
    如 `1.7.10-10.13.4.1614-1.7.10`（不带后缀是 404）
</summary>
```

```text
   `/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar`
   而 BMCLAPI 的版本列表给的是 `47.1.106`，所以这里要自己拼回去 ——
   直接拿 `47.1.106` 去问是 404（踩过）。
```

```text
   用户报的"NeoForge 的安装器失败了：net.neoforged:JarJarFileSystems:0.4.0@jar"
   就是"只看到最后一句"的典型 —— 真正的原因往往在它前几行。
   写文件是**原样**的，不经过 InstallerOutput 的刷屏过滤。
```

```text
<summary>
安装器输出里**最能说明问题**的一句（成功时也会返回一句，由调用方决定用不用）。

从后往前找，跳过两类：
  · `Patching ...` 那种刷屏（<see cref="InstallerOutput.SpamPrefixes"/>）
  · 安装器自己的套话（`There was an error during installation` ——
    这句它每次失败都打，信息量为零，真正的原因在它**上一行**，
    实测就是 `There is no minecraft launcher profile in ...`）

⚠️ 两个细节跟 1.0.0 逐字对齐，别"顺手优化"：
   1. 套话判的是**包含**（`gen in text`），不是相等 ——
      安装器会在前后缀上别的东西
   2. **不**拿"裸路径"那类形状去过滤（<see cref="InstallerOutput.LooksLikeListing"/>）：
      那只是"刷屏"的判据，不是"这句话没用"的判据。
      真出错时最后一行很可能就是一个路径（比如找不到的那个文件），
      滤掉它等于把唯一的线索扔了。
</summary>
```

```text
<summary>
这个加载器版本在本地叫什么目录名（没有 / 认不出来返回 null）。

为什么要容错：**同一个加载器版本在磁盘上至少有三种写法**
  · 我们自己的约定：`1.20.1-Forge_47.4.10`（照 PCL 的规则拼的）
  · Forge / NeoForge 安装器自己起的：`1.20.1-forge-47.4.10`
  · 别的启动器留下的各种大小写

只认第一种的话，"其实已经装好了"会被当成没装：
轻则白跑一遍要 3 分钟的安装器，重则**整包装不上** ——
整合包要拿这个加载器版本当父版本（`inheritsFrom`），找不到就写不了
（用户 2026-10-18 的 `Zombie Invade 100 Days` 就是这么坏的）。
</summary>
```

```text
   这种**没有游戏版本前缀**，上面的 `ParseVersionFolderName` 认不出
   （它按 `<mc>-<加载器名>…` 解，`neoforge-20.4.253` 会被解成
   mc=`neoforge`、加载器为空）。
   2026-10-18 用户实测：NeoForge 1.20.4 的安装器就起成 `neoforge-20.4.253`，
   于是装完了却报「装完了但版本不对：没有版本 JSON」。
   否则同一个加载器的**别的版本**目录也会被挑中，改名时就把人家覆盖了。
```

```text
   · `install_profile.json` → 打补丁那一步要用的库（89 条）
   · `version.json`        → 装完之后**启动**要用的库（48 条）
   两者**不完全重叠**。NeoForge 1.20.6 的 `cpw.mods:bootstraplauncher:2.0.2`
   只出现在 `version.json` 里 —— 2026-10-18 用户实测：它没被预下载，
   安装器自己去下又失败了，最后报「These libraries failed to download /
   cpw.mods:bootstraplauncher:2.0.2@jar」，而界面只显示成
   「NeoForge 的安装器失败了：cpw.mods:bootstraplauncher:2.0.2@jar」。
   （之前只读 install_profile，等于把"启动要用的库"留给安装器自己下 ——
     国内网络下这一步经常就卡死在这儿。）
```

```text
<summary>
实测的行数分布（NeoForge 1.20.1 一次安装）：
    7756  裸路径 / 类名
    1724  `net/minecraft/client/…`
    1385  `  Patching net/minecraft/…`
    1385  `Reading patch net.minecraft…`
     321  `Cant Find Class: com/…`
只写 `Patching ` 是不够的（那只能挡住 1385 行，剩下 3 万行照样糊到界面上）。
</summary>
```

```text
<summary>
还有一大类：**缩进两格的"一个条目一行"** —— 实测里 7756 行长这样：
      `  com/mojang/blaze3d/Blaze3D.class`
      `  net/minecraft/WorldlyContainer`
没有任何前缀能可靠地概括它们（前缀就是路径本身），所以按**形状**认：
去掉首尾空白之后，整行是"斜杠分隔的标识符/文件名"。
</summary>
```

## Mosslight.Core/Services/Engine/ModMetadata.cs

<a id="mosslight-core-services-engine-modmetadata-cs"></a>

```text
============================================================================
 从 mod jar 里读"这个 mod 叫什么 / 什么版本 / 给哪个加载器 / 谁写的 / 图标"

 1.0.0 的 `_refresh_mods` 里写过"到时候会在这里列出 mods 文件夹里每个 Mod 的
 名字、版本和图标"，但那边一直停在占位文字（`version_settings_page.py:777`）。
 2.0 把这件真做了 —— 名字 / 版本 / 加载器 / 作者 / 图标，**不用联网**。

 ## 认哪些元数据文件（命中就返回，顺序就是优先级）

     fabric.mod.json                              → Fabric
     quilt.mod.json                               → Quilt
     META-INF/neoforge.mods.toml                  → NeoForge
     META-INF/mods.toml                           → Forge
     mcmod.info（1.12 / 1.7 那套）                 → Forge（老写法）
     META-INF/MANIFEST.MF 的 Implementation-Title → 不知道加载器

 一个都读不到就把文件名去掉 `.jar` / `.disabled` 当名字 —— PCL / HMCL 也是
 这个兜底，总比列表里一片空白强。

 ## 图标从哪来

 先看元数据自己指的：fabric / quilt 的 `icon`（字符串或 `{"64": "…"}` 那种表）、
 `mods.toml` / `mcmod.info` 的 `logoFile`；都没有就按约定猜：
 `icon.png` → `logo.png` → `pack.png` → `assets/<modid>/icon.png`。
 全都没有就返回 null，界面拿名字首字母画个方块（**不画假图标**）。
 图标上限 512KB，只认第一张 —— 列表里是 32×32，用不着更大的。

 ## 两条硬约定

    独占锁住的 jar 都只是"读不出信息"，不能拖垮整页列表。
    所以整个 <see cref="Read"/> 的失败路径是 `catch` 完直接返回文件名。

    200 个 mod 的文件夹一次扫描 ~0.3 秒（实测 D:\开发工程\DHML-src\.minecraft
    里 23 个 mod 的 Fabric 版本）。
============================================================================
```

## Mosslight.Core/Services/Engine/ModVersionGroups.cs

<a id="mosslight-core-services-engine-modversiongroups-cs"></a>

```text
/// <summary>
/// 把一个项目的全部版本**分成组**（= 1.0.0 `mods_page.group_versions()` 的移植）。
///
/// ## 为什么非得分组
/// Fabric API 那种项目有**四百多个分组**（每个「游戏版本 × 加载器 × 正式/预览」一组）。
/// 平铺出来是一眼望不到底的列表，找东西只能靠滚；分组之后标题行就是目录，扫一眼就能定位。
/// 2.0 之前这里**只显示了"匹配你选的版本"那一组**，别的版本根本看不到 ——
/// 而"我要 1.12.2 那一版"恰恰是用户最常干的事。
///
/// ## 排序规则（照 1.0.0，一条都不能少）
///   1. **对得上筛选的排最前**（用户刚筛完就点进来，多半就想要这个）
///   2. 游戏版本新的在前（`1.21.5` 在 `1.20.1` 前面 —— 按**分段数字**比，不能按字符串）
///   3. 正式版在预览版前面（预览版混在正式版里，用户会点错）
///   4. 加载器按常用度（neoforge → fabric → forge → quilt → 其它）
///   5. 最后按版本号字符串兜底（保证同条件下顺序稳定）
///
///    用户没选加载器（「全部」）时，每个游戏版本的所有加载器分组都算对得上 ——
///    1.0.0 就是这样（`want_l` 为空时 `matched` 只看 `want_v`）。
/// </summary>
/// ⚠️ "对得上"只认**游戏版本**：加载器是"如果用户选了就也要对得上"。
```

```text
    /// <summary>
    /// 分组标题：`Fabric 1.20.4` / `Fabric 1.20.4 beta`。
    ///
    /// ⚠️ **详情页现在不按组显示了**（2026-10 用户要求照 PCL："全甩一个收缩标签里面"，
    ///    见 `ModsPage.ApplyDetailGroups`）。这个方法目前只有自检在用 —— 留着是因为
    ///    "组"这个概念（`Group` 的排序规则、匹配判定）还在用，标题格式也要有个既定说法。
    /// </summary>
    /// ⚠️「预览版」那几个中文字**不要**加回来（用户 2026-10：「直接用 snapshot 他原本的名字」）：
    ///    Modrinth 的 `version_type` 本来就是 `release` / `beta` / `alpha`，
    ///    翻译成「正式版 / 测试版 / 预览版」反而对不上号 —— 用户在外面（Modrinth 网站、
    ///    别人的截图）看到的都是英文那三个词。跟下载页那条规则一致：
    ///    那边的 `release` / `snapshot` 也是**不翻译**直接写的。
    ///
    /// ⚠️ 正式版那一组**不加后缀**（只有预览版才标），跟以前一样。
    /// ⚠️ 标题里**不写组内版本数**：`CollapsibleGroup` 会自动在标题后面接条目数，
    ///    这里再写一遍就重复了（"预览版 1 1"）。
    /// </summary>
```

## Mosslight.Core/Services/Engine/ModrinthApi.cs

<a id="mosslight-core-services-engine-modrinthapi-cs"></a>

```text
<summary>
搜索项目。

返回 `{hits: [...], total_hits, offset, limit}`。网络不通抛
<see cref="ModrinthException"/>。

facets 是"数组的数组"：内层是**或**、外层是**且**。
加载器只在 mod / modpack 上有意义 —— 资源包和光影包没有加载器一说，
硬塞 `categories:fabric` 会把结果**几乎全筛掉**，而且**不报错**
（2026-10 实测：资源包 36437 → 16、光影 983 → 5，剩下那几条只是
 恰好也被打了那个分类）。所以下面那个判断只对 mod / modpack 加这条 facet。
</summary>
```

```text
<summary>
某一类项目能用的**分类**（`/v2/tag/category`）。

⚠️ 只取 `header == "categories"` 的那一组：同一个接口里还混着
   `features`（资源包的 audio / gui / models …）和 `resolutions`
   （16x / 32x …）。那两组在 Modrinth 自己的界面上**不是"分类"**，
   混进同一个下拉里会让人以为分辨率和"冒险"是同一层的东西。
⚠️ 分类**必须按 project_type 取**：拿 mod 的分类去筛资源包会**静默返回 0 条**
   （`categories:technology` 对资源包是个不存在的组合）—— 跟 `loaders`
   那个坑是同一个，见 `ModContentType` 的类注释（实测 36437 → 16）。
⚠️ 返回的是接口原文（`vanilla-like` 这种），**翻译在界面层**：
   引擎不该知道这东西中文叫什么。
</summary>
```

```text
字符串倒序 = 时间倒序：Modrinth 给的是 ISO-8601 UTC
（"2024-03-05T12:00:00+00:00"），这种格式按字典序排就是按时间排
```

## Mosslight.Core/Services/Engine/VersionJson.cs

<a id="mosslight-core-services-engine-versionjson-cs"></a>

```text
<summary>
合并两个版本的库列表：**同一个 artifact 以后出现的为准**，位置保持不变。

顺序也保持：父版本在前，子版本覆盖的同名库**留在原位置**（官方启动器就是这么做的，
Forge 的 `-DignoreList` 依赖 classpath 顺序）。

⚠️ 去重的键是 `group:artifact:classifier`（**不含版本**），不是整串 name。
   这条 2026-10 才补：1.21.6 的原版 JSON 自带 `org.ow2.asm:asm:9.6`，
   而 Fabric loader 的 profile 带 `org.ow2.asm:asm:9.11` —— 两串 name 不同，
   只按整串 name 去重就**两条都进 classpath**，Fabric 的 ClasspathVerifier 直接抛
   `IllegalStateException: Duplicate ASM classes found on classpath`
   （用户实测：1.21.6-Fabric 一启动就崩）。子版本（加载器）要哪个版本就用哪个版本。

⚠️ 但 **classifier 必须算进键里**：`org.lwjgl:lwjgl:3.3.3` 和
   `org.lwjgl:lwjgl:3.3.3:natives-windows` 是两条都要留的（一个主 jar、
   一个 natives），按 group:artifact 去重会把 natives 顶掉。
</summary>
```

## Mosslight.Core/Services/Engine/VersionManifest.cs

<a id="mosslight-core-services-engine-versionmanifest-cs"></a>

```text
<summary>
清单地址。**元数据用官方优先**：实测官方 manifest 1.08 s、BMCLAPI 1.53 s，
而且 BMCLAPI 那个会间歇性慢到 5 s。
（下载*文件*那边是镜像优先，两边策略不同是有意的。）
</summary>
```

```text
   类型认不出来的条目会被界面静默丢掉，结果是"共 N 个版本"旁边
   只剩几条、预览版整组空白（2026-10 用户踩到的半空页面）。
   只要有干净的缓存就退回缓存 —— 宁可提示"这是缓存"，也不要摆一个坏列表。
```

```text
<summary>
清单里 `type` **不是那四种**的条目：type → 条数（空串记为 `&lt;空&gt;`）。

为什么要这个：界面上是"总 N 个版本"加上三个分组，分组只收认识的 type，
别的**静默丢弃** —— 于是"共 919 个版本"旁边可能只有 60 条能看、预览版整组是空的。
这种半空列表看着就像"下载页坏了"，而且不报错、没日志，根本无从查起
（2026-10 用户报到的那次就是这样：清单里 859 条的 type 认不出来）。
所以把它数出来：能回退缓存就回退，数不出来至少能在日志里看见。
</summary>
```

## Mosslight.Core/Services/Engine/VersionScanner.cs

<a id="mosslight-core-services-engine-versionscanner-cs"></a>

```text
============================================================================
 本地 Minecraft 版本扫描
 （移植自 1.0.0 的 core/versions.py）

 从 `.minecraft/versions` 读取所有已安装版本，供启动页的版本下拉框和启动逻辑使用。

 这里踩过的坑（都是被真实数据逼出来的）：
 1. 版本文件夹名 != 版本 id。以 `<文件夹名>.json` 里的 "id" 字段为准。
 2. `versions/` 底下会混进非版本的东西：.zip 压缩包、native-libraries 目录等。
    判据是"有没有一个带 id 的 json"，不是"是不是目录"。
 3. 文件夹名可能带空格、中文、全角字符，全部按 UTF-8 原样处理，不做转义。
 4. 单个版本坏掉不能影响其它版本，只记进 errors。
 5. 模组版本（Fabric/Forge）自己往往没有 jar，真正的 jar 在 inheritsFrom
    指向的父版本里，所以 jar 可能是 null，由启动器顺着 inheritsFrom 往上找。
 6. 整合包的 json 里 type 也写着 "release"、releaseTime 还很新，直接按类型+时间
    排序会让下拉框榜首全是整合包，所以必须分三段（见 SortKey）。
 7. **任何一个坏目录都不能拖垮整次扫描**：ACL 被改坏、被安全软件锁住、解压中断
    都会让 `Directory.Exists` / `EnumerateDirectories` 抛异常而不是返回 false。
    所以下面一律用 TryXxx 包一层。
 8. 不引用 WPF —— Core 层的约定（靠 .csproj 里没有 &lt;UseWPF&gt; 强制）。
============================================================================
```

```text
<summary>
把 `releaseTime` 转成时间戳（排序用）。

⚠️ 用 `DateTimeOffset.TryParse` 而不是 `DateTime.Parse`：releaseTime 带时区
   （`2023-06-07T12:00:00+00:00`），不认时区的话跨时区比出来的先后是错的。
   解析不出来就返回 0 —— 排序时它会被放到最后。
</summary>
```

## Mosslight.Core/Services/Models.cs

<a id="mosslight-core-services-models-cs"></a>

```text
<summary>
老实说这个加载器现在**能不能**由启动器自己装完。

Fabric / Quilt 走 profile JSON，一个 GET 就够；
Forge / NeoForge / OptiFine 要下安装器 jar 再跑一次 Java。
两种情况都实现了，这里返回的都是 true —— 留着是给界面判断
"要不要先跟用户说一声这步要几分钟"。
</summary>
```

## Mosslight.Core/Services/RealJavaService.cs

<a id="mosslight-core-services-realjavaservice-cs"></a>

```text
<summary>
重新扫一遍。

⚠️ **必须放到线程池里跑**：探测要起 java 进程（实测 12 个候选 5 秒多）。
   在 UI 线程上直接 await 一个同步实现 = 界面卡死 5 秒。
   这里用 Task.Run 包住，而且默认**用缓存**（第二次启动是毫秒级）。
</summary>
```

```text
   而这里从 JSON 文件的路径往回退：
     `.minecraft\versions\<id>\<id>.json`
       → 1 层：`.minecraft\versions\<id>`
       → 2 层：`.minecraft\versions`
       → 3 层：`.minecraft`          ← 要的是这个
   以前只退了两层（把 `versions` 当成游戏目录），于是
   `TryLoad` 去找 `versions\versions\<id>\<id>.json` —— 必然扑空，
   返回 null → `RequiredMajor` 退回 8 → **用 Java 8 去启动
   NeoForge 1.20.5**，报「Unrecognized option: -p」
   （NeoForge 的 -p 是模块路径，Java 9 才有）。
   2026-10-18 用户实测踩到（1.20.4 / 1.20.5 两个 NeoForge 实例都这样）。
```

## Mosslight.Core/Services/RealLaunchService.cs

<a id="mosslight-core-services-reallaunchservice-cs"></a>

```text
<summary>
启动器这一层的默认值（界面上「跟随全局设置」跟的就是它）。

放在这里是为了**只有一个地方**知道"默认值从哪来"：
以前这段是抄在 1.0.0 首页的 `_effective()` 里的，
版本设置页要用就得再抄一份，两份迟早不一致。
</summary>
```

```text
老实例自愈 + 统一成 PCL 那种"自包含"版本目录：Fabric / Quilt / Legacy Fabric
的实例目录里也放一份 `<实例名>.jar`（2026-10 用户要求："想要 PCL 装出来的那种效果"）。

   jar 可能是**打过补丁的**，塞一份原版 jar 反而会把它们顶掉。
   第一次启动时拷这一次（25~40 MB）；之后就是普通启动。
```

## Mosslight.Core/Services/RealLoaderService.cs

<a id="mosslight-core-services-realloaderservice-cs"></a>

```text
Legacy Fabric 同理（BMCLAPI 也没有它），走官方 meta；
实测返回的 profile 跟 Fabric 一个样（inheritsFrom / mainClass / libraries 都在）
```

```text
给实例补一份 `<实例名>.jar`，让它变成**自包含**的版本目录 ——
PCL 装出来的 Fabric 版本就长这样：`<目录>/<目录>.json` + `<目录>/<目录>.jar` + natives 齐全，
用户打开 versions 文件夹看到的每一版都是一个样子（2026-10 用户要求"类似 PCL 那种效果"）。

   `versions/<mc>/<mc>.jar` 也能启动（官方启动器就是这样，实例目录里只有 json）。
   补齐的好处是父版本被删/改名之后这个实例还能单独跑；代价是每个实例多 ~25-40 MB。
   不能被覆盖）；父版本 jar 不在 → 安静返回 false（照样能启动，classpath 去父版本找）。
```

```text
   试过一版"发现了就改名"，结果：用户自己装的 `1.20.1-forge-47.4.10`
   被我们改成了 `1.20.1-Forge_47.4.10` —— 而整合包收尾只会删
   "装之前快照里没有的"目录，改名后的那个不在快照里，于是被当成
   自己的中间产物**删掉了**（自检里两条清理断言当场变红）。
   别人的目录，名字难看就难看，别动。
```

```text
   而那一步在国内网络下经常失败（用户 2026-10-18 就卡在这儿：
   `cpw.mods:bootstraplauncher:2.0.2@jar` 没预下载成功，安装器自己下也失败）。
   这里不判死刑（安装器可能还是能下到），但必须把"没下全"说出来。
```

```text
安装器的完整输出写到这里（窗口里只显示过滤后的几句 —— 用户报的
"NeoForge 的安装器失败了：net.neoforged:JarJarFileSystems:0.4.0@jar"
就是只看到最后一句，真正的原因在它前几行）
```

```text
   （比如整合包实例目录），直接认目录名会让后面的校验去查一个根本没有
   JSON 的地方，结果明明是装好了却报「装完了但版本不对」（2026-10-18 踩到）。
```

```text
<summary>给安装器挑个 Java。</summary>

⚠️ 要求的最低版本**跟着游戏版本走**：1.20.1 的 Forge 安装器要 Java 17
   才跑得动，拿 Java 8 去跑会 `UnsupportedClassVersionError`。
   但也不能低于 8（安装器自己编译目标是 8）。

⚠️ **OptiFine 是例外，而且方向相反**：它那个安装器是 Java 8 编的，
   在 Java 17 上会**卡住不返回**（实测：10 分钟一行输出都没有）。
   所以 OptiFine 一律优先 Java 8，哪怕游戏本身要 17 ——
   安装器只是拿来打补丁的，跟游戏跑在哪个 Java 上无关。
```

```text
<summary>
从 JSON 里读一个整数（**宽进**：Int32 / Int64 / 字符串都认）。

⚠️ 为什么不能直接 `GetValue&lt;long&gt;()`：`System.Text.Json` 对 `0` 这种
   字面量给的是 Int32 的 JsonValue，强读成 Int64 会抛
   "A value of type 'System.Int32' cannot be converted to a 'System.Int64'"。
   这个异常还包着一层装加载器失败的壳，看不出是 size 的问题 —— 真踩过一次。
</summary>
```

## Mosslight.Core/Services/RealModpackService.cs

<a id="mosslight-core-services-realmodpackservice-cs"></a>

```text
<summary>
装完之后该删掉哪些"中间版本"（纯函数，好测）。

装整包必须先装原版和加载器（Forge 的安装器只认自己的目录），
但压平 + 拷 jar 之后它们就**只是中间产物**了 —— 实例自己带着 JSON 和 jar，
库和资源在游戏目录里（`libraries/` `assets/`）不跟着版本走。
留着它们的话 `versions/` 里就是"原版 + 加载器 + 整合包"三份文件夹，
而用户要的是**一份**（2026-10-18 的原话：全部装一起，不要分版本）。

⚠️ 两个 `...IsOurs` 的含义是"**这一份可以删**"，由调用方判断：
   · 这次才装出来的 → 是我们的；
   · 上次没装完、带着 `.mosslight-cache` 记号的半成品 → 也是我们的（顺手清掉）；
   · 用户自己装来玩的原版 / 加载器版本 → **不是**我们的，绝不动。
</summary>
```

```text
<summary>
这个版本能不能当实例的父版本（= 能不能真的启动）。

判据跟装加载器那边**同一份**（<see cref="LoaderInstaller.VerifyInstalled"/>）：
目录里有 JSON，且 JSON 自己有主类、或者它继承的父版本在本地。
别在这里另写一套判断 —— 两边标准不一致就会出现"装的时候说好了、启动时说不行"。
</summary>
```

## Mosslight.Core/Services/RealVersionSettingsService.cs

<a id="mosslight-core-services-realversionsettingsservice-cs"></a>

```text
============================================================================
 按版本覆盖启动参数的**服务包装**

 真正的逻辑全在 <see cref="Engine.VersionSettings"/> 里（三态、类型纠正、
 原子写、min/max 互夹，都逐值验过）。这一层只做两件事：

   1. 把 `LauncherDefaults()` 从"启动器默认值从哪来"收成**一处** ——
      以前这段是抄在首页的 `_effective()` 里的，版本设置页要用就得再抄一份，
      两份迟早不一致（1.0.0 就是这么踩的）。
   2. 给界面一个接口，别让它直接 new 引擎类。
============================================================================
```
