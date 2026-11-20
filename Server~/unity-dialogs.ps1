# Lists the native dialogs (Win32 #32770 windows: EditorUtility.DisplayDialog, SDK/VRCFury error boxes) a Unity process
# shows, or presses one of their buttons. Used by the MCP server when Unity's main thread is blocked, since the editor
# itself can't answer then. Output: JSON [{title, text, buttons}].
param(
    [int]$ProcessId = 0,
    [int]$Port = 0,
    [string]$Click = '',
    [string]$Title = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UnityVrcMcpDialogs
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);

    const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_COMMAND = 0x0111;

    public sealed class Dialog
    {
        public IntPtr Handle;
        public string Title;
        public List<string> Text = new List<string>();
        public List<KeyValuePair<string, IntPtr>> Buttons = new List<KeyValuePair<string, IntPtr>>();
    }

    // WM_GETTEXT works across processes (GetWindowText doesn't return another process's edit control text)
    static string Text(IntPtr h)
    {
        var n = (int)SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (n <= 0) return "";
        var sb = new StringBuilder(Math.Min(n, 65536) + 1);
        SendMessage(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
        return sb.ToString();
    }

    static string Class(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<Dialog> Find(uint pid)
    {
        var res = new List<Dialog>();
        EnumWindows((h, l) =>
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h) || Class(h) != "#32770") return true;
            var d = new Dialog { Handle = h, Title = Text(h) };
            EnumChildWindows(h, (c, m) =>
            {
                if (!IsWindowVisible(c)) return true;
                var cls = Class(c);
                var t = Text(c);
                if (cls == "Button") d.Buttons.Add(new KeyValuePair<string, IntPtr>(t.Replace("&", ""), c));
                else if ((cls == "Static" || cls == "Edit" || cls == "RichEdit20W") && t.Trim().Length > 0) d.Text.Add(t.Trim());
                return true;
            }, IntPtr.Zero);
            res.Add(d);
            return true;
        }, IntPtr.Zero);
        return res;
    }

    /// <summary>Presses a button like a click: WM_COMMAND/BN_CLICKED to its dialog (works while Unity is in the background).</summary>
    public static void Press(Dialog d, IntPtr button)
    {
        var id = GetDlgCtrlID(button);
        PostMessage(d.Handle, WM_COMMAND, (IntPtr)(id & 0xFFFF), button);
    }
}
'@

if ($ProcessId -le 0) {
    # older bridges don't report their pid: the process listening on the bridge port is the editor
    $ProcessId = (Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1).OwningProcess
}
$dialogs = [UnityVrcMcpDialogs]::Find([uint32]$ProcessId)
if ($Title) { $dialogs = @($dialogs | Where-Object { $_.Title -like "*$Title*" }) }

$clicked = $null
if ($Click) {
    if ($dialogs.Count -eq 0) { throw 'No Unity dialog is open.' }
    if ($dialogs.Count -gt 1) { throw "Several dialogs are open; pass a title: $(($dialogs | ForEach-Object { $_.Title }) -join ' | ')" }
    $d = $dialogs[0]
    $b = $d.Buttons | Where-Object { $_.Key -ieq $Click } | Select-Object -First 1
    if (-not $b) { throw "No button '$Click' on '$($d.Title)'. Buttons: $(($d.Buttons | ForEach-Object { $_.Key }) -join ', ')" }
    [UnityVrcMcpDialogs]::Press($d, $b.Value)
    $clicked = $b.Key
}

$out = @($dialogs | ForEach-Object {
    [ordered]@{ title = $_.Title; text = ($_.Text -join "`n"); buttons = @($_.Buttons | ForEach-Object { $_.Key }) }
})
ConvertTo-Json -Depth 4 -Compress -InputObject ([ordered]@{ dialogs = $out; clicked = $clicked })
