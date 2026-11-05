param([string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'Temp\opencode\harness-discovery-smoke'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$root = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$fixtureId = 'estao-discovery-fixture'
$skill = "---`nname: $fixtureId`ndescription: Isolated Estao discovery test; do not execute actions.`n---`nReport only that the skill was discovered.`n"

function Start-Isolated([string]$Harness, [string[]]$Arguments) {
    $command = Get-Command $Harness -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return $null }
    $profileHome = Join-Path $root $Harness
    $skillRoot = switch ($Harness) {
        'codex' { Join-Path $profileHome '.agents\skills' }
        'copilot' { Join-Path $profileHome '.copilot\skills' }
        'opencode' { Join-Path $profileHome '.config\opencode\skills' }
    }
    $folder = Join-Path $skillRoot $fixtureId
    New-Item -ItemType Directory -Force $folder | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'SKILL.md'), $skill, [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $profileHome
    if ($command.Path.EndsWith('.ps1')) {
        $start.FileName = (Get-Command pwsh).Path
        foreach ($arg in @('-NoProfile','-File',$command.Path) + $Arguments) { $start.ArgumentList.Add($arg) }
    } elseif ($command.Path.EndsWith('.cmd')) {
        $start.FileName = $env:ComSpec
        $start.Arguments = '/d /s /c ""' + $command.Path + '" ' + ($Arguments -join ' ') + '"'
    } else {
        $start.FileName = $command.Path
        foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    }
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match 'TOKEN|API_KEY|^OPENCODE_|^CODEX_|^COPILOT_|^CLAUDE_|^ANTHROPIC_|^OPENAI_') { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['HOME'] = $profileHome
    $start.Environment['USERPROFILE'] = $profileHome
    $start.Environment['CODEX_HOME'] = Join-Path $profileHome '.codex'
    $start.Environment['COPILOT_HOME'] = Join-Path $profileHome '.copilot'
    $start.Environment['XDG_CONFIG_HOME'] = Join-Path $profileHome '.config'
    $start.Environment['XDG_DATA_HOME'] = Join-Path $profileHome '.data'
    $start.Environment['XDG_CACHE_HOME'] = Join-Path $profileHome '.cache'
    $start.Environment['XDG_STATE_HOME'] = Join-Path $profileHome '.state'
    $start.Environment['OPENCODE_CONFIG_DIR'] = Join-Path $profileHome '.config\opencode'
    $start.Environment['OPENCODE_CONFIG_CONTENT'] = '{}'
    $start.Environment['OPENCODE_DB'] = Join-Path $profileHome 'isolated.db'
    $start.Environment['APPDATA'] = Join-Path $profileHome 'AppData\Roaming'
    $start.Environment['LOCALAPPDATA'] = Join-Path $profileHome 'AppData\Local'
    $start.Environment['NO_COLOR'] = '1'
    return [Diagnostics.Process]::Start($start)
}

foreach ($harness in @('codex','copilot','opencode')) {
    $arguments = switch ($harness) {
        'codex' { @('-s','read-only','-a','untrusted','app-server') }
        'copilot' { @('skill','list','--json') }
        'opencode' { @('--standalone','api','get','/api/skill') }
    }
    $process = Start-Isolated $harness $arguments
    if (-not $process) { Write-Output "NOT_RUN: $harness not installed on PATH."; continue }
    try {
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if ($harness -eq 'codex') {
            $process.StandardInput.WriteLine('{"id":1,"method":"initialize","params":{"clientInfo":{"name":"estao-discovery-smoke","version":"1.0"}}}')
            $initialization = $process.StandardOutput.ReadLineAsync()
            if (-not $initialization.Wait(10000)) { Write-Output 'NOT_VERIFIED: Codex initialization timed out.'; continue }
            if ($null -eq $initialization.Result) { Write-Output 'NOT_VERIFIED: Codex exited before the app-server handshake in the isolated environment.'; continue }
            $process.StandardInput.WriteLine('{"method":"initialized","params":{}}')
            $process.StandardInput.WriteLine((@{ id = 2; method = 'skills/list'; params = @{ cwds = @((Join-Path $root 'codex')); forceReload = $true } } | ConvertTo-Json -Compress))
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            $found = $false
            while ($deadline.Elapsed.TotalSeconds -lt 25) {
                $line = $process.StandardOutput.ReadLineAsync()
                if (-not $line.Wait(25000 - [int]$deadline.Elapsed.TotalMilliseconds)) { break }
                if ($null -eq $line.Result) { break }
                if ($line.Result.Contains('"id":2')) {
                    $found = $line.Result.Contains($fixtureId) -and $line.Result.Contains('estao-discovery')
                    if (-not $found) {
                        $response = $line.Result | ConvertFrom-Json
                        if ($response.error) { Write-Output "Diagnostic: Codex skills/list error code $($response.error.code)." }
                    }
                    break
                }
            }
            Write-Output "$(if($found){'PASS'}else{'NOT_VERIFIED'}): Codex native skills/list discovery in isolated profile."
        } else {
            $output = $process.StandardOutput.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { Write-Output "NOT_VERIFIED: $harness discovery timed out."; continue }
            $found = $process.ExitCode -eq 0 -and $output.Result.Contains($fixtureId)
            Write-Output "$(if($found){'PASS'}else{'NOT_VERIFIED'}): $harness native discovery (exit $($process.ExitCode)), isolated profile."
        }
    } catch {
        Write-Output "NOT_VERIFIED: $harness native discovery could not complete its isolated handshake."
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
Write-Output 'NOT_RUN: Claude Code is not assumed installed; no model requests, scripts or user configurations were executed.'
