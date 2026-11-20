using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace LarkCodex {
    internal static class DesktopNative {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process,ref uint length,StringBuilder name);
        internal static bool IsCodex(IntPtr window) {
            if(window==IntPtr.Zero) return false;
            uint id;GetWindowThreadProcessId(window,out id);
            try {
                using(var p=Process.GetProcessById((int)id)) {
                    uint length=256;var name=new StringBuilder((int)length);
                    return GetPackageFamilyName(p.Handle,ref length,name)==0 && name.ToString()=="OpenAI.Codex_2p2nqsd0c76g0";
                }
            } catch {return false;}
        }
        internal static bool ModifiersHeld() {return new[]{0x10,0x11,0x12,0x5b,0x5c}.Any(x=>(GetAsyncKeyState(x)&0x8000)!=0);}
    }
    internal sealed class CodexView {
        internal IntPtr Window;
        internal AutomationElement Document,Editor,Scope,Dictate,Stop,Cancel,Send;
        internal string Anchor,Draft;
        internal bool Recording,Transcribing,Starting,Failed;
        private static readonly Condition Buttons=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button);
        internal static string Identity(AutomationElement element) {return string.Join(".",element.GetRuntimeId());}
        internal static string Text(AutomationElement element) {
            object pattern;
            string value=element.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)?((ValuePattern)pattern).Current.Value:
                element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)?((TextPattern)pattern).DocumentRange.GetText(-1):"";
            value=value.Trim();
            return value==element.Current.Name.Trim()?"":value;
        }
        internal static CodexView Read(IntPtr window) {
            if(!DesktopNative.IsCodex(window)) return null;
            var root=AutomationElement.FromHandle(window);
            var doc=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"RootWebArea"));
            if(doc==null) return null;
            // Select the chat composer, not a terminal, search box or settings field.
            var editors=doc.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
            foreach(AutomationElement editor in editors) {
                string name=editor.Current.Name;
                if(!editor.Current.IsEnabled || editor.Current.IsOffscreen || !(name=="Work with Codex" || name=="Ask anything" || name=="Message ChatGPT")) continue;
                var scope=editor;
                for(int depth=0;depth<6;depth++) {
                    scope=TreeWalker.ControlViewWalker.GetParent(scope);if(scope==null||scope==doc) break;
                    var controls=scope.FindAll(TreeScope.Descendants,Buttons).Cast<AutomationElement>().ToArray();
                    if(!controls.Any(x=>IsDictationName(x.Current.Name))) continue;
                    var view=new CodexView {Window=window,Document=doc,Editor=editor,Scope=scope,Draft=Text(editor)};
                    foreach(var control in controls) {
                        string label=control.Current.Name;
                        if(label=="Dictate") view.Dictate=control;
                        if(label=="Stop dictation") view.Stop=control;
                        if(label=="Cancel dictation") view.Cancel=control;
                        if(label=="Cancel transcription" || label=="Finishing dictation" || label=="Preparing dictation") view.Transcribing=true;
                        if(label.StartsWith("Starting dictation",StringComparison.Ordinal)) view.Starting=true;
                        if(label=="Retry dictation") view.Failed=true;
                        if(label=="Send" || label=="Steer" || label=="Queue" || label=="Send message" || label=="Send now" || label=="Queue message" || label=="Send dictated message" || label=="Transcribe and send") view.Send=control;
                    }
                    view.Recording=view.Cancel!=null && view.Stop!=null && view.Stop.Current.IsEnabled;
                    var toolbar=doc.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ToolBar),new PropertyCondition(AutomationElement.NameProperty,"Chat toolbar")));
                    var title=toolbar==null?null:toolbar.FindFirst(TreeScope.Children,Buttons);
                    if(title==null) return null;
                    // Runtime identity also distinguishes separate chats with identical titles.
                    view.Anchor=Identity(doc)+"|"+Identity(editor)+"|"+Identity(title)+"|"+title.Current.Name;
                    return view;
                }
            }
            return null;
        }
        private static bool IsDictationName(string name) {
            return name=="Dictate" || name=="Stop dictation" || name=="Cancel dictation" || name=="Cancel transcription" || name=="Retry dictation" || name=="Finishing dictation" || name.StartsWith("Starting dictation",StringComparison.Ordinal) || name=="Preparing dictation";
        }
        internal static bool Invoke(AutomationElement button) {
            object pattern;
            if(button==null || !button.Current.IsEnabled || button.Current.IsOffscreen || !button.TryGetCurrentPattern(InvokePattern.Pattern,out pattern)) return false;
            ((InvokePattern)pattern).Invoke();return true;
        }
    }
    internal enum DictationPhase {Idle,Starting,Recording,Transcribing,Submitting}
    internal static class SendPolicy {
        internal static bool CanSend(string anchor,string currentAnchor,bool foreground,bool recording,bool transcribing,bool failed,bool dictateReady,bool sendReady,string original,string draft) {
            return foreground && anchor==currentAnchor && !recording && !transcribing && !failed && dictateReady && sendReady && !string.IsNullOrWhiteSpace(draft) && draft!=original;
        }
    }
    internal sealed class DictationController : IDisposable {
        private readonly Thread thread;
        private readonly AutoResetEvent wake=new AutoResetEvent(false);
        private readonly Action<string> status;
        private volatile bool stopping,paused,reset;
        private int queued;
        private DictationPhase phase;
        private string anchor,original;
        private IntPtr window;
        private double deadline;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        internal DictationController(Action<string> reportStatus) {status=reportStatus;thread=new Thread(Run){IsBackground=true,Name="Codex dictation"};thread.SetApartmentState(ApartmentState.MTA);thread.Start();}
        internal void Click() {Interlocked.Increment(ref queued);wake.Set();}
        internal void Pause(bool value) {paused=value;reset=true;wake.Set();}
        internal void CancelPending() {reset=true;wake.Set();}
        private bool Same(CodexView view) {return view!=null && view.Window==window && view.Anchor==anchor && DesktopNative.GetForegroundWindow()==window;}
        private void Clear(string message) {phase=DictationPhase.Idle;anchor=null;original=null;status(message);Log.Write(message);}
        private void HandleClick() {
            if(paused||DesktopNative.ModifiersHeld()) {Log.Write("Click skipped: paused or modifier held");return;}
            IntPtr active=DesktopNative.GetForegroundWindow();
            if(!DesktopNative.IsCodex(active)) {Log.Write("Click skipped: Codex is not foreground");return;}
            var view=CodexView.Read(active);
            if(view==null) {Clear("Cannot find Codex composer");return;}
            if(phase==DictationPhase.Recording) {
                if(!Same(view)||!view.Recording) {Clear("Chat changed or recording ended; no message sent");return;}
                // Stop inserts into the original draft. Submit only after Codex returns
                // to its idle Dictate control and confirms nonempty, changed text.
                if(CodexView.Invoke(view.Stop)) {phase=DictationPhase.Transcribing;deadline=clock.Elapsed.TotalSeconds+60;status("Waiting for transcription");Log.Write("Stop requested; waiting for completed draft");}
                return;
            }
            if(phase!=DictationPhase.Idle) {Log.Write("Click skipped: dictation is busy");return;}
            if(view.Recording||view.Transcribing||view.Starting) {Clear("Finish existing dictation first");return;}
            window=active;anchor=view.Anchor;original=view.Draft;
            if(CodexView.Invoke(view.Dictate)) {phase=DictationPhase.Starting;deadline=clock.Elapsed.TotalSeconds+8;status("Starting dictation");Log.Write("Start requested");}
            else Clear("Dictation is unavailable");
        }
        private void Observe() {
            if(phase==DictationPhase.Idle) return;
            double now=clock.Elapsed.TotalSeconds;
            if(phase==DictationPhase.Submitting) {
                // Send may briefly remount the composer. The action has already
                // been invoked once, so a refresh is not an automatic-send cancellation.
                if(DesktopNative.GetForegroundWindow()!=window) {Clear("Send action finished - ready");return;}
                var submitted=CodexView.Read(window);
                if(submitted!=null && string.IsNullOrWhiteSpace(submitted.Draft)) Clear("Message sent - ready for next dictation");
                else if(now>deadline) Clear("Send requested; check chat for result");
                return;
            }
            if(DesktopNative.GetForegroundWindow()!=window) {Clear("Focus changed; automatic send cancelled");return;}
            var view=CodexView.Read(window);
            if(!Same(view)) {Clear("Chat changed; automatic send cancelled");return;}
            if(now>deadline) {Clear("Dictation timed out; draft left unsent");return;}
            if(view.Failed) {Clear("Transcription failed; draft left unsent");return;}
            if(phase==DictationPhase.Starting) {
                if(view.Recording) {phase=DictationPhase.Recording;deadline=now+600;status("Recording - click LARK to finish and send");Log.Write("Recording confirmed");}
                return;
            }
            if(phase==DictationPhase.Recording) {
                if(!view.Recording&&!view.Starting) Clear("Recording ended manually; automatic send cancelled");
                return;
            }
            if(phase==DictationPhase.Transcribing) {
                bool ready=view.Dictate!=null && view.Dictate.Current.IsEnabled;
                bool send=view.Send!=null && view.Send.Current.IsEnabled;
                if(!SendPolicy.CanSend(anchor,view.Anchor,true,view.Recording,view.Transcribing||view.Starting,view.Failed,ready,send,original,view.Draft)) {
                    if(ready&&!view.Transcribing&&!view.Recording&&!view.Starting&&string.IsNullOrWhiteSpace(view.Draft)) Clear("No speech recognized; no message sent");
                    return;
                }
                // Re-read immediately before invoking the native Send control. No Enter,
                // clipboard manipulation, fixed transcription delay or text injection.
                var current=CodexView.Read(window);
                if(!Same(current)||DesktopNative.ModifiersHeld()) {Clear("Send cancelled; draft left in place");return;}
                if(!SendPolicy.CanSend(anchor,current.Anchor,true,current.Recording,current.Transcribing||current.Starting,current.Failed,current.Dictate!=null&&current.Dictate.Current.IsEnabled,current.Send!=null&&current.Send.Current.IsEnabled,original,current.Draft)) return;
                if(CodexView.Invoke(current.Send)) {phase=DictationPhase.Submitting;deadline=now+8;status("Sending dictated message");Log.Write("Native Send invoked once");}
                return;
            }
        }
        private void Run() {
            while(!stopping) {
                try {
                    if(reset) {reset=false;Clear(paused?"Paused":"Ready");}
                    int clicks=Interlocked.Exchange(ref queued,0);
                    if(clicks>0) HandleClick();
                    Observe();
                } catch(Exception e) {Clear("Codex control unavailable; no automatic send");Log.Write("Codex: "+e.GetType().Name);}
                wake.WaitOne(150);
            }
        }
        public void Dispose() {stopping=true;wake.Set();if(thread.Join(1500)) wake.Dispose();}
    }
}
