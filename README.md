# 🌿 Mosslight Launcher 2

**用 C# / WPF 重写的 Minecraft 启动器**

[![Release](https://img.shields.io/github/v/release/kongxia114/Mosslight-Launcher-2?color=007ec6&include_prereleases)](https://github.com/kongxia114/Mosslight-Launcher-2/releases)
[![License](https://img.shields.io/badge/license-MIT-fe7d37)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![WPF](https://img.shields.io/badge/WPF-Windows-17a2b8)](#下载)
[![Size](https://img.shields.io/badge/zip-1.4%20MB-5ec269)](#下载)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://afdian.com/a/Mosslight_Launcher)

[功能](#功能) · [下载](#下载) · [本地构建](#本地构建) · [第三方登录](#第三方登录) · [常见问题](#常见问题) · [用户协议](EULA.md) ·

</div>

> ⚠️ **使用须知**：使用本启动器即代表您同意 [《EULA 用户协议与免责声明》](EULA.md)。本启动器与 Mojang、微软及网易无任何隶属关系。

---

自己用的启动器，顺手开源。2.0 的目标只有一个：**同样的界面和手感，换成更快、更省内存的技术栈** —— 装原版、装加载器、装整合包和模组，然后直接开游戏，都还在一个窗口里。

## 功能

|  |  |
|---|---|
| 🚀 **启动游戏** | 按版本要求自动挑 Java（8 / 17 / 21…）、按物理内存给推荐值、解压 natives、拼启动命令。**运行中按钮变红**，点一下强制结束整个进程树 |
| ⬇️ **下载原版** | 版本清单走 BMCLAPI 镜像；多线程、失败重试、镜像连续抽风就熔断切官方源；已下好的文件认 sha1 跳过 |
| 🧩 **一键装加载器** | Forge / NeoForge / Fabric / Quilt / OptiFine / LiteLoader。Forge 那几家要跑官方安装器 —— 启动器会先把安装器要的库下好 |
| 📦 **一键装整合包** | Modrinth `.mrpack` 和拖进窗口的 zip：解包 → 装原版 → 装加载器 → 压平成**一个版本文件夹** → 中间产物用完就删 |
| 🔍 **社区资源** | 模组 / 整合包 / 数据包 / 资源包 / 光影包，可按游戏版本 + 加载器 + 分类筛；滚到底自动续 |
| 👤 **账户** | 离线档案、**LittleSkin 网页授权 / 账号密码登录**、**自建 Yggdrasil**、一个账号多角色随时切、皮肤头像自动获取 |
| 🎨 **外观** | 深色 / 浅色 / 跟随系统，自定义强调色，背景图五种铺法 + 压暗，卡片透明度 |
| ✨ **入场动效** | 7 档预设（含复刻 PCL2 的那一档）× 5 档速度；**能整个关掉**（不是把时长设成 0，是那些开销根本不创建） |
| 🌏 **多语言** | 简体中文 / 繁體中文 / English / 日本語 / Русский，切换立刻生效 |
| 🔄 **自动更新** | 单文件替换、SHA256 校验、失败留日志；强制更新（tag 带 `-force`）关不掉窗 |

## 下载

去 [Releases](https://github.com/kongxia114/Mosslight-Launcher-2/releases) 拿最新版：

| 包 | 说明 |
|---|---|
| `Mosslight-2.0-<版本>-win64.zip` | **只有一个**，约 **1.4 MB**。解压出来就一个 `Mosslight.exe`，双击即用 |

**运行前提**：机器上要有 [**.NET 10 桌面运行时（x64）**](https://dotnet.microsoft.com/download/dotnet/10.0) —— 注意**不是** SDK，也**不是** ASP.NET Core Runtime。

**为什么这么小**：这是框架依赖发布，好处是更新一次只下 1.4 MB、几秒钟的事；代价是第一次要先装一次运行时，之后每个版本都只下这么点。

配置默认在 `<启动器目录>\Mosslight\`，窗口大小 / 语言这类状态记在 `HKCU\Software\Mosslight`。整个文件夹拷走就是完整迁移。

## 本地构建

如果你想自己编译，需要 **.NET 10 SDK**：

```powershell
git clone https://github.com/kongxia114/Mosslight-Launcher-2.git
cd "Mosslight-Launcher 2.0"
dotnet build "Mosslight-Launcher 2.0.slnx" -m:1

# 界面
.\Mosslight.App\bin\Debug\net10.0-windows\win-x64\Mosslight.exe
```

游戏目录默认找 `%APPDATA%\.minecraft`，装在别的盘就在「设置 → 游戏目录」里填。

**界面这块一定在本地跑**：改一行 XAML 等 CI 四分钟再下载解压，是调不动的。

## 第三方登录

新建档案 → 第三方验证，两条路：

| 方式 | 适用 |
|---|---|
| 账号密码 | LittleSkin + 任何标准 Yggdrasil 服务器 |
| 网页授权（OAuth 2.0） | LittleSkin |

登录后角色自动入库，多个角色会先让你选一个默认；启动时自动续期 token、自动准备 `authlib-injector`（缺了会自己下），皮肤头像也会跟着拉下来。

## 常见问题

<details>
<summary><b>双击没反应 / 提示缺少 .NET</b></summary>

装 [.NET Desktop Runtime 10.0（x64）](https://dotnet.microsoft.com/download/dotnet/10.0) 就好。先看 `Logs\launcher.log` 最后几行，启动期的错误都写在那儿。
</details>

<details>
<summary><b>杀毒软件报毒 / 文件被删</b></summary>

单个未签名的 exe 被启发式引擎拦很常见。把启动器目录加入白名单即可；本项目只从 GitHub Releases 下载资产，每个包都校验 SHA256。
</details>

<details>
<summary><b>自动更新失败怎么办</b></summary>

看 `Logs\update.log` 和 `Logs\apply_update.log`；实在不行手动下 zip 覆盖 `Mosslight.exe`，配置和账户都还在。
</details>

<details>
<summary><b>正版（微软）登录为什么没有</b></summary>

Azure 应用注册免费，但 Minecraft Services 会回 `403 Invalid app registration`，`XboxLive.signin` 要加入 Xbox / ID@Xbox 开发者计划。界面上如实禁用了那一项。
</details>
```
