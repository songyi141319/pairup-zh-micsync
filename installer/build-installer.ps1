<#
.SYNOPSIS
    Build the Chinese Windows x64 installer with Inno Setup 6.7.3 or later.
.DESCRIPTION
    By default, publish a self-contained application first. Use -BinaryPath to
    package an existing self-contained PairUp.App.exe without changing it.
    Building never launches PairUp or runs the generated installer.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([.-][0-9A-Za-z]+)*$')]
    [string]$Version = '0.4.0-preview.3',
    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$')]
    [string]$WindowsVersion = '0.4.0.3',
    [string]$BinaryPath,
    [string]$WebRoot,
    [string]$ExpectedBinarySha256,
    [string]$IsccPath,
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\PairUp.App\PairUp.App.csproj"
$issPath = Join-Path $PSScriptRoot "PairUp.iss"

foreach ($part in $WindowsVersion.Split('.')) {
    if ([int]$part -gt 65535) { throw 'WindowsVersion components must be at most 65535.' }
}
if ($ExpectedBinarySha256 -and $ExpectedBinarySha256 -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'ExpectedBinarySha256 must contain exactly 64 hexadecimal characters.'
}

if (-not $IsccPath) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $IsccPath) {
        $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Install it or pass -IsccPath explicitly.'
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path

if (-not $BinaryPath) {
    $publishDir = Join-Path $root 'publish'
    Write-Host "Publishing PairUp $Version (self-contained win-x64)..."
    & $DotNetPath publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $BinaryPath = Join-Path $publishDir 'PairUp.App.exe'
}
if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) {
    throw "Application binary not found: $BinaryPath"
}
$BinaryPath = (Resolve-Path -LiteralPath $BinaryPath).Path
if (-not $WebRoot) { $WebRoot = Join-Path (Split-Path -Parent $BinaryPath) 'Web' }
if (-not (Test-Path -LiteralPath (Join-Path $WebRoot 'index.html') -PathType Leaf)) {
    throw 'The matching published Web folder is required beside the application, or pass -WebRoot explicitly.'
}
$WebRoot = (Resolve-Path -LiteralPath $WebRoot).Path
$binaryHash = (Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash
if ($ExpectedBinarySha256 -and $binaryHash -ne $ExpectedBinarySha256) {
    throw 'Application checksum does not match ExpectedBinarySha256.'
}

Write-Host "Compiling the Chinese installer with $IsccPath..."
& $IsccPath "/DMyAppVersion=$Version" "/DWindowsVersion=$WindowsVersion" "/DAppBinary=$BinaryPath" "/DAppWebRoot=$WebRoot" $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$output = Join-Path $PSScriptRoot "Output\Shengyu-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Installer output was not created.' }
[pscustomobject]@{
    Installer = $output
    InstallerSHA256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    ApplicationSHA256 = $binaryHash
}
