param([int]$Seeds = 25)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $sdk) { throw 'Install the .NET 9 SDK to run this standalone, offline core test harness.' }

# Keep compiler outputs outside Assets so Unity never imports generated files.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ParkChaos-CoreTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$coreGlob = [Security.SecurityElement]::Escape((Join-Path $projectRoot 'Assets/Scripts/Core/**/*.cs'))
$harnessGlob = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot '*.cs'))
$projectFile = Join-Path $scratch 'CoreTests.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$coreGlob" />
    <Compile Include="$harnessGlob" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectFile -Encoding utf8
$nugetConfig = Join-Path $scratch 'NuGet.Config'
'<configuration><packageSources><clear /></packageSources></configuration>' |
    Set-Content -LiteralPath $nugetConfig -Encoding utf8

Write-Host "Core test outputs: $scratch"
& $sdk.Source restore $projectFile --configfile $nugetConfig --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Offline restore failed ($LASTEXITCODE). Ensure the .NET 9 SDK is installed." }
& $sdk.Source run --project $projectFile --no-restore --configuration Release -- --seeds $Seeds
if ($LASTEXITCODE -ne 0) { throw "Core test harness failed ($LASTEXITCODE)." }
