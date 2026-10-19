$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$project = Join-Path $PSScriptRoot 'Launchpad\Launchpad.csproj'
$output = Join-Path $PSScriptRoot 'dist\Launchpad-Redesign'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $installedDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $installedDotnet) {
        throw '未找到 .NET SDK，请先安装 .NET 10 SDK 后重新运行此脚本。'
    }
    $dotnet = $installedDotnet.Source
}

& $dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $output
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "悬浮版便携程序已发布到 $output"
