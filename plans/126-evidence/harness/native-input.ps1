param([Parameter(Mandatory)][string]$Title,[Parameter(Mandatory)][string]$ProfilePath,[string]$Keys)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class OwnedAuditWindow {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr p);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder text,int count);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool attach);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 public static IntPtr Find(string title){IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{var s=new StringBuilder(512);GetWindowText(h,s,512);if(s.ToString().StartsWith(title,StringComparison.Ordinal)){found=h;return false;}return true;},IntPtr.Zero);return found;}
}
"@
$auditForeground=[OwnedAuditWindow]::GetForegroundWindow()
$auditClass=New-Object Text.StringBuilder 256
[void][OwnedAuditWindow]::GetClassName($auditForeground,$auditClass,256)
if($auditClass.ToString() -match 'LockScreen|Credential|Winlogon'){throw 'Secure desktop detected; no activation or input attempted.'}
$auditHandle=[OwnedAuditWindow]::Find($Title)
if($auditHandle -eq [IntPtr]::Zero){throw 'Owned audit window not found.'}
[uint32]$auditBrowserPid=0
[void][OwnedAuditWindow]::GetWindowThreadProcessId($auditHandle,[ref]$auditBrowserPid)
$auditBrowser=Get-CimInstance Win32_Process -Filter "ProcessId=$auditBrowserPid"
if(-not $auditBrowser.CommandLine.Replace('\','/').Contains($ProfilePath.Replace('\','/'))){throw 'Window does not belong to the dedicated browser profile; no input attempted.'}
[uint32]$auditOtherPid=0
$auditForegroundThread=[OwnedAuditWindow]::GetWindowThreadProcessId($auditForeground,[ref]$auditOtherPid)
$auditThread=[OwnedAuditWindow]::GetCurrentThreadId()
$auditAttached=$false
if($auditForeground -ne $auditHandle){
try {
 if($auditForegroundThread -ne $auditThread){$auditAttached=[OwnedAuditWindow]::AttachThreadInput($auditThread,$auditForegroundThread,$true)}
 [void][OwnedAuditWindow]::ShowWindow($auditHandle,9)
 [void][OwnedAuditWindow]::BringWindowToTop($auditHandle)
 [void][OwnedAuditWindow]::SetForegroundWindow($auditHandle)
} finally {if($auditAttached){[void][OwnedAuditWindow]::AttachThreadInput($auditThread,$auditForegroundThread,$false)}}
}
for($auditAttempt=0;$auditAttempt -lt 20;$auditAttempt++){if([OwnedAuditWindow]::GetForegroundWindow() -eq $auditHandle){break};Start-Sleep -Milliseconds 50}
if([OwnedAuditWindow]::GetForegroundWindow() -ne $auditHandle){throw 'Owned browser is not native foreground; no key sent.'}
if($Keys){Add-Type -AssemblyName System.Windows.Forms;[System.Windows.Forms.SendKeys]::SendWait($Keys);[System.Windows.Forms.SendKeys]::Flush();Start-Sleep -Milliseconds 100}
