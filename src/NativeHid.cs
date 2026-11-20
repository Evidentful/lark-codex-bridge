// Receiver protocol adapted from geekyshubham/lark-flow-bridge (MIT).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LarkCodex {
    internal static class HidNative {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Guid; public int Flags; public UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
        [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] report, int length);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        internal static bool HasReport(IntPtr pp, Caps caps, int type, byte id) {
            ushort values = type == 0 ? caps.InputValues : caps.OutputValues;
            ushort buttons = type == 0 ? caps.InputButtons : caps.OutputButtons;
            foreach (bool button in new [] {false, true}) {
                ushort count = button ? buttons : values;
                if (count == 0) continue;
                IntPtr buffer = Marshal.AllocHGlobal(72 * count);
                try {
                    int result = button ? HidP_GetButtonCaps(type, buffer, ref count, pp) : HidP_GetValueCaps(type, buffer, ref count, pp);
                    if (Environment.GetEnvironmentVariable("LARK_HID_TRACE")=="1") Console.WriteLine("CAPS_RESULT type={0} button={1} count={2} result=0x{3:x8}",type,button,count,result);
                    if (result >= 0) for (int i=0; i<count; i++) {
                        if (Environment.GetEnvironmentVariable("LARK_HID_TRACE")=="1") Console.WriteLine("CAP type={0} button={1} result=0x{2:x8} report=0x{3:x2}",type,button,result,Marshal.ReadByte(buffer,i*72+2));
                        if (Marshal.ReadByte(buffer, i*72+2) == id) return true;
                    }
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }
        internal sealed class DeviceInfo { public string Path; public Caps Caps; public bool Report55; }
        internal static List<DeviceInfo> Enumerate() {
            var result = new List<DeviceInfo>(); Guid guid; HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try {
                for (uint index=0;;index++) {
                    var item = new InterfaceData { Size=Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item)) {
                        if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception();
                        break;
                    }
                    uint required; SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail, IntPtr.Size==8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                        if (path.IndexOf("vid_3547&pid_0411", StringComparison.OrdinalIgnoreCase)<0) continue;
                        using (var handle=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if (handle.IsInvalid) continue;
                            IntPtr pp; if (!HidD_GetPreparsedData(handle,out pp)) continue;
                            try {
                                Caps caps; if (HidP_GetCaps(pp,out caps)<0) continue;
                                // This receiver describes output bytes as constants, so Windows
                                // exposes no output value caps. Match its input report ID and
                                // output length, then require a valid status response before use.
                                result.Add(new DeviceInfo {Path=path, Caps=caps, Report55=HasReport(pp,caps,0,0x55) && caps.OutputLength>=23});
                            } finally { HidD_FreePreparsedData(pp); }
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
    }

    internal sealed class HidConnection : IDisposable {
        private readonly SafeFileHandle handle;
        private readonly FileStream stream;
        private readonly byte[] input;
        private readonly int outputLength;
        private readonly Action<byte[]> receive;
        private volatile bool disposed;
        internal Exception ReadError;
        internal HidConnection(HidNative.DeviceInfo info, Action<byte[]> callback) {
            receive=callback; outputLength=info.Caps.OutputLength; input=new byte[info.Caps.InputLength];
            handle=HidNative.CreateFile(info.Path,0xc0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open the LARK control interface");
            stream=new FileStream(handle,FileAccess.ReadWrite,info.Caps.InputLength,true);
            ReadNext();
        }
        private void ReadNext() {
            if (disposed) return;
            try { stream.BeginRead(input,0,input.Length,ReadDone,null); }
            catch(Exception e) { if(!disposed) ReadError=e; }
        }
        private void ReadDone(IAsyncResult ar) {
            try {
                int count=stream.EndRead(ar);
                if (disposed) return;
                if (count==0) {ReadError=new IOException("Receiver disconnected");return;}
                var report=new byte[count]; Buffer.BlockCopy(input,0,report,0,count); receive(report); ReadNext();
            } catch(Exception e) { if(!disposed) ReadError=e; }
        }
        internal void Send(byte command, params byte[] payload) {
            if(disposed) throw new ObjectDisposedException("HID");
            byte[] packet=Protocol.Request(command,payload,outputLength);
            // The LARK responds on Windows to interrupt OUT (WriteFile), not
            // HidD_SetOutputReport's control-endpoint SET_REPORT request.
            var write=stream.BeginWrite(packet,0,packet.Length,null,null);
            using(var done=write.AsyncWaitHandle) {
                if(!done.WaitOne(1000)) {
                    HidNative.CancelIoEx(handle,IntPtr.Zero);
                    try {stream.EndWrite(write);} catch(IOException) {}
                    throw new IOException("LARK control write timed out");
                }
                stream.EndWrite(write);
            }
        }
        public void Dispose() { disposed=true; HidNative.CancelIoEx(handle,IntPtr.Zero); stream.Dispose(); handle.Dispose(); }
    }

    internal sealed class ReceiverStatus { internal bool Connected; internal byte Noise; internal bool Muted; }
    internal static class Protocol {
        internal static byte[] Request(byte command, byte[] payload, int length) {
            if(length<7+payload.Length) throw new ArgumentException("HID report too short");
            var b=new byte[length]; b[0]=0x55;b[1]=0xaa;b[2]=0xdd;b[3]=command;b[4]=0x40;b[5]=(byte)(payload.Length>>8);b[6]=(byte)payload.Length;
            Buffer.BlockCopy(payload,0,b,7,payload.Length);return b;
        }
        internal static byte[] Payload(byte[] report, byte command, int minimum) {
            int start=report.Length>0 && report[0]==0x55 ? 1 : 0;
            if(report.Length<start+6 || report[start]!=0xbb || report[start+1]!=0xdd || report[start+2]!=command || report[start+3]!=0x80) return null;
            int n=report[start+4]*256+report[start+5];
            if(n<minimum || report.Length<start+6+n) return null;
            var p=new byte[n];Buffer.BlockCopy(report,start+6,p,0,n);return p;
        }
        internal static ReceiverStatus Status(byte[] report) {
            var p=Payload(report,0x10,16); if(p==null || p[6]>3) return null;
            return new ReceiverStatus {Connected=p[0]==1 || p[1]==1,Noise=p[6],Muted=(p[0]==1 && (p[15]&1)!=0) || (p[1]==1 && (p[15]&2)!=0)};
        }
    }
}
