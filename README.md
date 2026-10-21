# Mosslight Launcher 2

Mosslight Launcher 的 **2.0 重写版**：界面和核心逻辑从 Python / PyQt6 换成了 **C# / WPF**，
但**设计、配色、文案、启动参数拼装全部对着 1.0.0 逐条复刻**，启动引擎还拿 Python 版当金标准逐值比对过。

- 1.0.0（风格与逻辑的权威源）：<https://github.com/kongxia114/Mosslight-Launcher>
- 2.0 源码：[`Mosslight.Core/`](Mosslight.Core/) · [`Mosslight.App/`](Mosslight.App/) · 变更记录：[`CHANGELOG.md`](CHANGELOG.md)

---

## 下载

**只有一个包，约 1.4 MB**：`Mosslight-2.0-<tag>-win64.zip`（[Releases](https://github.com/kongxia114/Mosslight-Launcher-2/releases) 页底 Assets）。

| 大小 | 运行前提 |
|---|---|
| zip 约 1.4 MB / exe 2.0 MB | 机器上要有 **.NET 10 桌面运行时（x64）** |

解压出来只有一个 `Mosslight.exe`，双击即可；5 种语言的数据编在 exe 里，不用带别的文件。
自动更新每次也只下这 1.4 MB。

装运行时报错的话看这里：[安装与运行](wiki/安装与运行.md)。

---

## 跑起来

```powershell
dotnet build "Mosslight-Launcher 2.0.slnx" -m:1
.\Mosslight.App\bin\Debug\net10.0-windows\win-x64\Mosslight.exe

# 自检（跟 1.0.0 的金标准逐值比对）
.\Mosslight.Core.SelfCheck\bin\Debug\net10.0-windows\Mosslight.Core.SelfCheck.exe tests\theme_golden.json
```

更细的构建 / 自检 / 截图命令见 [开发与构建](wiki/开发与构建.md)。

---

## 功能一览

| 模块 | 状态 |
|---|---|
| 启动引擎（版本扫描 / 命令行拼装 / Java 探测 / natives / 下载 / 加载器安装 / 整合包 / Modrinth） | ✅ 全部接真实现 |
| 界面（启动、下载、个性化、账户、设置、版本管理、版本设置、模组、社区资源、收藏夹、日志窗口） | ✅ 5 种语言 |
| 账户 | ✅ 离线档案 · **LittleSkin 网页授权 / 账号密码登录** · **自建 Yggdrasil** · 多角色切换 · 皮肤头像 |
| 正版（微软）登录 | ⛔ 未接入（Minecraft Services 要 Xbox 开发者计划，个人开发者拿不到） |
| 自动更新 | ✅ 单文件替换 + SHA256 校验 + 强制更新（tag 带 `-force`） |

第三方登录怎么用、支持哪些服务器：[第三方登录](wiki/第三方登录.md)。

---

## 文档

| 想了解 | 去哪看 |
|---|---|
| 安装、运行时、配置放哪、更新机制 | [安装与运行](wiki/安装与运行.md) |
| 双击没反应 / 缺 .NET / 更新失败 / 头像不对 | [常见问题](wiki/常见问题.md) |
| 工程结构、构建与自检、金标准来历、约定 | [开发与构建](wiki/开发与构建.md) |
| 发版流程、workflow 里的坑 | [发版流程](wiki/发版流程.md) |
| LittleSkin / 自建皮肤站登录 | [第三方登录](wiki/第三方登录.md) |

> 这些页面同时有 GitHub Wiki 版本（`/wiki`）。Wiki 没启用时，仓库里 `wiki/` 目录的内容是一样的。

---

## 发版

```powershell
# 先把这一版改动写进 CHANGELOG.md 的「## 未发布」
git tag 2.0.0
git push origin 2.0.0
```

CI 会自动构建、自检、打**一个** zip、生成 Release 正文。细节和五个容易踩的点见 [发版流程](wiki/发版流程.md)。

---

## 跟 1.0.0 的配置兼容吗

键名逐字对齐（`theme` / `accent_color` / `card_opacity` / `anim_*` …），两边都遵守「只动认识的键」。
区别只有位置：1.0.0 在 `%APPDATA%\Mosslight\`，2.0 在 `<启动器目录>\Mosslight\`，**第一次启动会自动迁移**。
细节见 [安装与运行](wiki/安装与运行.md#配置放哪)。

---

Free code signing provided by SignPath.io, certificate by SignPath Foundation.
