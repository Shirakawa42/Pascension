param([int]$Seconds=180,[string]$OutputPath='F:\Unity\projects\pascension\Tools\Diagnostics\window-trace-fast.jsonl')
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class FastProbe {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct PE { public uint size,usage,pid;public UIntPtr heap;public uint module,threads,parent;public int priority;public uint flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string name; }
 [DllImport("kernel32.dll")]static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32FirstW(IntPtr h,ref PE p);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32NextW(IntPtr h,ref PE p);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
 public static PE[] Processes(){var list=new List<PE>();var h=CreateToolhelp32Snapshot(2,0);try{var p=new PE();p.size=(uint)Marshal.SizeOf(p);if(Process32FirstW(h,ref p)){do{list.Add(p);}while(Process32NextW(h,ref p));}return list.ToArray();}finally{CloseHandle(h);}}
 [StructLayout(LayoutKind.Sequential)] public struct Message {public IntPtr hwnd; public uint message;public UIntPtr wparam;public IntPtr lparam;public uint time;public int x,y;public uint extra;}
 public delegate void WinEventProc(IntPtr hook,uint evt,IntPtr hwnd,int obj,int child,uint thread,uint time);
 [DllImport("user32.dll")]static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEventProc callback,uint pid,uint thread,uint flags);
 [DllImport("user32.dll")]static extern bool UnhookWinEvent(IntPtr hook);
 [DllImport("user32.dll")]static extern bool PeekMessage(out Message msg,IntPtr hwnd,uint min,uint max,uint remove);
 [DllImport("user32.dll")]static extern bool TranslateMessage(ref Message msg);
 [DllImport("user32.dll")]static extern IntPtr DispatchMessage(ref Message msg);
 public class WindowEvent {public uint evt,pid,thread,time;public long handle;public string cls;public bool visible;}
 static List<WindowEvent> events=new List<WindowEvent>();
 static WinEventProc callback=OnEvent;static IntPtr hook;
 static void OnEvent(IntPtr h,uint evt,IntPtr hwnd,int obj,int child,uint thread,uint time){
  if(obj!=0 || child!=0 || hwnd==IntPtr.Zero)return;
  var c=new StringBuilder(256);GetClassName(hwnd,c,256);string cls=c.ToString();
  if(cls!="ConsoleWindowClass" && cls!="Ghost" && cls!="UnityWndClass")return;
  uint pid;GetWindowThreadProcessId(hwnd,out pid);
  events.Add(new WindowEvent{evt=evt,pid=pid,thread=thread,time=time,handle=hwnd.ToInt64(),cls=cls,visible=IsWindowVisible(hwnd)});
 }
 public static void StartEvents(){hook=SetWinEventHook(0x8000,0x8003,IntPtr.Zero,callback,0,0,0);if(hook==IntPtr.Zero)throw new Exception("Window event hook unavailable");}
 public static WindowEvent[] DrainEvents(){Message m;while(PeekMessage(out m,IntPtr.Zero,0,0,1)){TranslateMessage(ref m);DispatchMessage(ref m);}var result=events.ToArray();events.Clear();return result;}
 public static void StopEvents(){if(hook!=IntPtr.Zero)UnhookWinEvent(hook);}
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr l);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")]public static extern bool IsHungAppWindow(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
 public class Window {public long handle;public uint pid,thread;public string cls;public bool hung,responding,foreground;}
 public static Window[] Windows(){var list=new List<Window>();var fg=GetForegroundWindow();EnumWindows((h,l)=>{if(!IsWindowVisible(h))return true;var c=new StringBuilder(256);GetClassName(h,c,256);string cls=c.ToString();if(cls!="UnityWndClass" && cls!="ConsoleWindowClass" && cls!="Ghost")return true;uint pid;uint tid=GetWindowThreadProcessId(h,out pid);bool responding=true;if(cls=="UnityWndClass"){IntPtr r;responding=SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,100,out r)!=IntPtr.Zero;}list.Add(new Window{handle=h.ToInt64(),pid=pid,thread=tid,cls=cls,hung=IsHungAppWindow(h),responding=responding,foreground=fg==h});return true;},IntPtr.Zero);return list.ToArray();}
}
'@
$path=$OutputPath
$writer=New-Object IO.StreamWriter($path,$false,(New-Object Text.UTF8Encoding($false)))
$writer.AutoFlush=$true
$sawGame=$false;$hung=$false;$timeouts=@{};
$writer.WriteLine((@{kind='metadata';utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress))
$seen=@{};$timer=[Diagnostics.Stopwatch]::StartNew();$lastWindows='';$lastReport=-1
[FastProbe]::StartEvents()
try {
 while($timer.Elapsed.TotalSeconds -lt $Seconds){
  foreach($event in @([FastProbe]::DrainEvents())){
   $writer.WriteLine((@{kind='window_event';seconds=$timer.Elapsed.TotalSeconds;event=$event}|ConvertTo-Json -Compress -Depth 4))
  }
  $procs=@([FastProbe]::Processes())
  foreach($p in $procs){
   $key=[string]$p.pid+':'+$p.name
   if(!$seen.ContainsKey($key)){
    $seen[$key]=$true
    $writer.WriteLine((@{kind='process';seconds=$timer.Elapsed.TotalSeconds;initial=($lastReport -lt 0);pid=$p.pid;parent=$p.parent;name=$p.name}|ConvertTo-Json -Compress))
   }
  }
  $windows=@([FastProbe]::Windows())
  foreach($w in $windows){
   if($w.cls -eq 'UnityWndClass'){
    $sawGame=$true;$key=[string]$w.handle
    if($w.hung){$hung=$true}
    if(!$w.responding){
     if(!$timeouts.ContainsKey($key)){$timeouts[$key]=$timer.Elapsed.TotalSeconds}
     if($timer.Elapsed.TotalSeconds-$timeouts[$key] -ge 5){$hung=$true}
    }else{$timeouts.Remove($key)}
   }
   if($w.cls -eq 'Ghost'){$hung=$true}
  }
  $encoded=ConvertTo-Json -Compress -InputObject $windows
  if($encoded -ne $lastWindows -or [int]$timer.Elapsed.TotalSeconds -ne $lastReport){
   $writer.WriteLine((@{kind='windows';seconds=$timer.Elapsed.TotalSeconds;windows=$windows}|ConvertTo-Json -Depth 4 -Compress));$lastWindows=$encoded
  }
  $lastReport=[int]$timer.Elapsed.TotalSeconds
  Start-Sleep -Milliseconds 40
 }
}finally{[FastProbe]::StopEvents();$writer.Dispose()}
@{output=$path;duration=$timer.Elapsed.TotalSeconds;game_observed=$sawGame;sustained_hang=$hung}|ConvertTo-Json -Compress
if($hung){exit 2};if(!$sawGame){exit 3}
