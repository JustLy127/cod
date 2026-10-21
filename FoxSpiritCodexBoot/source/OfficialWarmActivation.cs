// Restore Codex through Electron's own single-instance activation path. Showing
// its HWND directly does not update Electron's hidden/background lifecycle.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FoxSpiritCodexBoot {
    internal static class OfficialWarmActivation {
        internal sealed class VerifiedListener {
            public string Address;
            public int Port;
            public int ProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRowOwnerPid {
            public uint State,LocalAddress,LocalPort,RemoteAddress,RemotePort,ProcessId;
        }

        [DllImport("iphlpapi.dll",SetLastError=true)]
        private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool ordered,uint addressFamily,int tableClass,uint reserved);

        [ComImport,Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments,uint options,out uint processId);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                IntPtr itemArray,[MarshalAs(UnmanagedType.LPWStr)] string verb,out uint processId);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                IntPtr itemArray,out uint processId);
        }

        [DllImport("ole32.dll",ExactSpelling=true)]
        private static extern int CoCreateInstance(ref Guid classId,IntPtr outer,uint context,
            ref Guid interfaceId,[MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);

        internal static bool IsOfficialRootPath(string path) {
            return !String.IsNullOrWhiteSpace(path)&&Regex.IsMatch(path,
                @"^C:\\Program Files\\WindowsApps\\OpenAI\.Codex_\d+\.\d+\.\d+\.\d+_(?:x64|arm64|x86)__2p2nqsd0c76g0\\app\\ChatGPT\.exe$",
                RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        }

        // Pure Windows command-line tokenization keeps quoted executable paths
        // and quoted switch values usable without starting any helper process.
        private static bool TryTokenize(string commandLine,out List<string> arguments) {
            arguments=new List<string>();
            if(String.IsNullOrWhiteSpace(commandLine))return false;
            int index=0;
            while(index<commandLine.Length) {
                while(index<commandLine.Length&&Char.IsWhiteSpace(commandLine[index]))index++;
                if(index==commandLine.Length)break;
                StringBuilder value=new StringBuilder();bool quoted=false;
                while(index<commandLine.Length) {
                    if(!quoted&&Char.IsWhiteSpace(commandLine[index]))break;
                    int slashCount=0;
                    while(index<commandLine.Length&&commandLine[index]=='\\') { slashCount++;index++; }
                    if(index<commandLine.Length&&commandLine[index]=='"') {
                        value.Append('\\',slashCount/2);
                        if((slashCount&1)!=0)value.Append('"');
                        else if(quoted&&index+1<commandLine.Length&&commandLine[index+1]=='"') { value.Append('"');index++; }
                        else quoted=!quoted;
                        index++;continue;
                    }
                    value.Append('\\',slashCount);
                    if(index==commandLine.Length||(!quoted&&Char.IsWhiteSpace(commandLine[index])))break;
                    value.Append(commandLine[index++]);
                }
                if(quoted)return false;
                arguments.Add(value.ToString());
            }
            return arguments.Count>0;
        }

        internal static bool TryParseEndpoint(string commandLine,out int port) {
            port=0;List<string> arguments;
            if(!TryTokenize(commandLine,out arguments))return false;
            bool foundPort=false,foundAddress=false;
            for(int index=0;index<arguments.Count;index++) {
                string argument=arguments[index],value;
                if(argument.Equals("--",StringComparison.Ordinal))break;
                if(argument.Equals("--type",StringComparison.OrdinalIgnoreCase)||argument.StartsWith("--type=",StringComparison.OrdinalIgnoreCase)
                    ||argument.Equals("--remote-debugging-pipe",StringComparison.OrdinalIgnoreCase)
                    ||argument.StartsWith("--remote-debugging-pipe=",StringComparison.OrdinalIgnoreCase))return false;
                if(argument.Equals("--remote-debugging-port",StringComparison.OrdinalIgnoreCase)) {
                    if(index+1>=arguments.Count)return false;
                    value=arguments[++index];
                } else if(argument.StartsWith("--remote-debugging-port=",StringComparison.OrdinalIgnoreCase)) {
                    value=argument.Substring("--remote-debugging-port=".Length);
                } else value=null;
                if(value!=null) {
                    int parsed;
                    if(foundPort||!Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)||parsed<1024||parsed>65535)return false;
                    port=parsed;foundPort=true;continue;
                }
                if(argument.Equals("--remote-debugging-address",StringComparison.OrdinalIgnoreCase)) {
                    if(index+1>=arguments.Count)return false;
                    value=arguments[++index];
                } else if(argument.StartsWith("--remote-debugging-address=",StringComparison.OrdinalIgnoreCase)) {
                    value=argument.Substring("--remote-debugging-address=".Length);
                } else value=null;
                if(value!=null) {
                    if(foundAddress||!value.Equals("127.0.0.1",StringComparison.Ordinal))return false;
                    foundAddress=true;
                }
            }
            // Chromium may omit its default bind address; actual TCP ownership
            // is always checked separately and must still be exactly loopback.
            return foundPort;
        }

        internal static bool OwnsExclusiveLoopback(IEnumerable<VerifiedListener> listeners,int processId,int port) {
            if(listeners==null||processId<=0||port<1024||port>65535)return false;
            bool found=false;
            foreach(VerifiedListener listener in listeners) {
                if(listener==null||listener.Port!=port)continue;
                if(listener.ProcessId!=processId||listener.Address!="127.0.0.1")return false;
                found=true;
            }
            return found;
        }

        private static List<VerifiedListener> ReadIpv4Listeners() {
            const uint AfInet=2,Success=0,InsufficientBuffer=122;
            const int OwnerPidListener=3;
            int size=0;
            uint result=GetExtendedTcpTable(IntPtr.Zero,ref size,true,AfInet,OwnerPidListener,0);
            if(result!=InsufficientBuffer&&result!=Success)throw new InvalidOperationException("无法核验 Codex 的本机端口。");
            for(int attempt=0;attempt<3;attempt++) {
                if(size<4||size>16*1024*1024)throw new InvalidOperationException("本机端口信息大小异常。");
                int allocated=size;IntPtr buffer=Marshal.AllocHGlobal(allocated);
                try {
                    result=GetExtendedTcpTable(buffer,ref size,true,AfInet,OwnerPidListener,0);
                    if(result==InsufficientBuffer)continue;
                    if(result!=Success)throw new InvalidOperationException("无法读取 Codex 的本机端口归属。");
                    int rowSize=Marshal.SizeOf(typeof(TcpRowOwnerPid));
                    uint count=unchecked((uint)Marshal.ReadInt32(buffer));
                    if(size<4||size>allocated||count>(uint)((size-4)/rowSize))throw new InvalidOperationException("本机端口信息不完整。");
                    List<VerifiedListener> listeners=new List<VerifiedListener>();
                    for(uint index=0;index<count;index++) {
                        IntPtr rowAddress=new IntPtr(buffer.ToInt64()+4+(long)index*rowSize);
                        TcpRowOwnerPid row=(TcpRowOwnerPid)Marshal.PtrToStructure(rowAddress,typeof(TcpRowOwnerPid));
                        int port=(int)(((row.LocalPort&255)<<8)|((row.LocalPort>>8)&255));
                        string address=(row.LocalAddress&255)+"."+((row.LocalAddress>>8)&255)+"."+((row.LocalAddress>>16)&255)+"."+((row.LocalAddress>>24)&255);
                        if(row.ProcessId<=Int32.MaxValue)listeners.Add(new VerifiedListener {Address=address,Port=port,ProcessId=(int)row.ProcessId});
                    }
                    return listeners;
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("本机端口正在变化，请稍后重试。");
        }

        internal static uint Activate(Config config,PrimaryIdentity identity) {
            if(config==null||identity==null||!Native.IdentityOwnerAlive(config,identity))
                throw new InvalidOperationException("原 Codex 进程已经退出，未发送恢复请求。");
            string expectedPath=Native.ProcessPath(identity.ProcessId);
            if(!IsOfficialRootPath(expectedPath))throw new InvalidOperationException("无法核验正在运行的官方 Codex 路径。");
            string commandLine=null,observedPath=null;int matches=0;
            using(ManagementObjectSearcher searcher=new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId="+identity.ProcessId.ToString(CultureInfo.InvariantCulture))) {
                using(ManagementObjectCollection processes=searcher.Get()) {
                    foreach(ManagementObject process in processes) {
                        using(process) {
                            matches++;observedPath=Convert.ToString(process["ExecutablePath"],CultureInfo.InvariantCulture);
                            commandLine=Convert.ToString(process["CommandLine"],CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            int port;
            if(matches!=1||!String.Equals(expectedPath,observedPath,StringComparison.OrdinalIgnoreCase)||!TryParseEndpoint(commandLine,out port))
                throw new InvalidOperationException("当前 Codex 没有可核验的主题启动端口，请先正常退出后再从苏苏入口启动。");
            if(!OwnsExclusiveLoopback(ReadIpv4Listeners(),identity.ProcessId,port))
                throw new InvalidOperationException("Codex 本机端口归属不符，未发送恢复请求。");

            Guid classId=new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
            Guid interfaceId=new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
            IApplicationActivationManager manager;
            int created=CoCreateInstance(ref classId,IntPtr.Zero,4,ref interfaceId,out manager);
            if(created<0)Marshal.ThrowExceptionForHR(created);
            if(manager==null)throw new InvalidOperationException("Windows 的 Codex 启动入口不可用。");
            try {
                // Recheck after WMI/COM work. In the remaining unavoidable exit
                // race, the same verified CDP arguments keep a new root managed.
                if(!Native.IdentityOwnerAlive(config,identity)||!String.Equals(Native.ProcessPath(identity.ProcessId),expectedPath,StringComparison.OrdinalIgnoreCase)
                    ||!OwnsExclusiveLoopback(ReadIpv4Listeners(),identity.ProcessId,port))
                    throw new InvalidOperationException("原 Codex 正在退出，未发送恢复请求。");
                uint processId;
                string arguments="--remote-debugging-address=127.0.0.1 --remote-debugging-port="+port.ToString(CultureInfo.InvariantCulture);
                int activated=manager.ActivateApplication("OpenAI.Codex_2p2nqsd0c76g0!App",arguments,2,out processId);
                if(activated<0)Marshal.ThrowExceptionForHR(activated);
                if(processId==0)throw new InvalidOperationException("Windows 没有返回 Codex 恢复结果。");
                return processId;
            } finally { Marshal.FinalReleaseComObject(manager); }
        }
    }
}
