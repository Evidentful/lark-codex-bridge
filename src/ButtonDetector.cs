using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace LarkCodex {
    // Pure protocol state machine: only confirmed restorations produce clicks.
    internal sealed class ButtonDetector {
        internal byte? Baseline;
        internal bool Pending, Paused;
        internal int Attempts;
        internal string Status="Waiting for transmitter";
        private double settleUntil, suppressUntil, retryAt;
        private bool allowClick;
        private readonly Action<byte,byte> restore;
        private readonly Action click;
        internal ButtonDetector(Action<byte,byte> restoreNoise,Action onClick) {restore=restoreNoise;click=onClick;}
        internal void Reset() {Baseline=null;Pending=false;allowClick=false;Attempts=0;Status="Waiting for transmitter";}
        internal void CancelClick() {allowClick=false;}
        internal void Update(ReceiverStatus state,double now) {
            if(!state.Connected) {Reset();return;}
            if(state.AllMuted) {
                Reset();Status="Mic muted. Double-tap quickly to unmute, then space worktree presses 1 second apart.";return;
            }
            if(!Baseline.HasValue) {Baseline=state.Noise;settleUntil=now+1;Status="Settling receiver";return;}
            if(Pending) {
                if(state.Noise==Baseline.Value) {
                    Pending=false;suppressUntil=now+0.09;
                    bool emit=allowClick&&!Paused;allowClick=false;Status=Paused?"Paused":"Ready";
                    if(emit) click();return;
                }
                if(now>=retryAt) {
                    if(Attempts>=3) {Pending=false;allowClick=false;Paused=true;Status="Paused: noise restoration failed";return;}
                    Attempts++;restore(state.Noise,Baseline.Value);retryAt=now+0.5;
                }
                return;
            }
            if(Paused) {Baseline=state.Noise;Status="Paused";return;}
            if(now<settleUntil) {Baseline=state.Noise;return;}
            if(now<suppressUntil) return;
            if(state.Noise==Baseline.Value) {Status="Ready";return;}
            Pending=true;allowClick=true;Attempts=1;retryAt=now+0.6;Status="Restoring noise setting";
            restore(state.Noise,Baseline.Value);
        }
    }
    internal sealed class ReceiverEngine : IDisposable {
        private readonly Thread thread;
        private readonly ConcurrentQueue<byte[]> reports=new ConcurrentQueue<byte[]>();
        private readonly Action<string> status;
        private readonly Action clicked;
        private volatile bool stopping,paused;
        private HidConnection connection;
        private ButtonDetector detector;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        internal ReceiverEngine(Action onClick,Action<string> reportStatus) {
            clicked=onClick;status=reportStatus;thread=new Thread(Run){IsBackground=true,Name="LARK receiver"};thread.Start();
        }
        internal void SetPaused(bool value) {paused=value;}
        private void Restore(byte current,byte original) {RestoreNoise(connection,current,original);}
        internal static void RestoreNoise(HidConnection conn,byte current,byte original) {
            if((current>0)!=(original>0)) {
                conn.Send(0x19,2);
                if(original>0) {Thread.Sleep(80);conn.Send(0x06,original);}
            } else if(original>0 && original!=current) conn.Send(0x06,original);
        }
        private void Run() {
            string previous=null;
            while(!stopping) {
                try {
                    var devices=HidNative.Enumerate().FindAll(x=>x.Report55);
                    if(devices.Count==0) {Publish("Waiting for LARK receiver",ref previous);Thread.Sleep(1000);continue;}
                    if(devices.Count>1) {Publish("Paused: multiple LARK receivers connected",ref previous);Thread.Sleep(1000);continue;}
                    byte[] stale;while(reports.TryDequeue(out stale)) {}
                    using(connection=new HidConnection(devices[0],x=>reports.Enqueue(x))) {
                        detector=new ButtonDetector(Restore,clicked);
                        double nextPoll=0,lastStatus=clock.Elapsed.TotalSeconds,shutdownDeadline=0;
                        bool appliedPause=paused;
                        connection.Send(0x41);
                        while(true) {
                            double now=clock.Elapsed.TotalSeconds;
                            if(stopping) {
                                detector.CancelClick();
                                if(!detector.Pending) break;
                                if(shutdownDeadline==0) shutdownDeadline=now+2;
                                if(now>shutdownDeadline) break;
                            }
                            if(paused!=appliedPause) {
                                appliedPause=paused;
                                detector.CancelClick();
                                // Resume re-establishes a fresh, settled baseline.
                                if(!paused&&!detector.Pending) detector.Reset();
                            }
                            detector.Paused=paused;
                            byte[] report;
                            while(reports.TryDequeue(out report)) {
                                var firmware=Protocol.Payload(report,0x41,6);
                                if(firmware!=null) Log.Write(string.Format("Receiver firmware {0}.{1}.{2}.{3}{4}{5}",firmware[0],firmware[1],firmware[2],firmware[3],firmware[4],firmware[5]));
                                var state=Protocol.Status(report);
                                if(state==null) continue;
                                lastStatus=now;detector.Update(state,now);
                                if(detector.Paused&&!paused) paused=true;
                                Publish(detector.Status,ref previous);
                            }
                            if(connection.ReadError!=null) throw new System.IO.IOException("Receiver disconnected",connection.ReadError);
                            if(now-lastStatus>2) throw new System.IO.IOException("Receiver status timed out");
                            if(now>=nextPoll) {connection.Send(0x10);nextPoll=now+0.075;}
                            Thread.Sleep(15);
                        }
                    }
                } catch(Exception e) {
                    Log.Write("Receiver: "+e.Message);Publish("Waiting: reconnect receiver if needed",ref previous);
                    if(!stopping) Thread.Sleep(1000);
                } finally {connection=null;detector=null;}
            }
        }
        private void Publish(string value,ref string previous) {if(value!=previous) {previous=value;status(value);Log.Write(value);}}
        public void Dispose() {stopping=true;thread.Join(3500);}
    }
}
