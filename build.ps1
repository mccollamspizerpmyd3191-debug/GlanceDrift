$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$iconPath = Join-Path $PSScriptRoot 'BorrowScreen.ico'
$exePath = Join-Path $PSScriptRoot 'BorrowScreen.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Microsoft C++ Build Tools with Windows SDK are required.' }
$setup = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$buildDir = Join-Path ([IO.Path]::GetTempPath()) 'BorrowScreen-build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
Push-Location $buildDir
try {
    $nativeBuild = 'call "{0}" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /LD /utf-8 /DNOMINMAX "{1}\PixelCapture.cpp" /link /out:"{1}\PixelCapture.dll" /implib:PixelCapture.lib d3d11.lib windowsapp.lib user32.lib' -f $setup,$PSScriptRoot
    & cmd.exe /d /c $nativeBuild
    if ($LASTEXITCODE -ne 0) { throw 'Native capture build failed.' }
} finally { Pop-Location }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/win32icon:$iconPath" "/out:$exePath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'BorrowScreen.cs') (Join-Path $PSScriptRoot 'Settings.cs')
if ($LASTEXITCODE -ne 0) { throw "C# compiler failed ($LASTEXITCODE)" }
Get-Item -LiteralPath $exePath
