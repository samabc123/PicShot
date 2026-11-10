param(
    [Parameter(Mandatory = $true)][string]$WorkPath,
    [Parameter(Mandatory = $true)][string]$TargetPath,
    [int]$ParentProcessId
)

$ErrorActionPreference = 'Stop'
function Resolve-UpdateRoot([string]$Value) {
    # Older clients pass "D:" for an app installed at the drive root.
    if ($Value -match '^[a-zA-Z]:$') { $Value += '\' }
    $full = [IO.Path]::GetFullPath($Value)
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full.TrimEnd('\') -eq $root) { return $root + '\' }
    return $full.TrimEnd('\')
}

$workRoot = Resolve-UpdateRoot $WorkPath
$targetRoot = Resolve-UpdateRoot $TargetPath
$cacheRoot = Resolve-UpdateRoot (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FrameDock\Updates')
$sourceRoot = Join-Path $workRoot 'contents\app'
$exePath = Join-Path $targetRoot 'PicShot.exe'
$backupRoot = Join-Path $workRoot 'backup'
$changed = New-Object 'Collections.Generic.List[object]'

function Assert-UnderRoot([string]$PathToCheck, [string]$Root) {
    $path = [IO.Path]::GetFullPath($PathToCheck)
    $prefix = $Root.TrimEnd('\') + '\'
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update path is outside the allowed directory.'
    }
}

function Copy-File([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
    [IO.File]::Copy($Source, $Destination, $true)
}

try {
    Assert-UnderRoot $workRoot $cacheRoot
    Assert-UnderRoot $sourceRoot $workRoot
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'PicShot.exe'))) {
        throw 'Update package is incomplete.'
    }
    if ($ParentProcessId -gt 0) {
        $parent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
        if ($parent -and -not $parent.WaitForExit(30000)) {
            throw 'PicShot did not exit in time.'
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        if ($relative -match '^(data|test-data)(\\|$)') {
            throw 'Update package cannot contain user data.'
        }
        $destination = Join-Path $targetRoot $relative
        Assert-UnderRoot $file.FullName $sourceRoot
        Assert-UnderRoot $destination $targetRoot
        $backup = Join-Path $backupRoot $relative
        $existed = Test-Path -LiteralPath $destination
        if ($existed) {
            Copy-File $destination $backup
        }
        $changed.Add([PSCustomObject]@{
            Destination = $destination
            Backup = $backup
            Existed = $existed
        })
        Copy-File $file.FullName $destination
    }

    Start-Process -FilePath $exePath -WorkingDirectory $targetRoot -WindowStyle Hidden
}
catch {
    foreach ($file in $changed) {
        try {
            Assert-UnderRoot $file.Destination $targetRoot
            if ($file.Existed) {
                Copy-File $file.Backup $file.Destination
            } elseif (Test-Path -LiteralPath $file.Destination) {
                Remove-Item -LiteralPath $file.Destination -Force
            }
        } catch { }
    }
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $targetRoot -WindowStyle Hidden
    }
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show(
        ('Update failed: ' + $_.Exception.Message),
        'PicShot Update',
        'OK',
        'Warning') | Out-Null
    exit 1
}

# Cleanup must never roll back files after the updated app has already started.
try {
    Assert-UnderRoot $workRoot $cacheRoot
    Remove-Item -LiteralPath $workRoot -Recurse -Force
} catch {
    Write-Warning ('Update installed; temporary files could not be removed: ' + $_.Exception.Message)
}
