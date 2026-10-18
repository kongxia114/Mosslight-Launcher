' 致敬 PCL2 (Plain Craft Launcher)，本项目参考了其部分实现思路。
' 本文件仅作历史纪念，采用纯代码方式构建一个极简的 WPF 窗口，不参与主项目的编译。
' 如果你安装了 .NET SDK，可以在终端使用 `dotnet run` 运行此文件（需建临时 vbproj）。

Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Public Class LegacyWindow
    Inherits Window

    Public Sub New()
        Me.Title = "Mosslight Legacy (VB.NET Tribute)"
        Me.Width = 400
        Me.Height = 200
        Me.WindowStartupLocation = WindowStartupLocation.CenterScreen
        Me.Background = New SolidColorBrush(Color.FromRgb(30, 30, 30)) ' 极简深色背景

        ' 创建一个居中的文本
        Dim textBlock As New TextBlock()
        textBlock.Text = "Hello from VB.NET!" & vbCrLf & "致敬 PCL2 (Plain Craft Launcher)"
        textBlock.Foreground = Brushes.White
        textBlock.FontSize = 18
        textBlock.TextAlignment = TextAlignment.Center
        textBlock.VerticalAlignment = VerticalAlignment.Center
        textBlock.HorizontalAlignment = HorizontalAlignment.Center

        Me.Content = textBlock
    End Sub

    ' 独立启动入口（如果脱离主项目单独编译的话）
    <STAThread>
    Public Shared Sub Main()
        Dim app As New Application()
        app.Run(New LegacyWindow())
    End Sub
End Class
