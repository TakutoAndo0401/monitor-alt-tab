param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = if ($sdkCommand) { $sdkCommand.Source } else { '' }
if (!$sdk -or !(& $sdk --list-sdks | Where-Object { $_ -match '^10\.' })) {
    $sdk = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Codex\tools\dotnet\dotnet.exe'
}
if (!(Test-Path -LiteralPath $sdk)) { throw '.NET 10 SDKが必要です。' }
Push-Location $PSScriptRoot
try {
    & $sdk restore --configfile NuGet.Config -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '復元に失敗しました。' }
    & $sdk build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
    & $sdk 'bin\Release\net10.0-windows\MonitorAltTab.dll' --self-test --output 'test-results.txt'
    if ($LASTEXITCODE -ne 0) { throw '自己テストに失敗しました。test-results.txtを確認してください。' }
    Get-Content -LiteralPath 'test-results.txt'
    if ($Publish) {
        & $sdk publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false --source 'https://api.nuget.org/v3/index.json' -o 'publish'
        if ($LASTEXITCODE -ne 0) { throw '配布用EXEの作成に失敗しました。' }
    }
} finally { Pop-Location }
