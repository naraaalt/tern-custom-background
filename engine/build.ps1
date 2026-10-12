# Compiles engine/Dither.cs into engine/dither.exe with the C# compiler that
# ships with Windows, so the plugin needs nothing installed.
#
#   powershell -ExecutionPolicy Bypass -File engine/build.ps1
#
# Run it again after editing Dither.cs. The plugin runs it automatically when
# dither.exe is missing.

$ErrorActionPreference = "Stop"

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir "Dither.cs"
$out = Join-Path $dir "dither.exe"
$stamp = Join-Path $dir "dither.stamp"

$csc = @(
	"$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
	"$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $csc) {
	Write-Error "No .NET Framework C# compiler found. Install .NET Framework 4.x, or build Dither.cs with any C# compiler and place dither.exe beside it."
	exit 1
}

if (-not (Test-Path $src)) {
	Write-Error "Missing $src"
	exit 1
}

& $csc -nologo -optimize -target:exe -out:$out -r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) {
	Write-Error "csc failed with exit code $LASTEXITCODE"
	exit $LASTEXITCODE
}

# A hash of the source this binary was built from, beside it. The plugin puts
# it in the render's inputs, so a rebuilt engine re-renders the wallpaper
# instead of leaving the picture the previous binary made on screen. The
# filename has no extension because it is a token, not a file to open.
$hash = (Get-FileHash -Algorithm SHA256 $src).Hash.Substring(0, 16).ToLowerInvariant()
Set-Content -Path $stamp -Value $hash -NoNewline -Encoding ascii

Write-Output "built $out ($((Get-Item $out).Length) bytes) with $csc"
Write-Output "stamp $hash"
