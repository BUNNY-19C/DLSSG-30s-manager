<#
.SYNOPSIS
  End-to-end smoke test of the built command line against real folders.

.DESCRIPTION
  Uses a throwaway data folder (DLSSGMANAGER_HOME) and a fake game folder, so it never touches the
  user's library or games. With -Payload, deploys the real mod files from that mod root and checks
  the game folder is byte-identical after restore. Without it, a synthetic payload is generated
  (random DLLs that no manifest claims), which exercises the same deploy/restore path.

  Exit code 0 when every step behaved as expected.
#>
param(
    [Parameter(Mandatory)] [string] $Cli,
    [string] $Payload
)

$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path $Cli).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("dlssgm-smoke-" + [guid]::NewGuid().ToString('N'))
$env:DLSSGMANAGER_HOME = Join-Path $work 'home'
$env:DLSSGMANAGER_LANG = 'en'
$failures = 0

function Invoke-Cli([int] $Expect, [string[]] $Arguments) {
    # Expected failures write to stderr; Windows PowerShell 5 would turn that into a terminating
    # error under 'Stop', so relax it for the native call and judge by the exit code alone.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $Cli @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    $code = $LASTEXITCODE
    $mark = if ($code -eq $Expect) { 'ok  ' } else { 'FAIL' }
    Write-Host ("[{0}] dlssgm {1} -> {2} (expected {3})" -f $mark, ($Arguments -join ' '), $code, $Expect)
    if ($code -ne $Expect) { $out | ForEach-Object { Write-Host "      $_" }; $script:failures++ }
    return $out
}

function Get-Snapshot([string] $Dir) {
    Get-ChildItem $Dir -Recurse -File | Sort-Object FullName |
        ForEach-Object { '{0} {1}' -f $_.FullName.Substring($Dir.Length), (Get-FileHash $_.FullName).Hash }
}

try {
    $game = Join-Path $work 'games\a\b\c\Game'
    New-Item -ItemType Directory -Force $game | Out-Null
    Set-Content (Join-Path $game 'Game.exe') 'not really a game'
    Set-Content (Join-Path $game 'dlssg_sm86.ini') "; another tool's settings`r`nFoo=1"

    if ($Payload) {
        $mod = (Resolve-Path $Payload).Path
    } else {
        $mod = Join-Path $work 'mod'
        $files = @('version.dll', 'altnative\winmm.dll', 'altnative\dxgi.dll',
                   'dlssg_sm86\backend\sm86_backend.dll', 'dlssg_sm86\runtime\nvngx_dlssg.dll',
                   'dlssg_sm86\kernels\fg_kernels.dll', 'dlssg_sm86\xefg\xefg_kernels.dll')
        foreach ($f in $files) {
            $p = Join-Path $mod $f
            New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
            $bytes = New-Object byte[] 4096; [Random]::new().NextBytes($bytes); [IO.File]::WriteAllBytes($p, $bytes)
        }
        @('[General]', 'Enabled=1', '[FrameGeneration]', 'Engine=DLSS', 'MaxGeneratedFrames=3', '[DLSSG]', 'Optimized=1',
          'Preset=A', '[XeSSFG]', 'FlowResolution=Middle', 'UseHudless=Auto', '[Runtime]', 'Mode=Bundled',
          '[SuperResolution]', '; Off, M-INT8, L-INT8', 'Mode=Off', '[Logging]', 'Level=1') |
            Set-Content (Join-Path $mod 'dlssg_sm86.ini')
        Set-Content (Join-Path $mod '.manager-version') 'smoke'
    }

    $before = Get-Snapshot $game

    Invoke-Cli 2 @() | Out-Null
    Invoke-Cli 0 @('--help') | Out-Null
    if ($Payload) { Invoke-Cli 0 @('verify', '--mod', $mod) | Out-Null }
    $id = (Invoke-Cli 0 @('add', '--dir', $game, '--name', 'Smoke Game') | Select-Object -Last 1).ToString().Trim()
    Invoke-Cli 1 @('status', '--game', $id) | Out-Null
    Invoke-Cli 0 @('deploy', '--game', $id, '--mod', $mod) | Out-Null
    Invoke-Cli 0 @('status', '--game', $id) | Out-Null

    if (-not (Test-Path (Join-Path $game 'version.dll'))) { Write-Host '[FAIL] proxy not deployed'; $failures++ }
    if (-not (Get-ChildItem (Join-Path $env:DLSSGMANAGER_HOME 'restore') -Recurse -File -Filter 'dlssg_sm86.ini')) {
        Write-Host '[FAIL] foreign INI was not backed up'; $failures++
    }

    Invoke-Cli 0 @('restore', '--game', $id, '--remove-logs') | Out-Null
    $after = Get-Snapshot $game
    if (Compare-Object $before $after) {
        Write-Host '[FAIL] game folder differs after restore:'
        Compare-Object $before $after | ForEach-Object { Write-Host "      $($_.SideIndicator) $($_.InputObject)" }
        $failures++
    } else {
        Write-Host '[ok  ] game folder is byte-identical after restore'
    }

    Invoke-Cli 0 @('recover') | Out-Null
    Invoke-Cli 3 @('deploy', '--game', 'does-not-exist') | Out-Null

    $journal = Join-Path $env:DLSSGMANAGER_HOME 'journal'
    if ((Test-Path $journal) -and (Get-ChildItem $journal -Filter *.json)) { Write-Host '[FAIL] journal left behind'; $failures++ }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { Write-Host "$failures smoke check(s) failed"; exit 1 }
Write-Host 'smoke test passed'
exit 0
