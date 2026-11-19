param([string]$DotNet = 'dotnet', [string]$Output = (Join-Path $PSScriptRoot 'publish/win-x64'))
$ErrorActionPreference = 'Stop'
& $DotNet publish (Join-Path $PSScriptRoot 'FloatBrowser.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $Output '使用说明.md')
foreach ($name in 'LICENSE', 'docs', 'third-party') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Output -Recurse -Force
}
Write-Output "Portable build: $Output"
