param([string]$ProcessName = 'estao')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class EstaoDesktopTest {
 public delegate bool Callback(IntPtr h, IntPtr p);
 public delegate void EventCallback(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
 [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
 [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventCallback cb, uint process, uint thread, uint flags);
 [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
 public struct Rect { public int Left, Top, Right, Bottom; }
 public struct Point { public int X,Y; }
 public struct Found { public IntPtr Handle; public Rect Bounds; }
 public static Found[] Visible(uint process) {
  var found = new List<Found>();
  EnumWindows((h,p) => { uint owner; GetWindowThreadProcessId(h, out owner); Rect r;
   if(owner==process && IsWindowVisible(h) && GetWindowRect(h,out r) && r.Right-r.Left>100 && r.Bottom-r.Top>20)
    found.Add(new Found { Handle=h, Bounds=r });
   return true; }, IntPtr.Zero);
  return found.ToArray();
 }
 public static void Click(int x, int y) {
  SetCursorPos(x,y);
  System.Threading.Thread.Sleep(80);
  mouse_event(2,0,0,0,UIntPtr.Zero);
  System.Threading.Thread.Sleep(50);
  mouse_event(4,0,0,0,UIntPtr.Zero);
 }
 static EventCallback callback;
 static IntPtr hook;
 public static int LocationChanges, VisibilityChanges;
 public static void Watch(uint process, IntPtr window) {
  LocationChanges=0; VisibilityChanges=0;
  callback=(h,e,w,o,c,t,ms) => { if(w!=window || o!=0) return;
   if(e==0x800B) LocationChanges++;
   if(e==0x8002 || e==0x8003) VisibilityChanges++;
  };
  hook=SetWinEventHook(0x8002,0x800B,IntPtr.Zero,callback,process,0,0);
  if(hook==IntPtr.Zero) throw new Exception("SetWinEventHook failed");
 }
 public static void StopWatch() { if(hook!=IntPtr.Zero) UnhookWinEvent(hook); hook=IntPtr.Zero; callback=null; }
}
'@
$p = Get-Process $ProcessName | Select-Object -First 1
$oldDpi = [EstaoDesktopTest]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
function Pump([int]$Milliseconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $Milliseconds) {
        [System.Windows.Forms.Application]::DoEvents()
        if ($p.HasExited) { throw 'Estao exited during the smoke test' }
        [Threading.Thread]::Sleep(10)
    }
}
function Popup { ,@([EstaoDesktopTest]::Visible($p.Id) | Where-Object { $_.Bounds.Bottom - $_.Bounds.Top -gt 100 }) }
$original = New-Object EstaoDesktopTest+Point
[void][EstaoDesktopTest]::GetCursorPos([ref]$original)
$outside = New-Object System.Windows.Forms.Form
$outside.Text = 'Estao outside-click test'
$outside.StartPosition = 'Manual'
$outside.Location = [Drawing.Point]::new(100,400)
$outside.Size = [Drawing.Size]::new(240,100)
$outside.TopMost = $true
try {
    $outside.Show()
    Pump 300
    $outsidePoint = $outside.PointToScreen([Drawing.Point]::new(40,45))
    $strip = [EstaoDesktopTest]::Visible($p.Id) | Where-Object { $_.Bounds.Bottom - $_.Bounds.Top -lt 100 } | Select-Object -First 1
    if (-not $strip) { throw 'No visible usage strip' }
    $x = $strip.Bounds.Left + 70
    $y = ($strip.Bounds.Top + $strip.Bounds.Bottom) / 2
    [void][EstaoDesktopTest]::SetCursorPos($x,$y)
    Pump 600
    if ((Popup).Count -gt 0) { throw 'Hover still opens the popover' }
    Write-Output 'STEP: click open'
    [EstaoDesktopTest]::Click($x,$y)
    Pump 500
    $popup = Popup
    if ($popup.Count -ne 1) {
        $cursor = New-Object EstaoDesktopTest+Point
        [void][EstaoDesktopTest]::GetCursorPos([ref]$cursor)
        "Click at $x,$y; cursor $($cursor.X),$($cursor.Y); hit HWND $([EstaoDesktopTest]::WindowFromPoint($cursor)); expected $($strip.Handle)" | Write-Output
        [EstaoDesktopTest]::Visible($p.Id) | ForEach-Object { 'Visible HWND {0}: {1},{2} {3}x{4}' -f $_.Handle,$_.Bounds.Left,$_.Bounds.Top,($_.Bounds.Right-$_.Bounds.Left),($_.Bounds.Bottom-$_.Bounds.Top) } | Write-Output
        throw 'Click did not open exactly one popover'
    }
    [void][EstaoDesktopTest]::SetCursorPos(150,450)
    Pump 400
    if ((Popup).Count -ne 1) { throw 'Mouse leave incorrectly closed the click popover' }
    Write-Output ('STEP: click inside at {0},{1}' -f ($popup[0].Bounds.Left+40),($popup[0].Bounds.Top+40))
    $r = $popup[0].Bounds
    [EstaoDesktopTest]::Click(($r.Left+40),($r.Top+40))
    Pump 300
    if ((Popup).Count -ne 1) { throw 'Click inside closed the popover' }
    Write-Output 'STEP: click close'
    [EstaoDesktopTest]::Click($x,$y)
    Pump 400
    if ((Popup).Count -ne 0) {
        [EstaoDesktopTest]::Visible($p.Id) | ForEach-Object { 'At repeat click ({0},{1}), HWND {2}: {3},{4} {5}x{6}' -f $x,$y,$_.Handle,$_.Bounds.Left,$_.Bounds.Top,($_.Bounds.Right-$_.Bounds.Left),($_.Bounds.Bottom-$_.Bounds.Top) } | Write-Output
        throw 'Repeat click failed to close, or reopened the popover'
    }
    Write-Output 'STEP: click reopen'
    [EstaoDesktopTest]::Click($x,$y)
    Pump 400
    if ((Popup).Count -ne 1) { throw 'Popover failed to reopen' }
    [EstaoDesktopTest]::Click($outsidePoint.X,$outsidePoint.Y)
    Pump 400
    if ((Popup).Count -ne 0) { throw 'Outside click did not close the popover' }
    [EstaoDesktopTest]::Watch($p.Id,$strip.Handle)
    Pump 3500
    $moves = [EstaoDesktopTest]::LocationChanges
    $visibility = [EstaoDesktopTest]::VisibilityChanges
    if ($moves -ne 0 -or $visibility -ne 0) { throw "Idle strip geometry/visibility churn: $moves/$visibility" }
    $p.Refresh()
    'PASS: no hover; click opens; moving away stays open; inside click stays open; repeat/outside click closes.'
    "PASS: 3.5 seconds idle: $moves location changes, $visibility show/hide events."
    'Memory after popover open/close: private {0:N1} MiB, working set {1:N1} MiB' -f ($p.PrivateMemorySize64/1MB),($p.WorkingSet64/1MB)
} finally {
    [EstaoDesktopTest]::StopWatch()
    $outside.Close()
    $outside.Dispose()
    [void][EstaoDesktopTest]::SetCursorPos($original.X,$original.Y)
    [void][EstaoDesktopTest]::SetThreadDpiAwarenessContext($oldDpi)
}
