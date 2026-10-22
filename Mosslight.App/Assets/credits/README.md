# 关于 / 特别鸣谢 的头像

这里放 `Views/Credits.cs` 里引用到的头像图（PNG），按文件名对上就行：

```csharp
new CreditEntry(Name: "某某", Description: "…", Avatar: "someone.png", ButtonLabel: "打开主页", Url: "https://…")
```

- **格式**：正方形 PNG，建议 ≥ 128×128（界面上按 36×36 圆形裁，给大一点以后改尺寸不糊）；
- **缺图不报错**：没放图的条目会画一个圆底 + 名字首字母（见 `AboutDialog.BuildAvatar`），
  所以名单可以先加、图后补；
- **打包方式**：`Mosslight.App.csproj` 里 `<Resource Include="Assets\credits\*.png" />`，
  XAML/代码用 `pack://application:,,,/Assets/credits/xxx.png` 取。

名单现在**是空的**（`Credits.About` 只有"本软件"那一条，`Credits.Thanks` 是空表），
填的时候只改 `Credits.cs` 那张表，界面不用动。
