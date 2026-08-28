# Build + publish Shadow RDP C# as a single-file self-contained .exe.
# Output: bin\Release\net8.0-windows\win-x64\publish\PupSeekRDP.exe
#
# Why self-contained: works on any Windows 10/11 x64, no .NET install required.
# Why single-file: one .exe to ship, simpler than a folder of DLLs.
# Why compression: trims ~30% off the file size.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    Write-Host "==> dotnet publish..."
    dotnet publish -c Release `
        -r win-x64 `
        --self-contained true `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    $out = Join-Path $root 'bin\Release\net8.0-windows\win-x64\publish\PupSeekRDP.exe'
    if (Test-Path $out) {
        $size = [math]::Round((Get-Item $out).Length / 1MB, 1)
        Write-Host ""
        Write-Host "OK: $out" -ForegroundColor Green
        Write-Host "Size: $size MB"
    } else {
        throw "Expected output not found: $out"
    }
} finally {
    Pop-Location
}
