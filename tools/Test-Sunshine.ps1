param(
    [string]$Exe = (Join-Path $PSScriptRoot '../output/AgentBell-win-x64/AgentBell.exe'),
    [switch]$OverlayOnly,
    [string]$ReplaySession
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AgentBellSmokeNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
[AgentBellSmokeNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$log = Join-Path $env:LOCALAPPDATA 'AgentBell/agent-bell.log'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('agent-bell-smoke-' + [Guid]::NewGuid().ToString('N'))
$location = Join-Path $fixture ('sessions/' + (Get-Date -Format 'yyyy/MM/dd'))
$originalCursor = New-Object AgentBellSmokeNative+Point
if (-not [AgentBellSmokeNative]::GetCursorPos([ref]$originalCursor)) { throw 'Cursor unavailable' }

function Get-Window {
    [AgentBellSmokeNative]::FindWindow('AgentBell.Overlay','Agent Bell')
}
function Get-StreamState {
    $port = 47989
    $override = Select-String -LiteralPath (Join-Path $env:ProgramFiles 'Sunshine/config/sunshine.conf') -Pattern '^\s*port\s*=\s*(\d+)\s*$' | Select-Object -Last 1
    if ($override) { $port = [int]$override.Matches[0].Groups[1].Value }
    $xmlText = & curl.exe --silent --show-error --noproxy '*' --max-time 4 "http://127.0.0.1:$port/serverinfo"
    if ($LASTEXITCODE -ne 0) { throw 'Sunshine serverinfo did not respond' }
    ([xml]($xmlText -join [Environment]::NewLine)).root.state
}
function Get-LogCount {
    if (Test-Path -LiteralPath $log) { return @(Get-Content -LiteralPath $log).Count }
    return 0
}
function Get-NewLog([int]$base) {
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | Select-Object -Skip $base }
}
function Wait-Log([int]$base,[string]$pattern,[int]$timeout = 16) {
    $until = [DateTime]::UtcNow.AddSeconds($timeout)
    do {
        $line = @(Get-NewLog $base) | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($line) { return $line }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $until)
    throw "Missing log entry $pattern. Recent events: $(@(Get-NewLog $base) -join ' | ')"
}
function Write-Line([string]$path,[string]$line) {
    [IO.File]::AppendAllText($path,$line + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
function New-Rollout([string]$id,[string]$threadSource = 'user') {
    $file = Join-Path $location "rollout-$id.jsonl"
    Write-Line $file (@{type='session_meta';payload=@{id=$id;source='vscode';thread_source=$threadSource}} | ConvertTo-Json -Compress)
    return $file
}
function New-Event([string]$kind,[string]$turnId) {
    @{timestamp=[DateTimeOffset]::UtcNow.ToString('o');type='event_msg';payload=@{type=$kind;turn_id=$turnId}} | ConvertTo-Json -Compress
}
function Move-Cursor {
    $point = New-Object AgentBellSmokeNative+Point
    if (-not [AgentBellSmokeNative]::GetCursorPos([ref]$point)) { throw 'Cursor position unavailable' }
    foreach ($step in @(30,-30)) {
        if ([AgentBellSmokeNative]::SetCursorPos($point.X+$step,$point.Y)) {
            $moved = New-Object AgentBellSmokeNative+Point
            if ([AgentBellSmokeNative]::GetCursorPos([ref]$moved) -and $moved.X -ne $point.X) { return }
        }
    }
    throw 'Could not move the test pointer'
}
function Stop-TestApp($process) {
    if (-not $process -or $process.HasExited) { return }
    $window = Get-Window
    if ($window -ne [IntPtr]::Zero) {
        [AgentBellSmokeNative]::PostMessage($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
    }
    if (-not $process.WaitForExit(4000)) { Stop-Process -Id $process.Id }
}

function Assert-CenteredLettering($bitmap) {
    $scale = [Math]::Min($bitmap.Width / 1448.0, $bitmap.Height / 1086.0)
    $xOrigin = ($bitmap.Width - 1448 * $scale) / 2
    $yOrigin = ($bitmap.Height - 1086 * $scale) / 2
    $tolerance = [Math]::Max(6, 5 * $scale)
    $left = [int]($xOrigin + 180 * $scale)
    $right = [int]($xOrigin + 1268 * $scale)
    $textTop = $bitmap.Height
    $textBottom = -1
    foreach ($band in @(@(345,490),@(505,742))) {
        $firstX = $right
        $lastX = -1
        $startY = [int]($yOrigin + $band[0] * $scale)
        $endY = [int]($yOrigin + $band[1] * $scale)
        for ($y = $startY; $y -lt $endY; $y += 3) {
            for ($x = $left; $x -lt $right; $x += 3) {
                $color = $bitmap.GetPixel($x,$y)
                if ($color.R -gt 225 -and $color.G -gt 80 -and $color.B -lt 50) {
                    throw 'An orange dot is visible inside the centered card'
                }
                if ($color.R -lt 230 -or $color.G -lt 205 -or $color.B -lt 170) { continue }
                $firstX = [Math]::Min($firstX,$x)
                $lastX = [Math]::Max($lastX,$x)
                $textTop = [Math]::Min($textTop,$y)
                $textBottom = [Math]::Max($textBottom,$y)
            }
        }
        if ($lastX -le $firstX -or [Math]::Abs(($firstX+$lastX-$bitmap.Width)/2) -gt $tolerance) {
            throw 'A line of lettering is not horizontally centered'
        }
    }
    if ([Math]::Abs(($textTop+$textBottom-$bitmap.Height)/2) -gt $tolerance) {
        throw 'The text block is not vertically centered'
    }
}

function Test-WideLayout($window,$rect) {
    # Exercise the same visible HWND and renderer on the captured desktop.
    # Keeping the existing display mode preserves the active Sunshine stream.
    $width = 1920
    $height = 1080
    if ($rect.Right-$rect.Left -lt $width -or $rect.Bottom-$rect.Top -lt $height) {
        throw 'The active capture screen is too small for the 1920 × 1080 preview'
    }
    if (-not [AgentBellSmokeNative]::SetWindowPos($window,[IntPtr]::Zero,$rect.Left,$rect.Top,$width,$height,0x0014)) {
        throw 'The renderer test window could not be resized'
    }
    try {
        [AgentBellSmokeNative]::RedrawWindow($window,[IntPtr]::Zero,[IntPtr]::Zero,0x0101) | Out-Null
        Start-Sleep -Milliseconds 60
        $wideRect = New-Object AgentBellSmokeNative+Rect
        if (-not [AgentBellSmokeNative]::IsWindowVisible($window) -or
            -not [AgentBellSmokeNative]::GetWindowRect($window,[ref]$wideRect) -or
            $wideRect.Right-$wideRect.Left -ne $width -or $wideRect.Bottom-$wideRect.Top -ne $height) {
            throw 'The 16:9 renderer test window is not visible at the expected size'
        }

        $wide = [Drawing.Bitmap]::new($width,$height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($wide)
            try { $graphics.CopyFromScreen($wideRect.Left,$wideRect.Top,0,0,$wide.Size) }
            finally { $graphics.Dispose() }

            foreach ($point in @(@(8,8),@(8,1071),@(1911,1071))) {
                $color = $wide.GetPixel($point[0],$point[1])
                if ($color.R -lt 155 -or $color.R -gt 225 -or $color.G -gt 95) {
                    throw 'The corner artwork did not reach the corresponding 16:9 screen edge'
                }
            }
            $scale = $height / 1086.0
            $left = [int](($width-1448*$scale)/2+172*$scale)
            $right = $width-$left
            $scanY = [int](325*$scale)
            foreach ($edge in @(@(($left+8),($left-8)),@(($right-8),($right+8)))) {
                $inside = $wide.GetPixel($edge[0],$scanY)
                $outside = $wide.GetPixel($edge[1],$scanY)
                if ($inside.R -lt 95 -or $inside.R -gt 135 -or $inside.G -gt 15 -or $outside.R -lt 235) {
                    throw 'The center card did not keep its 4:3 design scaling'
                }
            }
            Assert-CenteredLettering $wide
            $wideShot = Join-Path $project 'output/verification/sunshine-overlay-16x9.png'
            $wide.Save($wideShot,[Drawing.Imaging.ImageFormat]::Png)
            return $wideShot
        }
        finally { $wide.Dispose() }
    }
    finally {
        [AgentBellSmokeNative]::SetWindowPos($window,[IntPtr]::Zero,$rect.Left,$rect.Top,
            $rect.Right-$rect.Left,$rect.Bottom-$rect.Top,0x0014) | Out-Null
    }
}

if ((Get-Window) -ne [IntPtr]::Zero) { throw 'An Agent Bell instance is already running' }
$streamState = Get-StreamState
if ($streamState -ne 'SUNSHINE_SERVER_BUSY') { throw "Sunshine is not currently streaming: $streamState" }
New-Item -ItemType Directory -Path $location -Force | Out-Null
$originalHome = $env:CODEX_HOME
$fixtureApp = $null
$agentApp = $null
$passed = $false
try {
    if (-not $OverlayOnly) {
        $history = New-Rollout 'history'
        Write-Line $history (New-Event 'task_complete' 'old-turn')
        $base = Get-LogCount
        try {
            $env:CODEX_HOME = $fixture
            $fixtureApp = Start-Process -FilePath $exePath -WindowStyle Hidden -PassThru
        }
        finally { $env:CODEX_HOME = $originalHome }
        $null = Wait-Log $base 'Codex watcher ready'
        $subagent = New-Rollout 'subagent' 'subagent'
        Write-Line $subagent (New-Event 'task_complete' 'subagent-turn')
        $early = New-Rollout 'early'
        Write-Line $early (New-Event 'turn_aborted' 'aborted-turn')
        $event = New-Event 'task_complete' 'move-before-deadline'
        $half = [int][Math]::Floor($event.Length/2)
        [IO.File]::AppendAllText($early,$event.Substring(0,$half))
        Start-Sleep -Milliseconds 600
        if (@(Get-NewLog $base) -match 'Alert armed') { throw 'History, abort, subagent or partial JSON armed a reminder' }
        Write-Line $early $event.Substring($half)
        $null = Wait-Log $base 'turn=move-before-deadline'
        $null = Wait-Log $base 'Alert armed'
        $earlyTimer = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Milliseconds 2000
        Move-Cursor
        $canceled = Wait-Log $base 'Alert canceled reason=cursor moved'
        Write-Line $early $event
        while ($earlyTimer.ElapsedMilliseconds -lt 11000) { Start-Sleep -Milliseconds 200 }
        $earlyLog = @(Get-NewLog $base)
        if ($earlyLog -match 'Alert shown' -or @($earlyLog -match 'Alert armed').Count -ne 1) {
            throw 'The early movement or duplicate completion produced an overlay'
        }
        Write-Output ('PASS early movement, history, partial line, abort, subagent and duplicate | ' + $canceled)
        Stop-TestApp $fixtureApp
        $fixtureApp = $null
    }

    $base = Get-LogCount
    if ($ReplaySession) {
        try {
            $env:CODEX_HOME = $fixture
            $agentApp = Start-Process -FilePath $exePath -WindowStyle Hidden -PassThru
        }
        finally { $env:CODEX_HOME = $originalHome }
    }
    else {
        $agentApp = Start-Process -FilePath $exePath -WindowStyle Hidden -PassThru
    }
    $null = Wait-Log $base 'Codex watcher ready'
    if ($ReplaySession) {
        $codexHome = $originalHome
        if (-not $codexHome) { $codexHome = Join-Path $env:USERPROFILE '.codex' }
        $recording = Get-ChildItem -LiteralPath (Join-Path $codexHome 'sessions') -Recurse -File -Filter "rollout-*$ReplaySession.jsonl" | Select-Object -First 1
        if (-not $recording) { throw 'Recorded Codex session was not found' }
        $completionLine = Select-String -LiteralPath $recording.FullName -SimpleMatch '"type":"task_complete"' | Select-Object -Last 1
        if (-not $completionLine) { throw 'Recorded Codex completion was not found' }
        $replay = $completionLine.Line | ConvertFrom-Json
        $completed = $replay.type -eq 'event_msg' -and $replay.payload.type -eq 'task_complete'
        if (-not $completed) { throw 'The recorded line is not a completion event' }
        $recordedRollout = New-Rollout 'sunshine-replay'
        $deadline = [DateTime]::UtcNow.AddSeconds(55)
        do {
            $steady = [Diagnostics.Stopwatch]::StartNew()
            $previous = New-Object AgentBellSmokeNative+Point
            [AgentBellSmokeNative]::GetCursorPos([ref]$previous) | Out-Null
            while ($steady.ElapsedMilliseconds -lt 750 -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
                $point = New-Object AgentBellSmokeNative+Point
                [AgentBellSmokeNative]::GetCursorPos([ref]$point) | Out-Null
                if ($point.X -ne $previous.X -or $point.Y -ne $previous.Y) { $steady.Restart() }
                $previous = $point
            }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'No stationary cursor window was available' }

            $foreground = [AgentBellSmokeNative]::GetForegroundWindow()
            $base = Get-LogCount
            $replay.timestamp = [DateTimeOffset]::UtcNow.ToString('o')
            $replay.payload.turn_id = [Guid]::NewGuid().ToString()
            Write-Line $recordedRollout ($replay | ConvertTo-Json -Depth 20 -Compress)
            $observed = Wait-Log $base 'Alert (shown|canceled)'
            if ($observed -match 'canceled reason=(cursor moved|keyboard or mouse input)') { continue }
            if ($observed -notmatch 'Alert shown') { throw $observed }
            break
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($observed -notmatch 'Alert shown') { throw 'The cursor moved before the ten-second test completed' }
    }
    else {
        $foreground = [AgentBellSmokeNative]::GetForegroundWindow()
        $nativeCodex = (Get-Command codex.exe -ErrorAction Stop | Select-Object -First 1).Source
        $codexEvents = & $nativeCodex exec --skip-git-repo-check --sandbox read-only --cd $project --json 'Do not use tools or change files. Reply with exactly: Agent Bell Sunshine test.'
        if ($LASTEXITCODE -ne 0) { throw 'Real Codex smoke turn failed' }
        $session = $null
        $completed = $false
        foreach ($eventLine in $codexEvents) {
            try { $decoded = $eventLine | ConvertFrom-Json } catch { continue }
            if ($decoded.type -eq 'thread.started') { $session = $decoded.thread_id }
            if ($decoded.type -eq 'turn.completed') { $completed = $true }
        }
        if (-not $session -or -not $completed) { throw 'Codex did not emit a completed turn' }
        $null = Wait-Log $base ('Codex completed session=' + [regex]::Escape($session))
        $observed = Wait-Log $base 'Alert (shown|canceled)'
    }
    if ($observed -notmatch 'Alert shown display=(?<display>\S+) bounds=(?<bounds>[-\d,]+) elapsed_ms=(?<elapsed>\d+)\.') {
        throw "No visible overlay: $observed"
    }
    $captureDisplay = $Matches.display
    $captureBounds = $Matches.bounds
    $shownMs = [int]$Matches.elapsed
    if ($shownMs -lt 10000 -or $shownMs -gt 11300) { throw "Unexpected alert display time $shownMs ms" }

    $window = Get-Window
    $rect = New-Object AgentBellSmokeNative+Rect
    if (-not [AgentBellSmokeNative]::IsWindowVisible($window) -or
        -not [AgentBellSmokeNative]::GetWindowRect($window,[ref]$rect)) { throw 'Alert window is not visible' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $capture = [Drawing.Bitmap]::new($width,$height)
    $expected = [Drawing.Bitmap]::new((Join-Path $project 'design/orange-sign-4k.png'))
    $shot = Join-Path $project 'output/verification/sunshine-overlay.png'
    try {
        $drawing = [Drawing.Graphics]::FromImage($capture)
        try { $drawing.CopyFromScreen($rect.Left,$rect.Top,0,0,$capture.Size) }
        finally { $drawing.Dispose() }
        foreach ($fraction in @(@(0.08,0.42),@(0.53,0.11),@(0.5,0.88),@(0.23,0.32))) {
            $actualPixel = $capture.GetPixel([int]($width*$fraction[0]),[int]($height*$fraction[1]))
            $expectedPixel = $expected.GetPixel([int]($expected.Width*$fraction[0]),[int]($expected.Height*$fraction[1]))
            if ([Math]::Abs($actualPixel.R-$expectedPixel.R) -gt 24 -or
                [Math]::Abs($actualPixel.G-$expectedPixel.G) -gt 24 -or
                [Math]::Abs($actualPixel.B-$expectedPixel.B) -gt 24) {
                throw 'Sampled screen pixels do not match the C artwork'
            }
        }
        Assert-CenteredLettering $capture
        New-Item -ItemType Directory -Path (Split-Path -Parent $shot) -Force | Out-Null
        $capture.Save($shot,[Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $expected.Dispose(); $capture.Dispose() }

    $wideShot = Test-WideLayout $window $rect
    $foregroundUnchanged = $foreground -eq [AgentBellSmokeNative]::GetForegroundWindow()
    $moveTimer = [Diagnostics.Stopwatch]::StartNew()
    Move-Cursor
    while ([AgentBellSmokeNative]::IsWindowVisible($window) -and $moveTimer.ElapsedMilliseconds -lt 1200) {
        Start-Sleep -Milliseconds 15
    }
    $hiddenMs = $moveTimer.ElapsedMilliseconds
    if ([AgentBellSmokeNative]::IsWindowVisible($window)) { throw 'Overlay stayed visible after cursor movement' }
    $null = Wait-Log $base 'Alert hidden reason=cursor moved'
    $stateAfter = Get-StreamState
    if ($stateAfter -ne 'SUNSHINE_SERVER_BUSY') { throw 'Sunshine stopped streaming during the test' }
    if ($ReplaySession) {
        Stop-TestApp $agentApp
        $base = Get-LogCount
        $agentApp = Start-Process -FilePath $exePath -WindowStyle Hidden -PassThru
        $null = Wait-Log $base 'Codex watcher ready'
    }

    $result = [ordered]@{
        CodexTurnCompleted = $completed
        ReplayedSession = $ReplaySession
        SunshineBefore = $streamState
        SunshineAfter = $stateAfter
        Display = $captureDisplay
        Bounds = $captureBounds
        ShownAfterMs = $shownMs
        HiddenWithinMs = $hiddenMs
        ForegroundUnchanged = $foregroundUnchanged
        Screenshot = $shot
        WideScreenshot = $wideShot
        WidePreview = '1920x1080 HWND on the active Sunshine capture display'
        RunningProcess = $agentApp.Id
    }
    $json = $result | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $project 'output/verification/sunshine-result.json'),$json,[Text.UTF8Encoding]::new($false))
    $json
    $passed = $true
}
finally {
    [AgentBellSmokeNative]::SetCursorPos($originalCursor.X,$originalCursor.Y) | Out-Null
    Stop-TestApp $fixtureApp
    if (-not $passed) { Stop-TestApp $agentApp }
}
