$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MatchLens.csproj'
$config = Join-Path $PSScriptRoot 'NuGet.Config'
dotnet restore $project --configfile $config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $project -c Release --no-restore
exit $LASTEXITCODE
