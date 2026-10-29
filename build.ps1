$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$buildPath = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null

$references = @(
    "/r:$frameworkPath\WPF\PresentationCore.dll",
    "/r:$frameworkPath\WPF\PresentationFramework.dll",
    "/r:$frameworkPath\WPF\WindowsBase.dll",
    "/r:$frameworkPath\System.Xaml.dll",
    "/r:$frameworkPath\System.Web.Extensions.dll",
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

$sources = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs').FullName
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$buildPath\FrameDock.exe" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'FrameDock 构建失败' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath 'FrameDock.exe.config')
Write-Output ('构建完成：' + (Join-Path $buildPath 'FrameDock.exe'))
