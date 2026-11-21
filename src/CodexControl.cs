using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] events, int size);
        [StructLayout(LayoutKind.Explicit, Size=40)]
        private struct Input {
            [FieldOffset(0)] internal uint Type;
            [FieldOffset(8)] internal ushort Key;
            [FieldOffset(12)] internal uint Flags;
        }
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
        internal static bool ShiftHeld() {return (GetAsyncKeyState(0x10)&0x8000)!=0;}
        internal static bool NonShiftModifiersHeld() {return new[]{0x11,0x12,0x5b,0x5c}.Any(x=>(GetAsyncKeyState(x)&0x8000)!=0);}
        internal static string CodexExecutable(IntPtr window) {
            if(!IsCodex(window)) throw new IOException("Focus the Windows Codex app first.");
            uint id;
            GetWindowThreadProcessId(window,out id);
            using(var process=Process.GetProcessById((int)id)) {
                string path=Path.Combine(Path.GetDirectoryName(process.MainModule.FileName),"resources","codex.exe");
                if(!File.Exists(path)) throw new IOException("The installed Codex configuration tool was not found.");
                return path;
            }
        }
        internal static bool CopyWorkingDirectory(IntPtr window) { return CopyShortcut(window,0xa0,0x43); }
        internal static bool CopyChatLink(IntPtr window) { return CopyShortcut(window,0xa4,0x4c); }
        private static bool CopyShortcut(IntPtr window,ushort modifier,ushort key) {
            if (GetForegroundWindow()!=window || ModifiersHeld() || (GetAsyncKeyState(key)&0x8000)!=0) return false;
            var keys = new ushort[] {0xa2,modifier,key,key,modifier,0xa2};
            var events = keys.Select((code,index)=>new Input { Type=1, Key=code, Flags=index>=3 ? 2u : 0u }).ToArray();
            uint sent = SendInput((uint)events.Length,events,Marshal.SizeOf(typeof(Input)));
            if (sent==events.Length) return true;
            if (sent>0) SendInput(3,events.Skip(3).ToArray(),Marshal.SizeOf(typeof(Input)));
            return false;
        }
    }
    internal sealed class CodexView {
        internal IntPtr Window;
        internal AutomationElement Document,Editor,Scope,Dictate,Stop,Cancel,Send;
        internal string Anchor,Draft,ProjectLabel;
        internal bool NewComposer;
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
                if(!editor.Current.IsEnabled || editor.Current.IsOffscreen || !(name=="Work with Codex" || name=="Do anything" || name=="Ask anything" || name=="Message ChatGPT")) continue;
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
                    var project=scope;
                    for(int projectDepth=0; projectDepth<5 && project!=null && project!=doc; projectDepth++) {
                        var picker=project.FindAll(TreeScope.Descendants,Buttons).Cast<AutomationElement>()
                            .FirstOrDefault(x=>!x.Current.IsOffscreen && x.Current.Name.StartsWith("Change project: ",StringComparison.Ordinal));
                        if(picker!=null) {
                            view.ProjectLabel=picker.Current.Name.Substring("Change project: ".Length);
                            view.NewComposer=true;
                            break;
                        }
                        project=TreeWalker.ControlViewWalker.GetParent(project);
                    }
                    if(title==null&&!view.NewComposer) return null;
                    // Runtime identity also distinguishes separate chats with identical titles.
                    view.Anchor=Identity(doc)+"|"+Identity(editor)+"|"+(view.NewComposer ? "new:"+view.ProjectLabel : Identity(title)+"|"+title.Current.Name);
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
        internal static AutomationElement ReadFolderTrust(IntPtr window, string expectedPath, out AutomationElement confirm) {
            confirm=null;
            var root=AutomationElement.FromHandle(window);
            var dialogs=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Window));
            foreach(AutomationElement dialog in dialogs) {
                string title=dialog.Current.Name;
                if(dialog.Current.IsOffscreen || !(title=="Trust this folder?" || title=="Trust these folders?" ||
                   title=="This folder is marked untrusted" || title=="These folders are marked untrusted")) continue;
                var folders=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem))
                    .Cast<AutomationElement>().Select(item=>item.Current.Name).ToArray();
                if(WorktreeDialog.CanConfirm(title,expectedPath,folders)) {
                    confirm=dialog.FindFirst(TreeScope.Descendants,new AndCondition(Buttons,
                        new PropertyCondition(AutomationElement.NameProperty,"Trust folder")));
                }
                return dialog;
            }
            return null;
        }
    }
    internal static class WorktreeDialog {
        internal static bool CanConfirm(string title, string expectedPath, string[] folders) {
            return title=="Trust this folder?" && folders.Length==1 && GitWorktrees.IsLocalPath(folders[0]) &&
                string.Equals(Path.GetFullPath(folders[0]),expectedPath,StringComparison.OrdinalIgnoreCase);
        }
    }
    internal static class CodexSidebar {
        private static AutomationElement FindUnique(AutomationElement root, ControlType type, string name) {
            var condition=new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,type),
                new PropertyCondition(AutomationElement.NameProperty,name));
            var matches=root.FindAll(TreeScope.Descendants,condition).Cast<AutomationElement>()
                .Where(item=>!item.Current.IsOffscreen && item.Current.IsEnabled).Take(2).ToArray();
            return matches.Length==1 ? matches[0] : null;
        }

        private static bool Expand(AutomationElement item) {
            object pattern;
            if(item==null || !item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern)) return false;
            var menu=(ExpandCollapsePattern)pattern;
            if(menu.Current.ExpandCollapseState==ExpandCollapseState.Collapsed) menu.Expand();
            return true;
        }

        private static AutomationElement WaitForItem(IntPtr window, AutomationElement root, string name, Func<bool> cancelled) {
            var wait=Stopwatch.StartNew();
            while(wait.ElapsedMilliseconds<1200 && !cancelled() && DesktopNative.GetForegroundWindow()==window) {
                var item=FindUnique(root,ControlType.MenuItem,name);
                if(item!=null) return item;
                Thread.Sleep(75);
            }
            return null;
        }

        internal static bool Assign(CodexView view, LocalProject project, Func<bool> cancelled) {
            if(cancelled() || DesktopNative.GetForegroundWindow()!=view.Window) return false;
            var toolbar=FindUnique(view.Document,ControlType.ToolBar,"Chat toolbar");
            if(toolbar==null || !Expand(FindUnique(toolbar,ControlType.Button,"Chat actions"))) return false;
            var root=AutomationElement.FromHandle(view.Window);
            if(!Expand(WaitForItem(view.Window,root,"Move to project",cancelled))) return false;
            var destination=WaitForItem(view.Window,root,project.Name,cancelled);
            return !cancelled() && DesktopNative.GetForegroundWindow()==view.Window && CodexView.Invoke(destination);
        }
    }
    internal enum DictationPhase {Idle,AwaitingSecondPress,AwaitingShiftRelease,OpeningWorktree,Starting,Recording,Transcribing,Submitting}
    internal static class PressTiming {
        internal static double DoubleClickSeconds = 2.2;
        // Conservative spacing guidance, not a measured firmware threshold.
        internal const double NoGoSeconds = 0.6;
        internal static bool IsDouble(double first, double second) {
            return second >= first + NoGoSeconds && second <= first + DoubleClickSeconds;
        }
    }
    internal static class SendPolicy {
        internal static bool CanSend(string anchor,string currentAnchor,bool foreground,bool recording,bool transcribing,bool failed,bool dictateReady,bool sendReady,string original,string draft) {
            return foreground && anchor==currentAnchor && !recording && !transcribing && !failed && dictateReady && sendReady && !string.IsNullOrWhiteSpace(draft) && draft!=original;
        }
    }
    internal sealed class DictationController : IDisposable {
        private readonly Thread thread;
        private readonly AutoResetEvent wake=new AutoResetEvent(false);
        private readonly Action<string> status;
        private readonly string projectOverride;
        private readonly bool worktreesEnabled;
        private volatile bool stopping,reset;
        private readonly ConcurrentQueue<Press> queued=new ConcurrentQueue<Press>();
        private sealed class Press { internal double Time; internal IntPtr Window; internal bool Shift,OtherModifier; }
        private DictationPhase phase;
        private string anchor,original;
        private IntPtr window;
        private double deadline;
        private double firstPress;
        private WorktreeTarget pendingWorktree;
        private bool showingTrust,worktreeTrustVerified,trustClickAttempted,waitingForFocus,sidebarMoveRequested;
        private double lastCodexFocus,nextChatCheck;
        internal Action<string,double,double,bool> Countdown;
        private double countdownEnd,countdownTotal;
        private string countdownLabel;
        private void TickCountdown() {
            var callback=Countdown;
            if(callback==null) return;
            double now=clock.Elapsed.TotalSeconds;
            if(phase==DictationPhase.AwaitingSecondPress) {
                double elapsed=now-firstPress;
                bool red=elapsed<PressTiming.NoGoSeconds;
                callback(red?"Too soon: avoid firmware mute double-tap":"Second press accepted for new worktree",
                    Math.Max(0,(red?PressTiming.NoGoSeconds:PressTiming.DoubleClickSeconds)-elapsed),
                    red?PressTiming.NoGoSeconds:PressTiming.DoubleClickSeconds-PressTiming.NoGoSeconds,red);
            } else if(phase==DictationPhase.Idle) callback("Ready",0,1,false);
            else {
                string label=phase.ToString();
                if(countdownLabel!=label||countdownEnd!=deadline) {countdownLabel=label;countdownEnd=deadline;countdownTotal=Math.Max(0.01,deadline-now);}
                callback(label,Math.Max(0,deadline-now),countdownTotal,false);
            }
        }
        private readonly Stopwatch clock=Stopwatch.StartNew();
        internal DictationController(Action<string> reportStatus,string projectOverride,bool worktreesEnabled) {
            status=reportStatus;this.projectOverride=projectOverride;this.worktreesEnabled=worktreesEnabled;
            thread=new Thread(Run){IsBackground=true,Name="Codex dictation"};
            thread.SetApartmentState(ApartmentState.MTA);thread.Start();
        }
        internal void Click() {
            if(stopping) return;
            queued.Enqueue(new Press {
                Time=clock.Elapsed.TotalSeconds,Window=DesktopNative.GetForegroundWindow(),
                Shift=DesktopNative.ShiftHeld(),OtherModifier=DesktopNative.NonShiftModifiersHeld()
            });
            try {wake.Set();} catch(ObjectDisposedException) {}
        }
        private bool Cancelled() {return stopping||reset;}
        internal void CancelPending() {reset=true;wake.Set();}
        private bool Same(CodexView view) {return view!=null && view.Window==window && view.Anchor==anchor && DesktopNative.GetForegroundWindow()==window;}
        private bool SourceStillCurrent() {
            var wait=Stopwatch.StartNew();
            while(!Cancelled() && DesktopNative.GetForegroundWindow()==window && wait.ElapsedMilliseconds<1500) {
                try {
                    var view=CodexView.Read(window);
                    if(view!=null) return Same(view);
                } catch(ElementNotAvailableException) {
                    // Chromium can rebuild the accessibility tree while the chat is open.
                }
                Thread.Sleep(75);
            }
            return false;
        }
        private void Clear(string message) {
            phase=DictationPhase.Idle;anchor=null;original=null;pendingWorktree=null;
            showingTrust=false;worktreeTrustVerified=false;trustClickAttempted=false;waitingForFocus=false;sidebarMoveRequested=false;
            status(message);Log.Write(message);
        }
        private void HandleClick(Press press) {
            if(Cancelled()||press.OtherModifier||DesktopNative.NonShiftModifiersHeld()||
               DesktopNative.GetForegroundWindow()!=press.Window||!DesktopNative.IsCodex(press.Window)) return;
            if(phase==DictationPhase.AwaitingShiftRelease || phase==DictationPhase.OpeningWorktree || phase==DictationPhase.Starting || phase==DictationPhase.Transcribing || phase==DictationPhase.Submitting) return;
            var view=CodexView.Read(press.Window);
            if(view==null) {Clear("Cannot find Codex composer");return;}
            if(phase==DictationPhase.Recording) {
                if(DesktopNative.ModifiersHeld()) return;
                if(!Same(view)||!view.Recording) {Clear("Chat changed or recording ended; no message sent");return;}
                // Stop inserts into the original draft. Submit only after Codex returns
                // to its idle Dictate control and confirms nonempty, changed text.
                if(!Cancelled() && CodexView.Invoke(view.Stop)) {phase=DictationPhase.Transcribing;deadline=clock.Elapsed.TotalSeconds+Timing.Transcription;status("Waiting for transcription");Log.Write("Stop requested; waiting for completed draft");}
                return;
            }
            if(press.Shift) {
                if(!worktreesEnabled) {Clear("Enable new worktrees in the LARK window first.");return;}
                if(phase==DictationPhase.AwaitingSecondPress&&!Same(view)) {Clear("Chat changed before dictation");return;}
                if(view.Recording||view.Transcribing||view.Starting) {Clear("Finish existing dictation first");return;}
                window=view.Window;anchor=view.Anchor;
                phase=DictationPhase.AwaitingShiftRelease;deadline=clock.Elapsed.TotalSeconds+Timing.Startup;
                status("Release Shift to open a new worktree");
                Log.Write("Worktree gesture selected with Shift");
                return;
            }
            if(DesktopNative.ModifiersHeld()) return;
            if(phase==DictationPhase.AwaitingSecondPress) {
                if(!Same(view)) {Clear("Chat changed before dictation");return;}
                if(press.Time<firstPress+PressTiming.NoGoSeconds) return;
                if(PressTiming.IsDouble(firstPress,press.Time)) {
                    Log.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Worktree gesture: {0:F2}s between detected presses",press.Time-firstPress));
                    OpenWorktree(view);
                }
                else StartRecording(view);
                return;
            }
            if(view.Recording||view.Transcribing||view.Starting) {Clear("Finish existing dictation first");return;}
            if(!worktreesEnabled) {StartRecording(view);return;}
            window=view.Window;anchor=view.Anchor;firstPress=press.Time;
            phase=DictationPhase.AwaitingSecondPress;
            deadline=firstPress+PressTiming.DoubleClickSeconds+2;
            status("Press again for a new worktree…");
        }
        private void StartRecording(CodexView view) {
            if(Cancelled() || DesktopNative.GetForegroundWindow()!=view.Window || view.Recording || view.Transcribing || view.Starting || DesktopNative.ModifiersHeld()) {
                Clear("Dictation start cancelled");return;
            }
            window=view.Window;anchor=view.Anchor;original=view.Draft;
            if(CodexView.Invoke(view.Dictate)) {phase=DictationPhase.Starting;deadline=clock.Elapsed.TotalSeconds+Timing.Startup;status("Starting dictation");Log.Write("Start requested");}
            else Clear("Dictation is unavailable");
        }
        private void OpenWorktree(CodexView source) {
            if(!worktreesEnabled || !ProjectChoices.GrantEnabled) {Clear("Enable new worktrees in the LARK window first.");return;}
            phase=DictationPhase.OpeningWorktree;
            status("Preparing branch and worktree…");
            try {
                string folder=projectOverride??CurrentChatFolder.Read(source,Cancelled);
                if(string.IsNullOrEmpty(folder)) {Clear("Could not read chat folder. Choose a project in LARK.");return;}
                if(!SourceStillCurrent()) {Clear("Chat changed before worktree creation");return;}
                string parent=ProjectChoices.Worktrees;
                string repo=GitWorktrees.ResolveRepository(folder,Cancelled);
                string primary=GitWorktrees.PrimaryCheckout(repo,Cancelled);
                using(var codex=new CodexProjectClient(DesktopNative.CodexExecutable(window),ProjectChoices.CodexHome,Cancelled)) {
                    var project=codex.ResolveProject(folder,repo,primary,parent);
                    var sidebar=ProjectChoices.Read().Where(item=>item.Folder!=null &&
                        string.Equals(Path.GetFullPath(item.Folder).TrimEnd('\\'),project.Folder.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(sidebar.Length!=1) throw new IOException("Could not match the worktree's Codex sidebar project.");
                    if(!SourceStillCurrent()) {Clear("Chat changed before worktree creation");return;}
                    pendingWorktree=GitWorktrees.Create(repo,primary,parent,Cancelled,codex.RequireTrustedSource);
                    pendingWorktree.SidebarProject=sidebar[0];
                    Log.Write("Created worktree: "+pendingWorktree.Branch+" — "+pendingWorktree.Path);
                    if(!SourceStillCurrent()) {Clear("Worktree created. Chat changed before opening it.");return;}
                    codex.Register(pendingWorktree,parent);
                    worktreeTrustVerified=true;
                    codex.CreateEmptyChat(pendingWorktree,project.Id,parent);
                    Log.Write("Created empty worktree chat: "+pendingWorktree.ChatId);
                }
                OpenCreatedChat();
            } catch(OperationCanceledException) {
                Clear("Worktree launch cancelled");
            } catch(Exception e) {
                Log.Write("Worktree launch: "+e);
                Clear(e.Message.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"Could not open a new worktree.");
            }
        }
        private void OpenCreatedChat() {
            if(!SourceStillCurrent()) {Clear("Worktree chat is saved. The source chat changed before it opened.");return;}
            Log.Write("Opening saved Codex chat: "+pendingWorktree.ChatId);
            using(Process.Start(new ProcessStartInfo(pendingWorktree.Link) {UseShellExecute=true})) {}
            lastCodexFocus=clock.Elapsed.TotalSeconds;
            nextChatCheck=lastCodexFocus;
            deadline=lastCodexFocus+Timing.Worktree;
            status("Opening Codex chat on "+pendingWorktree.Branch+"…");
        }
        private void ObserveWorktree(double now) {
            if(now>deadline) {Clear("Could not verify the worktree chat under its sidebar project.");return;}
            IntPtr active=DesktopNative.GetForegroundWindow();
            if(!DesktopNative.IsCodex(active)) {
                if(now-lastCodexFocus>Timing.Focus) Clear("Worktree created. Return to Codex to record.");
                else if(!waitingForFocus) {waitingForFocus=true;status("Waiting for Codex to finish opening the worktree");}
                return;
            }
            lastCodexFocus=now;
            waitingForFocus=false;
            AutomationElement trustButton;
            var dialog=CodexView.ReadFolderTrust(active,pendingWorktree.Path,out trustButton);
            if(dialog!=null) {
                if(!showingTrust) {showingTrust=true;status("Waiting for Codex folder trust");}
                if(worktreeTrustVerified && !trustClickAttempted && ProjectChoices.GrantEnabled && trustButton!=null &&
                   !Cancelled() && DesktopNative.GetForegroundWindow()==active && !DesktopNative.ModifiersHeld()) {
                    trustClickAttempted=true;
                    if(CodexView.Invoke(trustButton)) {
                        status("Worktree confirmed. Waiting for the new chat…");
                        Log.Write("Confirmed the exact generated worktree in Codex: "+pendingWorktree.Branch);
                    }
                }
                return;
            }
            if(showingTrust) {
                showingTrust=false;
                status("Waiting for the new Codex chat…");
            }
            var view=CodexView.Read(active);
            if(view!=null && !view.NewComposer && view.Anchor!=anchor && view.Dictate!=null && view.Dictate.Current.IsEnabled &&
               string.IsNullOrEmpty(view.Draft) && !DesktopNative.ModifiersHeld() && now>=nextChatCheck)
            {
                VerifySidebarAndRecord(view);
            }
        }
        private void VerifySidebarAndRecord(CodexView view) {
            var target=pendingWorktree;
            var project=target.SidebarProject;
            nextChatCheck=clock.Elapsed.TotalSeconds+1;
            bool assigned=ProjectChoices.HasSidebarMembership(target.ChatId,project.Id);
            if(sidebarMoveRequested && !assigned) return;
            if(!CurrentChatFolder.IsExpectedChat(view.Window,target,Cancelled) &&
               !string.Equals(view.Document.Current.Name,target.ChatName,StringComparison.Ordinal)) return;
            string cwd=CurrentChatFolder.Read(view.Window,Cancelled);
            if(cwd==null || !string.Equals(Path.GetFullPath(cwd),target.Path,StringComparison.OrdinalIgnoreCase)) return;
            var current=CodexView.Read(view.Window);
            if(current==null || current.Anchor!=view.Anchor || !string.IsNullOrEmpty(current.Draft)) return;
            if(!assigned) {
                if(CodexSidebar.Assign(current,project,Cancelled)) {
                    sidebarMoveRequested=true;
                    Log.Write("Requested Codex sidebar membership: "+project.Name);
                    status("Waiting for Codex to file the worktree chat…");
                }
                return;
            }
            Log.Write("Verified sidebar membership and empty worktree composer: "+project.Name+" / "+target.Branch);
            StartRecording(current);
        }
        private void Observe() {
            if(phase==DictationPhase.Idle || Cancelled()) return;
            double now=clock.Elapsed.TotalSeconds;
            if(phase==DictationPhase.OpeningWorktree) {ObserveWorktree(now);return;}
            if(phase==DictationPhase.AwaitingShiftRelease) {
                if(now>deadline||DesktopNative.GetForegroundWindow()!=window) {Clear("Worktree shortcut cancelled");return;}
                if(DesktopNative.ModifiersHeld()) return;
                var source=CodexView.Read(window);
                if(Same(source)) OpenWorktree(source);
                else Clear("Chat changed before worktree creation");
                return;
            }
            if(phase==DictationPhase.AwaitingSecondPress) {
                if(DesktopNative.GetForegroundWindow()!=window) {Clear("Focus changed before dictation");return;}
                if(now-firstPress<PressTiming.DoubleClickSeconds) return;
                var destination=CodexView.Read(window);
                if(Same(destination)) StartRecording(destination);
                else Clear("Chat changed before dictation");
                return;
            }
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
                if(view.Recording) {phase=DictationPhase.Recording;deadline=now+Timing.Recording;status("Recording - click LARK to finish and send");Log.Write("Recording confirmed");}
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
                if(Cancelled()) return;
                // Fence the invocation before calling it. An unavailable element after
                // this point must never cause a second submission attempt.
                phase=DictationPhase.Submitting;deadline=now+Timing.Submission;
                if(CodexView.Invoke(current.Send)) {status("Sending dictated message");Log.Write("Native Send invoked once");}
                else Clear("Send button unavailable; draft left unsent");
                return;
            }
        }
        private void Run() {
            while(!stopping) {
                try {
                    Press press;
                    if(reset) {
                        while(queued.TryDequeue(out press)) {}
                        reset=false;Clear("Ready");
                    }
                    while(!Cancelled()&&queued.TryDequeue(out press)) HandleClick(press);
                    Observe();
                    TickCountdown();
                } catch(ElementNotAvailableException) {
                    // Chromium remounts accessibility elements during navigation and
                    // transcription. Read a fresh tree on the next observation.
                    if(clock.Elapsed.TotalSeconds>deadline && phase!=DictationPhase.Idle) Clear("Codex view changed; check your draft");
                } catch(Exception e) {Clear("Codex control unavailable; check your draft");Log.Write("Codex: "+e.GetType().Name);}
                wake.WaitOne(phase==DictationPhase.AwaitingSecondPress ? 35 : 150);
            }
        }
        public void Dispose() {stopping=true;wake.Set();if(thread.Join(1500)) wake.Dispose();}
    }
}
