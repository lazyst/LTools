# 输入状态诊断（鼠标点击「没反应」时用）
#
#   pwsh -File tools/input-diag.ps1          # 只诊断
#   pwsh -File tools/input-diag.ps1 -Fix     # 诊断 + 给「持有鼠标捕获」的窗口发 WM_CANCELMODE
#
# 为什么需要它：鼠标**移动正常（悬停有高亮）、点击却全都不生效**，在 Windows 上最常见的原因是
# 某个窗口 `SetCapture` 之后再也没收到抬起，于是全系统的鼠标按键消息都送给它。这个脚本把
# 「谁还握着捕获」直接点出来（连同当前按键状态、前台窗口、光标下的窗口）。
param(
    [switch]$Fix
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public struct GTInfo
{
    public int cbSize;
    public int flags;
    public IntPtr hwndActive;
    public IntPtr hwndFocus;
    public IntPtr hwndCapture;
    public IntPtr hwndMenuOwner;
    public IntPtr hwndMoveSize;
    public IntPtr hwndCaret;
    public int left, top, right, bottom;
}

public struct Pt { public int X, Y; }

public class InputDiag
{
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GTInfo lpgui);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Pt p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public const uint WM_CANCELMODE = 0x001F;

    public static readonly List<string> CaptureLines = new List<string>();
    public static readonly List<IntPtr> CaptureHandles = new List<IntPtr>();

    public static string Cls(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    public static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    public static string ProcName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    public static string Describe(IntPtr h)
    {
        uint pid = 0;
        if (h != IntPtr.Zero) GetWindowThreadProcessId(h, out pid);
        return string.Format("hwnd=0x{0:X} class={1} pid={2} ({3}) title=\"{4}\"",
            h.ToInt64(), Cls(h), pid, ProcName(pid), Title(h));
    }

    public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    /// <summary>扫所有进程的所有线程，填 CaptureLines / CaptureHandles。</summary>
    public static void Scan()
    {
        CaptureLines.Clear();
        CaptureHandles.Clear();
        foreach (Process p in Process.GetProcesses())
        {
            ProcessThreadCollection threads;
            try { threads = p.Threads; } catch { continue; }
            foreach (ProcessThread t in threads)
            {
                GTInfo g = new GTInfo();
                g.cbSize = Marshal.SizeOf(typeof(GTInfo));
                bool ok;
                try { ok = GetGUIThreadInfo((uint)t.Id, ref g); } catch { ok = false; }
                if (!ok || g.hwndCapture == IntPtr.Zero) continue;
                CaptureLines.Add(string.Format("pid={0} ({1}) thread={2} capture: {3}",
                    p.Id, p.ProcessName, t.Id, Describe(g.hwndCapture)));
                CaptureHandles.Add(g.hwndCapture);
            }
        }
    }
}
'@

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== 输入状态诊断 $(Get-Date -Format 'HH:mm:ss') ==="
Write-Host ("按键: 左={0} 右={1} 中={2}   （都应为 False；某个是 True 说明系统认为它还按着）" -f `
        [InputDiag]::Down(0x01), [InputDiag]::Down(0x02), [InputDiag]::Down(0x04))

$fg = [InputDiag]::GetForegroundWindow()
Write-Host ("前台: " + [InputDiag]::Describe($fg))

$pt = New-Object Pt
if ([InputDiag]::GetCursorPos([ref]$pt)) {
    $under = [InputDiag]::WindowFromPoint($pt)
    $root = [InputDiag]::GetAncestor($under, 2)
    if ($root -ne [IntPtr]::Zero) { $under = $root }
    Write-Host ("光标 $($pt.X),$($pt.Y) 下: " + [InputDiag]::Describe($under))
}

[InputDiag]::Scan()
if ([InputDiag]::CaptureLines.Count -eq 0) {
    Write-Host "鼠标捕获: 无（没有线程握着捕获）"
}
else {
    Write-Host "鼠标捕获: 找到 $([InputDiag]::CaptureLines.Count) 个"
    [InputDiag]::CaptureLines | ForEach-Object { Write-Host ("  " + $_) }

    if ($Fix) {
        foreach ($h in [InputDiag]::CaptureHandles) {
            [void][InputDiag]::PostMessage($h, [InputDiag]::WM_CANCELMODE, [IntPtr]::Zero, [IntPtr]::Zero)
            Write-Host ("  已发 WM_CANCELMODE → " + [InputDiag]::Describe($h))
        }
        Write-Host "（DefWindowProc 收到 WM_CANCELMODE 会释放捕获）"
    }
    else {
        Write-Host "要当场解锁：带 -Fix 再跑一次（向上面这些窗口发 WM_CANCELMODE）"
    }
}

Write-Host "--- LTools / UiHost 进程 ---"
$found = $false
foreach ($name in 'LTools', 'UiHost') {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $procs) { Write-Host "  ${name}: 未运行"; continue }
    $found = $true
    foreach ($p in $procs) { Write-Host "  ${name}: pid=$($p.Id) 线程=$($p.Threads.Count)" }
}
if (-not $found) {
    Write-Host "  （两个进程都没在跑：问题不在 LTools 的钩子上——低级钩子随进程一起消失）"
}
