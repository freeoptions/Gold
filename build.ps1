$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /debug- /platform:anycpu /win32manifest:"$PSScriptRoot\app.manifest" /out:"$PSScriptRoot\gold.exe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\GoldTicker.cs"
if ($LASTEXITCODE -ne 0) { throw 'Release 编译失败' }
