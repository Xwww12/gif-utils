param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot "src\FFmpegUtils\FFmpegUtils.csproj"
$output = Join-Path $projectRoot "artifacts\publish\$Runtime"

dotnet restore $project -r $Runtime --ignore-failed-sources
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed with exit code: $LASTEXITCODE"
}

dotnet publish $project -c Release -r $Runtime --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code: $LASTEXITCODE"
}

Write-Output "Published to: $output"
