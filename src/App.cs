using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace LarkCodex {
    internal static class Log {
        private static readonly object gate=new object();
        private static readonly string Base=AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string FilePath=Path.Combine(Directory.Exists(Path.Combine(Base,"..","src"))?Path.GetFullPath(Path.Combine(Base,"..")):Base,"bridge.log");
        internal static void Write(string message) {
            lock(gate) {try {
                if(File.Exists(FilePath)&&new FileInfo(FilePath).Length>262144) {if(File.Exists(FilePath+".previous")) File.Delete(FilePath+".previous");File.Move(FilePath,FilePath+".previous");}
                File.AppendAllText(FilePath,DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")+" "+message+Environment.NewLine);
            } catch { /* Logging must never prevent cancellation or recovery. */ }}
        }
    }
    internal static class AppSignals {
        internal const string StopName="Local\\LarkCodex.Stop.v1";
        internal const string ShowName="Local\\LarkCodex.Show.v1";
        internal const string MutexName="Local\\LarkCodex.Bridge.v1";
        internal static void ShowExisting() {
            try {using(var signal=EventWaitHandle.OpenExisting(ShowName)) signal.Set();}
            catch(WaitHandleCannotBeOpenedException) {}
        }
    }
    internal static class Timing {
        internal static double Startup=8,Recording=600,Transcription=60,Submission=8,Worktree=45,Focus=12;
    }
    internal sealed class CountdownBar : Control {
        internal double Fraction;
        internal bool Red;
        internal CountdownBar() {DoubleBuffered=true;AccessibleName="Remaining click acceptance time";}
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(SystemColors.ControlDark);
            using(var brush=new SolidBrush(Red?Color.Firebrick:Color.SeaGreen))
                e.Graphics.FillRectangle(brush,0,0,(int)(ClientSize.Width*Fraction),ClientSize.Height);
        }
    }
    internal sealed class BridgeWindow : Form {
        private ReceiverEngine receiver;
        private DictationController controller;
        private readonly EventWaitHandle stop;
        private readonly EventWaitHandle show;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Button startButton,stopButton;
        private readonly Label statusLabel,hint;
        private readonly ComboBox projectPicker;
        private readonly CheckBox allowWorktrees;
        private FlowLayoutPanel timingPanel;
        private CountdownBar countdown;
        private Label countdownText;
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int cx,int cy,uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        protected override bool ShowWithoutActivation {get {return true;}}
        private void RevealWithoutFocus() {
            if(WindowState==FormWindowState.Minimized) ShowWindow(Handle,4);
            Show();SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
        }
        private bool closing;
        private int generation;
        private string receiverStatus="Waiting for receiver",dictationStatus="";
        internal BridgeWindow(bool startOnOpen) {
            Text="LARK Codex";
            Font=new Font("Segoe UI",9F);
            AutoScaleMode=AutoScaleMode.Dpi;
            AutoScaleDimensions=new SizeF(96F,96F);
            ClientSize=new Size(580,680);
            MinimumSize=new Size(480,580);
            FormBorderStyle=FormBorderStyle.Sizable;
            MaximizeBox=true;MinimizeBox=true;
            StartPosition=FormStartPosition.CenterScreen;
            Icon=SystemIcons.Application;
            var title=new Label {Text="LARK A2  →  Codex",AutoSize=true,Location=new Point(14,13)};
            startButton=new Button {Text="Start",Name="StartButton",AccessibleName="Start",Location=new Point(14,40),Size=new Size(150,34)};
            stopButton=new Button {Text="Stop",Name="StopButton",AccessibleName="Stop",Location=new Point(176,40),Size=new Size(150,34),Enabled=false};
            var projectLabel=new Label {Text="Project for new worktrees",Location=new Point(14,84),AutoSize=true};
            projectPicker=new ComboBox {
                AccessibleName="Project for new worktrees",Location=new Point(14,105),Size=new Size(312,24),
                DropDownStyle=ComboBoxStyle.DropDownList,DropDownWidth=580
            };
            LoadProjects(ProjectChoices.Load());
            projectPicker.DropDown+=delegate {
                var choice=projectPicker.SelectedItem as LocalProject;
                LoadProjects(choice==null ? null : choice.Folder);
            };
            allowWorktrees=new CheckBox {
                Text="Enable new worktrees",Location=new Point(14,134),AutoSize=true,Checked=ProjectChoices.GrantEnabled
            };
            allowWorktrees.CheckedChanged+=ConfigureWorktreeTrust;
            statusLabel=new Label {Text="Stopped",Name="BridgeStatus",Location=new Point(14,163),Size=new Size(312,39),AutoEllipsis=true};
            hint=new Label {
                Location=new Point(14,206),Size=new Size(316,50),ForeColor=SystemColors.GrayText
            };
            UpdateGestureHint();
            Controls.AddRange(new Control[]{title,startButton,stopButton,projectLabel,projectPicker,allowWorktrees,statusLabel,hint});
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=11,Padding=new Padding(14),AutoScroll=true};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(Control control in new Control[]{title,startButton,stopButton,projectLabel,projectPicker,allowWorktrees,statusLabel,hint}) {
                Controls.Remove(control);control.Dock=DockStyle.Top;control.Margin=new Padding(0,4,0,4);
                if(control==statusLabel) {control.Height=56;statusLabel.AutoEllipsis=false;}
                if(control==hint) control.Height=64;
                layout.Controls.Add(control);
            }
            countdownText=new Label {AutoSize=true,Dock=DockStyle.Top,Text="Countdown appears after a LARK press"};
            countdown=new CountdownBar {Height=22,Dock=DockStyle.Top};
            var timingBox=new GroupBox {Text="Durations (seconds) - session settings; change while stopped",Dock=DockStyle.Top,Height=225};
            timingPanel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
            timingBox.Controls.Add(timingPanel);
            AddTiming("Second-press window",PressTiming.DoubleClickSeconds,0.8,10,v=>PressTiming.DoubleClickSeconds=v);
            AddTiming("Dictation startup / Shift release",Timing.Startup,1,60,v=>Timing.Startup=v);
            AddTiming("Recording acceptance window",Timing.Recording,5,3600,v=>Timing.Recording=v);
            AddTiming("Transcription timeout",Timing.Transcription,5,600,v=>Timing.Transcription=v);
            AddTiming("Send confirmation",Timing.Submission,1,60,v=>Timing.Submission=v);
            AddTiming("Worktree open timeout",Timing.Worktree,5,300,v=>Timing.Worktree=v);
            AddTiming("Worktree focus grace",Timing.Focus,1,60,v=>Timing.Focus=v);
            timingPanel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(390,0),Text="Fixed reference: 0.60 s no-go spacing (conservative guidance; firmware threshold unknown)."});
            timingPanel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(390,0),Text="Receiver: settle 1 s; debounce 0.09 s; restore retry 0.6 / 0.5 s (3 attempts)."});
            timingPanel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(390,0),Text="Receiver: poll 0.075 s; reconnect 1 s; status timeout 2 s; shutdown restore 2 s."});
            layout.Controls.Add(countdownText);layout.Controls.Add(countdown);layout.Controls.Add(timingBox);
            Controls.Add(layout);
            startButton.Click+=delegate {StartBridge();};stopButton.Click+=delegate {StopBridge();};
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,AppSignals.StopName);stop.Reset();
            show=new EventWaitHandle(false,EventResetMode.AutoReset,AppSignals.ShowName);
            timer=new System.Windows.Forms.Timer {Interval=50};
            timer.Tick+=delegate {
                if(stop.WaitOne(0)) Close();
                else if(show.WaitOne(0)) {Show();if(WindowState==FormWindowState.Minimized) WindowState=FormWindowState.Normal;Activate();}
            };
            timer.Start();
            Shown+=delegate {if(startOnOpen) StartBridge();};
            FormClosing+=delegate {closing=true;timer.Stop();StopBridge();};
            FormClosed+=delegate {stop.Dispose();show.Dispose();timer.Dispose();};
        }
        private void AddTiming(string name,double value,double min,double max,Action<double> change) {
            var row=new FlowLayoutPanel {Width=400,Height=28,WrapContents=false};
            row.Controls.Add(new Label {Text=name,Width=280,Height=24});
            var input=new NumericUpDown {Minimum=(decimal)min,Maximum=(decimal)max,DecimalPlaces=2,Increment=0.1M,Value=(decimal)value,Width=85,AccessibleName=name};
            input.ValueChanged+=delegate {change((double)input.Value);};
            row.Controls.Add(input);timingPanel.Controls.Add(row);
        }
        private void ConfigureWorktreeTrust(object sender,EventArgs args) {
            bool enabled=allowWorktrees.Checked;
            if(enabled && MessageBox.Show(this,
                "Allow Codex to trust new worktrees created by LARK from repositories you already trust in Codex?\r\n\r\nTrusted worktrees can load and run the repository's project settings. This skips the folder prompt for worktrees created by LARK.",
                "Enable new worktrees",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) {
                allowWorktrees.Checked=false;
                return;
            }
            try {ProjectChoices.SetGrant(enabled);}
            catch(Exception e) {
                Log.Write("Could not save worktree preference: "+e.GetType().Name);
                allowWorktrees.CheckedChanged-=ConfigureWorktreeTrust;
                allowWorktrees.Checked=ProjectChoices.GrantEnabled;
                allowWorktrees.CheckedChanged+=ConfigureWorktreeTrust;
                statusLabel.Text="Could not save worktree preference";
            }
            UpdateGestureHint();
        }
        private void UpdateGestureHint() {
            hint.Text=allowWorktrees.Checked
                ? "New worktree: hold Shift, press LARK, release Shift.\r\nOr press twice about 1 second apart.\r\nWhile recording, press once to finish and send."
                : "Worktree gesture is off. Enable new worktrees to use it.\r\nOne press starts or finishes dictation in Codex.";
        }
        private void LoadProjects(string selectedFolder) {
            var items=ProjectChoices.Read();
            var selected=items.FirstOrDefault(p=>string.Equals(p.Folder,selectedFolder,StringComparison.OrdinalIgnoreCase));
            if(selected==null && !string.IsNullOrEmpty(selectedFolder)) {
                selected=new LocalProject {Folder=selectedFolder,Label=selectedFolder+" (unavailable in Codex list)"};
                items.Add(selected);
            }
            projectPicker.BeginUpdate();
            projectPicker.Items.Clear();
            projectPicker.Items.AddRange(items.ToArray());
            projectPicker.SelectedItem=selected??items[0];
            projectPicker.EndUpdate();
        }
        private void StartBridge() {
            if(controller!=null||closing) return;
            var selection=projectPicker.SelectedItem as LocalProject;
            string selectedProject=selection==null ? null : selection.Folder;
            try {ProjectChoices.Save(selectedProject);}
            catch(Exception e) {Log.Write("Could not save project choice: "+e.GetType().Name);}
            int current=++generation;
            receiverStatus="Waiting for receiver";dictationStatus="";
            startButton.Enabled=false;stopButton.Enabled=true;projectPicker.Enabled=false;allowWorktrees.Enabled=false;statusLabel.Text="Starting…";
            controller=new DictationController(s=>Post(current,delegate {dictationStatus=s;UpdateStatus();}),selectedProject,allowWorktrees.Checked);
            timingPanel.Enabled=false;
            controller.Countdown=(label,left,total,red)=>Post(current,delegate {
                countdownText.Text=label+" - "+left.ToString("F2")+" s";
                countdown.Fraction=total>0?Math.Min(1,left/total):0;countdown.Red=red;countdown.Invalidate();
                if(label!="Ready" && DesktopNative.IsCodex(DesktopNative.GetForegroundWindow())) RevealWithoutFocus();
                else if(label=="Ready") TopMost=false;
            });
            receiver=new ReceiverEngine(controller.Click,s=>Post(current,delegate {
                receiverStatus=s;
                if(s.StartsWith("Paused:",StringComparison.Ordinal)) {
                    StopBridge();statusLabel.Text=s+". Press Start to retry.";return;
                }
                else if(s.StartsWith("Waiting",StringComparison.Ordinal) || s.StartsWith("Mic muted",StringComparison.Ordinal)) controller.CancelPending();
                UpdateStatus();
            }));
            Log.Write("Bridge started from window; Codex foreground only; stop/transcribe/send enabled");
        }
        private void Post(int current,Action action) {
            try {if(!closing) BeginInvoke(new Action(delegate {if(!closing&&current==generation) action();}));}
            catch(InvalidOperationException) {}
        }
        private void UpdateStatus() {
            statusLabel.Text=receiverStatus!="Ready"||string.IsNullOrEmpty(dictationStatus)?receiverStatus:dictationStatus;
        }
        private void StopBridge() {
            ++generation;
            timingPanel.Enabled=true;TopMost=false;countdown.Fraction=0;countdown.Invalidate();countdownText.Text="Stopped";
            var oldController=controller;var oldReceiver=receiver;controller=null;receiver=null;
            if(oldController!=null) oldController.Dispose();
            if(oldReceiver!=null) oldReceiver.Dispose();
            startButton.Enabled=true;stopButton.Enabled=false;projectPicker.Enabled=true;allowWorktrees.Enabled=true;statusLabel.Text="Stopped";
            if(oldController!=null||oldReceiver!=null) Log.Write("Bridge stopped");
        }
    }
}
