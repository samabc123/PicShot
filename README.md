# PicShot

PicShot 是一个面向 Windows 的图片工作台，参考了 PicRope 的截图收集和桌面贴图思路，但把图片资源管理改成了“原始路径索引 + 可选本地副本”。

## 解决的问题

- 图片可以从任意文件夹导入，不依赖 Windows 的 Screenshots 文件夹。
- 添加文件夹后会持续监听新图片，重命名会更新记录，删除后会显示离线状态。
- 导入外部图片时默认保存本地副本。原始文件移动或删除后，PicShot 仍可从副本读取。
- 图片记录保存在 `%LocalAppData%\FrameDock\data\state.json`，重启后恢复。
- 截图、剪贴板图片和文件夹导入进入同一个资源库。

## 构建和运行

需要 Windows 10/11 与 .NET Framework 4.8：

```powershell
.\build.ps1
.\build\FrameDock.exe
```

构建产物位于 `build\FrameDock.exe`。核心路径规则测试：

```powershell
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @("/r:$frameworkPath\System.dll", "/r:$frameworkPath\System.Core.dll")
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /out:build\CoreTests.exe $refs src\Core.cs tests\CoreTests.cs
& .\build\CoreTests.exe
```

## 当前版本

这是一版可运行的基础版本，已经覆盖截图、导入、目录监听、可靠存储、缩略图、复制、打开、定位、另存和置顶预览。标注编辑、文字收集、全局快捷键和多显示器截图还可以在此资源库模型上继续补充。

本项目受 PicRope 的产品思路启发。PicRope 原项目及其许可信息请以原仓库为准。
