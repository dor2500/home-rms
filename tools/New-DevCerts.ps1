<#
.SYNOPSIS
  Generates the Home RMS deployment PKI (root CA + agent + controller certs).

.DESCRIPTION
  Run this ONCE for your home deployment, then copy the generated certs folder to
  every agent machine and to the controller machine. All agents and the controller
  share the same CA so they mutually authenticate; no certificate is ever sent in
  the clear and there is no embedded shared secret.

  By default certs are written to:
      %ProgramData%\HomeRMS\certs

.PARAMETER Password
  PFX password protecting the private keys. Must match the HOMERMS_CERT_PASSWORD
  environment variable used by the agent and controller (default: "homerms-dev").

.EXAMPLE
  ./New-DevCerts.ps1 -Password 'my-strong-pass'
#>
param(
    [string]$Password = $env:HOMERMS_CERT_PASSWORD
)

if ([string]::IsNullOrWhiteSpace($Password)) { $Password = 'homerms-dev' }

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/HomeRms.Shared/HomeRms.Shared.csproj'

Write-Host "Generating Home RMS certificates (password: $Password)..." -ForegroundColor Cyan

# Drive the same generation code the apps use, via a tiny inline program.
$certDir = Join-Path $env:ProgramData 'HomeRMS/certs'
$embeddedDir = Join-Path $root 'src/HomeRms.Shared/Certs'
New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null

$code = @"
using HomeRms.Shared;
using HomeRms.Shared.Security;
DevCertificates.GenerateNewCertificates(@"$embeddedDir", "$Password");
foreach (var f in System.IO.Directory.GetFiles(@"$embeddedDir", "*.pfx"))
{
    System.IO.Directory.CreateDirectory(@"$certDir");
    System.IO.File.Copy(f, System.IO.Path.Combine(@"$certDir", System.IO.Path.GetFileName(f)), overwrite: true);
}
System.Console.WriteLine("Certificates generated & synced to: " + @"$embeddedDir" + " and " + @"$certDir");
"@

$tmp = Join-Path $env:TEMP ("homerms-certgen-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Set-Content -Path (Join-Path $tmp 'Program.cs') -Value $code -Encoding UTF8
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$proj" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $tmp 'certgen.csproj') -Value $csproj -Encoding UTF8
    dotnet run --project (Join-Path $tmp 'certgen.csproj') -c Release
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Write-Host "Done. Copy '$certDir' to each agent and to the controller." -ForegroundColor Green
