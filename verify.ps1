param([Parameter(Mandatory=$true)][string]$Exe, [string]$Results = (Join-Path $PSScriptRoot 'test-results'))
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$Results = [IO.Path]::GetFullPath($Results)
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('FloatBrowser.Verify.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $Results,$runRoot | Out-Null
function Run-Case([string]$Mode, [string]$Profile, [string]$Name, [switch]$Render) {
    $result = Join-Path $Results "$Name.txt"
    $testArgs = @($Mode, '--test-root', ('"' + $Profile + '"'), '--result', ('"' + $result + '"'))
    if ($Render) { $testArgs += @('--render-dir', ('"' + (Join-Path $Results 'previews') + '"')) }
    $process = Start-Process -FilePath $Exe -ArgumentList $testArgs -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$Name timed out." }
    $process.Refresh()
    $text = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { '' }
    if ($process.ExitCode -ne 0 -or -not $text.StartsWith('PASS')) { throw "$Name failed, exit=$($process.ExitCode)`n$text" }
    Add-Content -LiteralPath (Join-Path $Results 'exit-codes.txt') -Value "$Name exit=0"
    Write-Output "$Name PASS; exit=0"
}
Run-Case '--self-test' (Join-Path $runRoot 'unit') 'unit'
Run-Case '--smoke-test' (Join-Path $runRoot 'browser') 'integration' -Render
Run-Case '--persistence-test' (Join-Path $runRoot 'browser') 'persistence'
Run-Case '--close-test' (Join-Path $runRoot 'browser') 'close-last-window'
Run-Case '--close-list-test' (Join-Path $runRoot 'browser') 'close-last-from-list'
Write-Output "Reports: $Results"
Write-Output "Isolated test profile retained for diagnosis: $runRoot"
