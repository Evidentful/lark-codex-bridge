using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace LarkCodex {
    internal static class WindowTests {
        private static AutomationElement Button(AutomationElement root,string name) {
            return root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,name)));
        }
        private static bool Wait(Func<bool> condition,int timeout) {
            var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<timeout) {if(condition()) return true;Thread.Sleep(100);}return false;
        }
        internal static int Run() {
            foreach(var p in Process.GetProcessesByName("LARK Codex")) {
                if(p.MainWindowHandle==IntPtr.Zero) continue;
                var root=AutomationElement.FromHandle(p.MainWindowHandle);
                var start=Button(root,"Start");var stop=Button(root,"Stop");
                if(start==null||stop==null) throw new Exception("Start/Stop buttons missing");
                if(stop.Current.IsEnabled) {
                    ((InvokePattern)stop.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if(!Wait(()=>start.Current.IsEnabled&&!stop.Current.IsEnabled,6000)) throw new Exception("Stop did not disable bridge");
                }
                Console.WriteLine("PASS stopped state: Start enabled, Stop disabled");
                for(int cycle=1;cycle<=2;cycle++) {
                    ((InvokePattern)start.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if(!Wait(()=>!start.Current.IsEnabled&&stop.Current.IsEnabled,2000)) throw new Exception("Start did not enable bridge");
                    if(!Wait(()=> {
                        var status=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"BridgeStatus"));
                        return status!=null&&status.Current.Name=="Ready";
                    },6000)) throw new Exception("Receiver did not reach Ready in window");
                    Console.WriteLine("PASS start cycle "+cycle+": buttons updated; worker running");
                    if(cycle==1) {
                        ((InvokePattern)stop.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        if(!Wait(()=>start.Current.IsEnabled&&!stop.Current.IsEnabled,6000)) throw new Exception("Stop/restart failed");
                        Console.WriteLine("PASS stop cycle: bridge disabled; window remains open");
                    }
                }
                var exe=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","LARK Codex.exe"));
                using(var duplicate=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden})) {
                    if(!duplicate.WaitForExit(3000)||duplicate.ExitCode!=0) throw new Exception("Duplicate launch did not activate existing window");
                }
                if(Process.GetProcessesByName("LARK Codex").Length!=1) throw new Exception("Duplicate application instance found");
                Console.WriteLine("PASS duplicate launch: one window/process remains");
                Console.WriteLine("Window checks passed; bridge left running. No screen capture or Codex actions.");return 0;
            }
            throw new Exception("Open LARK Codex.exe first");
        }
    }
}
