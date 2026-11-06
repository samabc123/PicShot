param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$buildPath = Join-Path $projectRoot 'build'
$generatedPath = Join-Path $buildPath 'generated'
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
New-Item -ItemType Directory -Path $generatedPath -Force | Out-Null

$versionSource = Join-Path $generatedPath 'AppVersion.g.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText(
    $versionSource,
    "[assembly: System.Reflection.AssemblyVersion(`"$Version.0`")]`r`n" +
    "[assembly: System.Reflection.AssemblyFileVersion(`"$Version.0`")]`r`n" +
    "[assembly: System.Reflection.AssemblyInformationalVersion(`"$Version`")]`r`n",
    $utf8)

$references = @(
    "/r:$frameworkPath\WPF\PresentationCore.dll",
    "/r:$frameworkPath\WPF\PresentationFramework.dll",
    "/r:$frameworkPath\WPF\WindowsBase.dll",
    "/r:$frameworkPath\System.Xaml.dll",
    "/r:$frameworkPath\System.Web.Extensions.dll",
    "/r:$frameworkPath\System.Net.Http.dll",
    "/r:$frameworkPath\System.IO.Compression.dll",
    "/r:$frameworkPath\System.IO.Compression.FileSystem.dll",
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

$sources = @(
    (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs').FullName
    $versionSource
)
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$buildPath\PicShot.exe" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'PicShot 构建失败' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath 'PicShot.exe.config')
$assetSource = Join-Path $projectRoot 'assets'
$assetDestination = Join-Path $buildPath 'assets'
if (Test-Path -LiteralPath $assetSource) {
    New-Item -ItemType Directory -Path $assetDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $assetSource 'ground-stake-anchor.png') -Destination $assetDestination -Force
}
Write-Output ('构建完成：' + (Join-Path $buildPath 'PicShot.exe'))
