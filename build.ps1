$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (!(Test-Path -LiteralPath "$PSScriptRoot\assets\app.ico")) {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\make-icon.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
}
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/out:$output\Discord Emoji Downloader.exe" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\assets\app.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll "$PSScriptRoot\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Path (Join-Path $output 'Downloaded-Emojis') -Force | Out-Null
Write-Host 'Built build\Discord Emoji Downloader.exe'
