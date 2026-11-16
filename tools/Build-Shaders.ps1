# Recompiles the controller's GPU pixel shaders (Shaders\*.fx -> *.ps). Needs the Windows SDK.
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter fxc.exe |
    Where-Object FullName -match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1
if (-not $fxc) { throw "fxc.exe not found - install the Windows SDK." }
$dir = Join-Path $PSScriptRoot "..\src\HomeRms.Controller\Shaders"
Get-ChildItem $dir -Filter *.fx | ForEach-Object {
    $out = [IO.Path]::ChangeExtension($_.FullName, ".ps")
    & $fxc.FullName /nologo /T ps_3_0 /E main /O3 /Fo $out $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile $($_.Name)" }
}
