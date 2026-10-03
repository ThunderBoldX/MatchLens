$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/MatchLens/MatchLens.csproj'
$config = Join-Path $PSScriptRoot 'src/MatchLens/NuGet.Config'
dotnet restore $project --configfile $config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $project -c Release --no-restore
exit $LASTEXITCODE
