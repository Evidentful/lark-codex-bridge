using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace LarkCodex {
    internal static class Tests {
        private static int count;
        private static void Check(bool condition,string name) {if(!condition) throw new Exception("FAIL: "+name);count++;Console.WriteLine("PASS "+name);}
        private static ReceiverStatus State(byte noise) {return new ReceiverStatus {Connected=true,Noise=noise};}
        private static byte[] Report(byte noise) {var b=new byte[64];b[0]=0x55;b[1]=0xbb;b[2]=0xdd;b[3]=0x10;b[4]=0x80;b[6]=16;b[7]=1;b[13]=noise;return b;}
        internal static int Run() {
            Check(Protocol.Status(Report(3)).Noise==3,"valid padded HID status");
            Check(Protocol.Status(Report(0).Skip(1).ToArray()).Noise==0,"native callback without report-ID prefix");
            Check(Protocol.Status(new byte[]{0x55,0xbb})==null,"truncated header ignored");
            var malformed=Report(0);malformed[6]=60;Check(Protocol.Status(malformed)==null,"truncated declared payload ignored");
            var shortPayload=Report(0);shortPayload[6]=15;Check(Protocol.Status(shortPayload)==null,"short status ignored");
            var wrong=Report(0);wrong[4]=0x40;Check(Protocol.Status(wrong)==null,"wrong response destination ignored");
            wrong=Report(0);wrong[0]=0x41;Check(Protocol.Status(wrong)==null,"unrelated report ignored");
            Check(Protocol.Status(Report(4))==null,"unsupported noise setting ignored");
            var offline=Report(0);offline[7]=0;Check(!Protocol.Status(offline).Connected,"offline transmitter parsed");
            var request=Protocol.Request(0x19,new byte[]{2},64);Check(request.Length==64&&request[0]==0x55&&request[3]==0x19&&request[6]==1&&request[7]==2,"Windows output has one report ID and correct payload");
            int clicks=0,writes=0;byte original=255;
            var detector=new ButtonDetector((a,b)=>{writes++;original=b;},()=>clicks++);
            detector.Update(State(0),0);detector.Update(State(3),0.3);detector.Update(State(3),1.1);
            Check(clicks==0&&writes==0&&detector.Baseline==3,"startup transitions establish baseline without a click");
            detector.Update(State(0),1.2);Check(clicks==0&&writes==1&&original==3,"noise change requests restoration, not a premature shortcut");
            detector.Update(State(3),1.4);detector.Update(State(3),1.5);Check(clicks==1&&!detector.Pending,"only confirmed restoration emits one click");
            detector.Update(State(0),1.6);Check(writes==1,"debounce suppresses rapid duplicate transition");
            detector.Update(State(3),1.8);Check(clicks==1,"restoration reports do not retrigger");
            detector.Reset();detector.Update(State(0),10);detector.Update(State(0),11.1);detector.Update(State(2),11.2);detector.Update(State(2),11.9);detector.Update(State(2),12.5);detector.Update(State(2),13.1);
            Check(detector.Paused&&!detector.Pending&&clicks==1&&detector.Attempts==3,"failed restoration pauses after three attempts without clicking");
            detector=new ButtonDetector((a,b)=>{},()=>clicks++);detector.Update(State(0),0);detector.Update(State(0),1.1);detector.Update(State(1),1.2);detector.CancelClick();detector.Update(State(0),1.4);
            Check(clicks==1,"pause/shutdown can finish restoration without an action");
            detector.Update(new ReceiverStatus{Connected=false},2);detector.Update(State(2),3);Check(detector.Baseline==2&&!detector.Pending&&clicks==1,"reconnect resets pending actions and noise baseline");
            Func<string,string,bool,bool,bool,bool,bool,bool,string,string,bool> can=SendPolicy.CanSend;
            Check(can("chatA","chatA",true,false,false,false,true,true,"","recognized words"),"finished nonempty transcript in same chat can send");
            Check(!can("chatA","chatB",true,false,false,false,true,true,"","recognized words"),"chat switch prevents send");
            Check(!can("chatA","chatA",false,false,false,false,true,true,"","recognized words"),"focus switch prevents send");
            Check(!can("chatA","chatA",true,true,false,false,true,true,"","recognized words"),"recording prevents send");
            Check(!can("chatA","chatA",true,false,true,false,true,true,"","recognized words"),"transcription in progress prevents send");
            Check(!can("chatA","chatA",true,false,false,true,true,true,"","recognized words"),"transcription failure prevents send");
            Check(!can("chatA","chatA",true,false,false,false,true,false,"","recognized words"),"disabled Send prevents send");
            Check(!can("chatA","chatA",true,false,false,false,true,true,"","  "),"empty transcript prevents send");
            Check(!can("chatA","chatA",true,false,false,false,true,true,"existing draft","existing draft"),"unchanged preexisting draft prevents send");
            Check(!can("chatA","chatA",true,false,false,false,false,true,"","recognized words"),"Codex must return to idle dictation before send");
            Console.WriteLine("All "+count+" checks passed; no hardware, keyboard or message actions.");return 0;
        }
    }
    internal static class HardwareTests {
        private static readonly ConcurrentQueue<byte[]> reports=new ConcurrentQueue<byte[]>();
        private static ReceiverStatus Read(HidConnection connection,int timeout) {
            var watch=Stopwatch.StartNew();double poll=-1;
            while(watch.ElapsedMilliseconds<timeout) {
                if(watch.ElapsedMilliseconds>=poll) {connection.Send(0x10);poll=watch.ElapsedMilliseconds+200;}
                byte[] report;
                while(reports.TryDequeue(out report)) {var state=Protocol.Status(report);if(state!=null&&state.Connected) return state;}
                Thread.Sleep(10);
            }
            throw new Exception("No valid transmitter status");
        }
        private static ReceiverStatus WaitNoise(HidConnection conn,byte noise) {
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<2500) {var s=Read(conn,800);if(s.Noise==noise) return s;}
            throw new Exception("Noise setting was not confirmed");
        }
        internal static int Run() {
            var devices=HidNative.Enumerate().Where(x=>x.Report55).ToArray();
            if(devices.Length!=1) throw new Exception("Connect exactly one supported receiver");
            using(var conn=new HidConnection(devices[0],x=>reports.Enqueue(x))) {
                byte initial=Read(conn,2000).Noise;int passed=0;
                Console.WriteLine("Original noise setting: "+initial+". No dictation or messages will be triggered.");
                try {
                    for(byte baseline=0;baseline<=3;baseline++) {
                        var state=Read(conn,800);ReceiverEngine.RestoreNoise(conn,state.Noise,baseline);state=WaitNoise(conn,baseline);
                        for(int cycle=0;cycle<5;cycle++) {
                            int clicks=0;
                            var detector=new ButtonDetector((a,b)=>ReceiverEngine.RestoreNoise(conn,a,b),()=>clicks++);
                            detector.Update(state,-2);detector.Update(state,0);
                            conn.Send(0x19,2); // Produces the same noise-state change as the transmitter button.
                            var clock=Stopwatch.StartNew();
                            while(clock.ElapsedMilliseconds<3500&&clicks==0) {state=Read(conn,800);detector.Update(state,clock.Elapsed.TotalSeconds);Thread.Sleep(35);}
                            if(clicks!=1||state.Noise!=baseline) throw new Exception("Click/restoration cycle failed");
                            for(int i=0;i<2;i++) {state=Read(conn,800);detector.Update(state,clock.Elapsed.TotalSeconds);Thread.Sleep(100);}
                            if(clicks!=1) throw new Exception("Duplicate click after restoration");
                            passed++;Console.WriteLine("PASS receiver cycle "+passed+"; baseline="+baseline+"; one event; restored");
                        }
                    }
                    Console.WriteLine("All "+passed+" live receiver cycles passed.");return 0;
                } finally {
                    var current=Read(conn,1000);ReceiverEngine.RestoreNoise(conn,current.Noise,initial);WaitNoise(conn,initial);Console.WriteLine("Restored original noise setting: "+initial);
                }
            }
        }
    }
}
