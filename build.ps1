#requires -version 5
# Compiles the wrapper and ffprobe pass-through with the in-box .NET Framework C#
# compiler (no SDK needed). Outputs .\out\ffmpeg.exe and .\out\ffprobe.exe.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework) not found" }

$ffmpegOut  = Join-Path $out  'ffmpeg.exe'
$ffprobeOut = Join-Path $out  'ffprobe.exe'
$ffmpegSrc  = Join-Path $root 'src\wrapper.cs'
$ffprobeSrc = Join-Path $root 'src\ffprobe-wrap.cs'

& $csc /nologo /optimize+ /target:exe /platform:x64 "/out:$ffmpegOut" "$ffmpegSrc"
if ($LASTEXITCODE) { throw "ffmpeg.exe build failed ($LASTEXITCODE)" }

# ffprobe wrapper needs System.Web.Extensions (JavaScriptSerializer) to patch JSON.
& $csc /nologo /optimize+ /target:exe /platform:x64 /r:System.Web.Extensions.dll "/out:$ffprobeOut" "$ffprobeSrc"
if ($LASTEXITCODE) { throw "ffprobe.exe build failed ($LASTEXITCODE)" }

Write-Host "Built:"
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize
