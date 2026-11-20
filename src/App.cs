using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

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
    internal sealed class BridgeWindow : Form {
        private ReceiverEngine receiver;
        private DictationController controller;
        private readonly EventWaitHandle stop;
        private readonly EventWaitHandle show;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Button startButton,stopButton;
        private readonly Label statusLabel;
        private bool closing;
        private int generation;
        private string receiverStatus="Waiting for receiver",dictationStatus="";
        internal BridgeWindow(bool startOnOpen) {
            Text="LARK Codex";
            Font=new Font("Segoe UI",9F);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(340,168);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false;MinimizeBox=true;
            StartPosition=FormStartPosition.CenterScreen;
            Icon=SystemIcons.Application;
            var title=new Label {Text="LARK A2  →  Codex",AutoSize=true,Location=new Point(14,13)};
            startButton=new Button {Text="Start",Name="StartButton",AccessibleName="Start",Location=new Point(14,40),Size=new Size(150,34)};
            stopButton=new Button {Text="Stop",Name="StopButton",AccessibleName="Stop",Location=new Point(176,40),Size=new Size(150,34),Enabled=false};
            statusLabel=new Label {Text="Stopped",Name="BridgeStatus",Location=new Point(14,84),Size=new Size(312,36),AutoEllipsis=true};
            var hint=new Label {Text="In Codex: click LARK to record.\r\nClick again to finish and send.",Location=new Point(14,124),Size=new Size(312,34),ForeColor=SystemColors.GrayText};
            Controls.AddRange(new Control[]{title,startButton,stopButton,statusLabel,hint});
            startButton.Click+=delegate {StartBridge();};stopButton.Click+=delegate {StopBridge();};
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,AppSignals.StopName);stop.Reset();
            show=new EventWaitHandle(false,EventResetMode.AutoReset,AppSignals.ShowName);
            timer=new System.Windows.Forms.Timer {Interval=200};
            timer.Tick+=delegate {
                if(stop.WaitOne(0)) Close();
                else if(show.WaitOne(0)) {Show();if(WindowState==FormWindowState.Minimized) WindowState=FormWindowState.Normal;Activate();}
            };
            timer.Start();
            Shown+=delegate {if(startOnOpen) StartBridge();};
            FormClosing+=delegate {closing=true;timer.Stop();StopBridge();};
            FormClosed+=delegate {stop.Dispose();show.Dispose();timer.Dispose();};
        }
        private void StartBridge() {
            if(controller!=null||closing) return;
            int current=++generation;
            receiverStatus="Waiting for receiver";dictationStatus="";
            startButton.Enabled=false;stopButton.Enabled=true;statusLabel.Text="Starting…";
            controller=new DictationController(s=>Post(current,delegate {dictationStatus=s;UpdateStatus();}));
            receiver=new ReceiverEngine(controller.Click,s=>Post(current,delegate {
                receiverStatus=s;
                if(s.StartsWith("Paused:",StringComparison.Ordinal)) {
                    StopBridge();statusLabel.Text=s+". Press Start to retry.";return;
                }
                else if(s.StartsWith("Waiting",StringComparison.Ordinal)) controller.CancelPending();
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
            var oldController=controller;var oldReceiver=receiver;controller=null;receiver=null;
            if(oldController!=null) oldController.Dispose();
            if(oldReceiver!=null) oldReceiver.Dispose();
            startButton.Enabled=true;stopButton.Enabled=false;statusLabel.Text="Stopped";
            if(oldController!=null||oldReceiver!=null) Log.Write("Bridge stopped");
        }
    }
}
