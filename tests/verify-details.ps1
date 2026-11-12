$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$projectDir = Split-Path -Parent $PSScriptRoot
$verificationDir = Join-Path $projectDir 'work\verification'
New-Item -ItemType Directory -Path $verificationDir -Force | Out-Null
$verificationExe = Join-Path $verificationDir 'DetailsVerification.exe'
& $compiler /nologo /target:winexe /optimize+ /debug- /main:GoldTicker.DetailsVerification /win32manifest:"$projectDir\app.manifest" /out:$verificationExe /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectDir 'GoldTicker.cs') (Join-Path $PSScriptRoot 'DetailsVerification.cs')
if ($LASTEXITCODE -ne 0) { throw 'Verification build failed' }
$reportPath = Join-Path $verificationDir 'details.txt'
$testProcess = Start-Process -FilePath $verificationExe -ArgumentList @(('"' + $reportPath + '"')) -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath $reportPath -Encoding UTF8
if ($testProcess.ExitCode -ne 0) { throw 'Verification failed' }
