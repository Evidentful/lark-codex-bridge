using System;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace LarkCodex {
    internal static class Diagnostics {
        internal static int CancelRecording() {
            foreach(var p in System.Diagnostics.Process.GetProcessesByName("ChatGPT")) {
                if(p.MainWindowHandle==IntPtr.Zero || !DesktopNative.IsCodex(p.MainWindowHandle)) continue;
                var root=AutomationElement.FromHandle(p.MainWindowHandle);
                var cancel=root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,"Cancel dictation")));
                if(cancel!=null) {((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Console.WriteLine("Cancelled recording without sending.");return 0;}
            }
            Console.WriteLine("No recording to cancel."); return 0;
        }
        internal static int Probe() {
            int replies=0; var got=new AutoResetEvent(false);
            var devices=HidNative.Enumerate();
            foreach(var d in devices) {
                Console.WriteLine("HID usagePage=0x{0:x4} in={1} out={2} report55={3}",d.Caps.UsagePage,d.Caps.InputLength,d.Caps.OutputLength,d.Report55);
                if(!d.Report55) continue;
                using(var conn=new HidConnection(d,delegate(byte[] report) {
                    var status=Protocol.Status(report);
                    if(status!=null) { Console.WriteLine("STATUS transmitter={0} noise={1} muted={2}",status.Connected,status.Noise,status.Muted); Interlocked.Increment(ref replies);got.Set(); }
                    var firmware=Protocol.Payload(report,0x41,6);
                    if(firmware!=null) Console.WriteLine("FIRMWARE {0}.{1}.{2}.{3}{4}{5}",firmware[0],firmware[1],firmware[2],firmware[3],firmware[4],firmware[5]);
                })) {
                    conn.Send(0x41); Thread.Sleep(150);
                    for(int i=0;i<3;i++) {conn.Send(0x10);got.WaitOne(800);}
                    if(conn.ReadError!=null) Console.WriteLine("READ ERROR "+conn.ReadError.Message);
                }
            }
            Console.WriteLine("Valid status replies: "+replies);return replies>0?0:2;
        }
        internal static void Inspect() {
            foreach(var p in System.Diagnostics.Process.GetProcessesByName("ChatGPT")) {
                if(p.MainWindowHandle==IntPtr.Zero || !DesktopNative.IsCodex(p.MainWindowHandle)) continue;
                var root=AutomationElement.FromHandle(p.MainWindowHandle);
                Console.WriteLine("WINDOW "+p.Id+" "+p.MainWindowHandle);
                var nodes=root.FindAll(TreeScope.Descendants,new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document)));
                foreach(AutomationElement n in nodes) {
                    string name=n.Current.Name;
                    bool relevant=n.Current.ControlType==ControlType.Edit || n.Current.ControlType==ControlType.Document || name.IndexOf("dictat",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("record",StringComparison.OrdinalIgnoreCase)>=0 || name=="Stop" || name=="Send" || name.IndexOf("Send",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("transcrib",StringComparison.OrdinalIgnoreCase)>=0;
                    if(!relevant) continue;
                    Console.WriteLine("{0} name={1} enabled={2} id={3} runtime={4}",n.Current.ControlType.ProgrammaticName,name,n.Current.IsEnabled,n.Current.AutomationId,string.Join(".",n.GetRuntimeId()));
                    Console.WriteLine(" patterns="+string.Join(",",n.GetSupportedPatterns().Select(x=>x.ProgrammaticName).ToArray()));
                    if(n.Current.ControlType==ControlType.Edit) {
                        object value; string text="";
                        if(n.TryGetCurrentPattern(ValuePattern.Pattern,out value)) text=((ValuePattern)value).Current.Value;
                        else if(n.TryGetCurrentPattern(TextPattern.Pattern,out value)) text=((TextPattern)value).DocumentRange.GetText(-1);
                        Console.WriteLine(" draftLength="+text.Trim().Length+" placeholder="+(text.Trim()==name.Trim()));
                    }
                }
            }
        }
        internal static int RecordTest() {
            foreach(var p in System.Diagnostics.Process.GetProcessesByName("ChatGPT")) {
                if(p.MainWindowHandle==IntPtr.Zero || !DesktopNative.IsCodex(p.MainWindowHandle)) continue;
                var root=AutomationElement.FromHandle(p.MainWindowHandle);
                var button=root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,"Dictate")));
                if(button==null) continue;
                var before=CodexView.Read(p.MainWindowHandle);
                Console.WriteLine("Invoking Dictate; will cancel without submitting.");
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                try {
                    Thread.Sleep(2000); Inspect();
                    var during=CodexView.Read(p.MainWindowHandle);
                    if(before==null||during==null||!during.Recording||during.Anchor!=before.Anchor) throw new Exception("Recording state or chat identity was not confirmed");
                    Console.WriteLine("PASS: native recording recognized; chat/editor identity stayed stable.");
                }
                finally {
                    var cancel=root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,"Cancel dictation")));
                    if(cancel!=null) {((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Console.WriteLine("Cancelled recording.");}
                    else Console.WriteLine("No active Cancel dictation control.");
                }
                return 0;
            }
            return 2;
        }
        internal static int View() {
            foreach(var p in System.Diagnostics.Process.GetProcessesByName("ChatGPT")) {
                if(p.MainWindowHandle==IntPtr.Zero || !DesktopNative.IsCodex(p.MainWindowHandle)) continue;
                var view=CodexView.Read(p.MainWindowHandle);
                if(view==null) {Console.WriteLine("No composer view found");return 2;}
                Console.WriteLine("Composer identified; foreground={0} recording={1} transcribing={2} dictate={3} send={4} draftLength={5}",DesktopNative.GetForegroundWindow()==view.Window,view.Recording,view.Transcribing,view.Dictate!=null,view.Send!=null,view.Draft.Length);
                Console.WriteLine("Chat/editor identity: "+view.Anchor);
                return 0;
            }
            return 2;
        }
        internal static int DraftTest() {
            var view=CodexView.Read(DesktopNative.GetForegroundWindow());
            if(view==null||view.Recording||view.Transcribing||view.Starting) throw new Exception("Focus an idle Codex composer first");
            var pattern=(ValuePattern)view.Editor.GetCurrentPattern(ValuePattern.Pattern);
            string original=string.IsNullOrEmpty(view.Draft)?"":pattern.Current.Value;
            try {
                pattern.SetValue("LARK bridge UI test (unsent)");Thread.Sleep(400);
                var after=CodexView.Read(view.Window);
                if(after!=null) Console.WriteLine("DRAFT CHECK length={0} sameChat={1} sendFound={2} buttons={3}",after.Draft.Length,after.Anchor==view.Anchor,after.Send!=null,string.Join("; ",after.Scope.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button)).Cast<AutomationElement>().Select(x=>x.Current.Name).ToArray()));
                if(after==null||after.Anchor!=view.Anchor||after.Send==null||!after.Send.Current.IsEnabled||after.Draft.Length==0) throw new Exception("Send control was not available for an unsent draft");
                Console.WriteLine("PASS: enabled native Send control identified; chat identity stable; no message submitted.");
            } finally {pattern.SetValue(original);Thread.Sleep(150);Console.WriteLine("Restored original draft.");}
            return 0;
        }
    }
    internal static class Program {
        [STAThread] internal static int Main(string[] args) {
            try {
                if(args.Contains("--probe")) return Diagnostics.Probe();
                if(args.Contains("--inspect")) {Diagnostics.Inspect();return 0;}
                if(args.Contains("--record-test")) return Diagnostics.RecordTest();
                if(args.Contains("--cancel")) return Diagnostics.CancelRecording();
                if(args.Contains("--view")) return Diagnostics.View();
                if(args.Contains("--draft-test")) return Diagnostics.DraftTest();
                if(args.Contains("--test")) return Tests.Run();
                if(args.Contains("--hardware-test")) return HardwareTests.Run();
                if(args.Contains("--window-test")) return WindowTests.Run();
                if(args.Contains("--stop")) {
                    try {using(var stop=EventWaitHandle.OpenExisting(AppSignals.StopName)) stop.Set();}
                    catch(WaitHandleCannotBeOpenedException) {Console.WriteLine("Bridge is already stopped.");}
                    return 0;
                }
                if(args.Length>0 && !(args.Length==1&&args[0]=="--start")) {Console.Error.WriteLine("Unknown option.");return 2;}
                bool created;
                using(var mutex=new Mutex(true,AppSignals.MutexName,out created)) {
                    if(!created) {AppSignals.ShowExisting();return 0;}
                    try {System.Windows.Forms.Application.EnableVisualStyles();System.Windows.Forms.Application.Run(new BridgeWindow(args.Contains("--start")));}
                    finally {mutex.ReleaseMutex();}
                }
                return 0;
            } catch(Exception e) {Console.Error.WriteLine(e.ToString());return 1;}
        }
    }
}
