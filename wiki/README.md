# 发布到 GitHub Wiki

GitHub 的 Wiki 是**独立仓库**：`https://github.com/kongxia114/Mosslight-Launcher-2.wiki.git`。
只有当仓库 **Settings → Features → Wikis 勾选**、并且在 Wiki 页面点过一次「Create the first page」之后，
那个仓库才会存在（当前它还不存在，所以本目录是源文件，先在仓库里备份）。

`Home.md` = Wiki 首页，其余文件名就是页面标题（中文文件名在 Wiki 里会显示成中文标题）。

## 首次发布（手动，一次就够）

1. 打开 <https://github.com/kongxia114/Mosslight-Launcher-2/settings>，勾上 **Wikis**；
2. 打开 <https://github.com/kongxia114/Mosslight-Launcher-2/wiki>，点 **Create the first page**，
   随便存一页（内容一会儿会被覆盖）；
3. 在本地执行：

```powershell
cd D:\开发工程\Mosslight-Launcher 2.0\Mosslight-Launcher 2.0
git clone https://github.com/kongxia114/Mosslight-Launcher-2.wiki.git ..\ml-wiki
Copy-Item wiki\*.md ..\ml-wiki\ -Force
Remove-Item ..\ml-wiki\README.md -ErrorAction SilentlyContinue   # 这个是给仓库看的说明，不发布
cd ..\ml-wiki
git add -A
git commit -m "docs: sync wiki from repo"
git push
```

## 之后每次同步

改的是仓库里的 `wiki\*.md`，然后重复上面第 3 步的 `Copy-Item` + commit + push 即可。

也可以让 CI 代劳（需要给仓库配一个有 wiki 写权限的 token，或者用 `GITHUB_TOKEN` 配合
`repository_dispatch`）——目前没做，因为 Wiki 一旦启用，手动同步一次只要十秒。

## 约定

- 页面之间用**相对链接**（`安装与运行.md`），Wiki 渲染器认；
- 指回源码用**绝对 GitHub URL**，不要用 `../`（Wiki 仓库在另一条路径下）；
- 图片放仓库的 `docs/` 或 `wiki/images/`，Wiki 里用绝对 raw URL 引用；
- 改 README 时顺手检查：README 里链到 `wiki/xxx.md` 的锚点是否还对得上。
