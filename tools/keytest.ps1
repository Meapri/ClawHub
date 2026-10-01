<#
  ClawHub hardware key-output test.
  Applies a temporary mapping to controller RAM, records keyboard events with a low-level hook
  (and XInput button state) for a few seconds per phase, then always restores the original mapping.

  -SelfTest : only checks that the hook works (sends one injected F24, no controller writes)
#>
param([int]$Seconds = 15, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class KbHook {
  public static List<string> Log = new List<string>();
  delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
  static Proc _p = Cb; static IntPtr _h;
  [StructLayout(LayoutKind.Sequential)] struct K { public uint vk, scan, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct XS { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }
  [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc p, IntPtr mod, uint tid);
  [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
  [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("xinput1_4.dll")] public static extern int XInputGetState(int i, ref XS s);
  static IntPtr Cb(int n, IntPtr w, IntPtr l) {
    int msg = (int)w;
    if (n >= 0 && (msg == 0x100 || msg == 0x104)) {
      var k = (K)Marshal.PtrToStructure(l, typeof(K));
      Log.Add(string.Format("{0:HH:mm:ss.fff} key vk=0x{1:X2} scan=0x{2:X2} injected={3}", DateTime.Now, k.vk, k.scan, (k.flags & 0x10) != 0));
    }
    return CallNextHookEx(_h, n, w, l);
  }
  public static void Start() { Log.Clear(); _h = SetWindowsHookEx(13, _p, GetModuleHandle(null), 0); }
  public static void Stop() { UnhookWindowsHookEx(_h); }
}
"@

function Pump([int]$sec, [string]$label) {
    $end = (Get-Date).AddSeconds($sec); $prev = 0
    while ((Get-Date) -lt $end) {
        [System.Windows.Forms.Application]::DoEvents()
        $s = New-Object KbHook+XS
        if ([KbHook]::XInputGetState(0, [ref]$s) -eq 0 -and $s.buttons -ne $prev) {
            $new = $s.buttons -band (-bnot $prev)
            if ($new) { [KbHook]::Log.Add(("{0:HH:mm:ss.fff} xinput buttons=0x{1:X4} ({2})" -f (Get-Date), $new, $label)) }
            $prev = $s.buttons
        }
        Start-Sleep -Milliseconds 8
    }
}

if ($SelfTest) {
    [KbHook]::Start()
    [KbHook]::keybd_event(0x87, 0, 0, [UIntPtr]::Zero); [KbHook]::keybd_event(0x87, 0, 2, [UIntPtr]::Zero)  # F24
    Pump 1 'self'
    [KbHook]::Stop()
    [KbHook]::Log
    return
}

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ClawHub.Service', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)
$w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe, [Text.Encoding]::UTF8)
$id = 0
function Call($obj) { $script:id++; $obj.Id = $script:id; $w.WriteLine(($obj | ConvertTo-Json -Compress -Depth 5)); $r.ReadLine() | ConvertFrom-Json }

# A->Z(105=0x69) X->LCtrl(116=0x74) M1->1(64=0x40), bytes are base64 for System.Text.Json byte[]
$bindings = @{ A = 'aQ=='; X = 'dA=='; M1 = 'QA==' }
$orig = (Call @{ Op = 'GetStatus' }).Status.ControllerMode
try {
    foreach ($mode in 'XInput', 'Desktop') {
        $m = Call @{ Op = 'SetControllerMode'; ControllerMode = $mode }
        Start-Sleep -Milliseconds 1500   # mode switch may re-enumerate the device
        $a = Call @{ Op = 'ApplyMapping'; Bindings = $bindings }
        Write-Host ("[{0}] apply ok={1} {2} -> press A, X, M1 now ({3}s)" -f $mode, $a.Ok, $a.Error, $Seconds)
        [KbHook]::Start(); Pump $Seconds $mode; [KbHook]::Stop()
        "== $mode : $([KbHook]::Log.Count) events"; [KbHook]::Log | Select-Object -First 30
    }
}
finally {
    $x = Call @{ Op = 'RestoreMapping' }
    $y = Call @{ Op = 'SetControllerMode'; ControllerMode = $orig }
    "restored mapping ok=$($x.Ok) active=$($x.Status.MappingActive); mode back to $orig ok=$($y.Ok)"
    $pipe.Dispose()
}
