param(
    [string]$Executable = "$PSScriptRoot\..\src\Hmy.Estao.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Hmy.Estao.exe",
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'Temp\opencode\harness-ui-smoke')
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive Windows desktop required.' }
$desktopRuntime = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
Add-Type -Path (Join-Path $desktopRuntime.FullName 'UIAutomationClient.dll')
Add-Type -Path (Join-Path $desktopRuntime.FullName 'UIAutomationTypes.dll')
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HarnessSmokeWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
}
'@
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$profile = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $profile | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $Executable).Path, '--harness-hub')
$start.UseShellExecute = $false
$start.Environment['HMY_ESTAO_CONFIG'] = Join-Path $profile 'config.json'
$start.Environment['HMY_ESTAO_GITHUB_APP_CLIENT_ID'] = ''
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForInputIdle(15000)) { throw 'Window did not become ready.' }
    # WinForms can signal input-idle while theme/font initialization is still creating its first window.
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw 'Harness Hub exited before creating its window.' }
        if ($process.MainWindowHandle -ne 0) { break }
        [Threading.Thread]::Sleep(100)
    } while ($deadline.Elapsed.TotalSeconds -lt 15)
    if ($process.MainWindowHandle -eq 0) { throw 'No Harness Hub window.' }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
    $names = @($buttons | ForEach-Object { $_.Current.Name })
    foreach ($harness in @('OpenAI Codex', 'Claude Code', 'GitHub Copilot CLI', 'OpenCode')) {
        if ("Instalar $harness" -notin $names) { throw "Missing action: $harness" }
    }
    if ($names -match 'Publish|Catalog|My Setup') { throw 'Obsolete UI remains active.' }
    $details = $buttons | Where-Object { $_.Current.Name -eq 'Ver detalles' } | Select-Object -First 1
    $details.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    foreach ($size in @(@(940,780), @(800,680))) {
        [HarnessSmokeWindow]::MoveWindow($process.MainWindowHandle, 60, 60, $size[0], $size[1], $true) | Out-Null
        $rect = [HarnessSmokeWindow+Rect]::new()
        [HarnessSmokeWindow]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
        foreach ($action in ($buttons | Where-Object { $_.Current.Name.StartsWith('Instalar ') })) {
            $bounds = $action.Current.BoundingRectangle
            if ($action.Current.IsOffscreen -or $bounds.Right -gt $rect.Right -or $bounds.Bottom -gt $rect.Bottom) { throw 'Harness action is clipped.' }
        }
        $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left, $rect.Bottom-$rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        try { if (-not [HarnessSmokeWindow]::PrintWindow($process.MainWindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' } }
        finally { $graphics.ReleaseHdc($hdc) }
        $image = Join-Path $OutputDirectory "harness-hub-$($size[0])x$($size[1]).png"
        $bitmap.Save($image, [Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Write-Output "Screenshot: $image"
    }
    Write-Output 'PASS: four branded actions, no obsolete catalog/publish controls, details interaction, unclipped actions and two window sizes. Device authorization not exercised.'
    $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit(10000)) { throw 'Window did not close safely.' }
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
    $process.Dispose()
}
