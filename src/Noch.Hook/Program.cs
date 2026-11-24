using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Noch.Hook
{
    /// Passes one hook event from an agent to the notch and prints the answer, if any.
    /// It must never get in the agent's way: if the notch is closed or anything fails,
    /// it prints nothing and exits 0, which every agent reads as "no opinion".
    static class Program
    {
        static int Main(string[] args)
        {
            try { Run(args.Length > 0 ? args[0] : "claude"); } catch { }
            return 0;
        }

        static void Run(string source)
        {
            byte[] payload;
            using (var stdin = Console.OpenStandardInput())
            using (var ms = new MemoryStream()) { stdin.CopyTo(ms); payload = ms.ToArray(); }

            var me = WindowsIdentity.GetCurrent().User;
            var name = "noch-" + me.Value;
            if (!WaitNamedPipe(@"\\.\pipe\" + name, 300)) return;

            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Anonymous))
            {
                pipe.Connect(500);
                // Make sure nobody else on this PC is pretending to be the notch.
                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                if (!me.Equals(owner) && !admins.Equals(owner)) return;

                var header = source + "\n" + DateTime.UtcNow.Ticks + "\n" + string.Join(",", Ancestors());
                var w = new BinaryWriter(pipe);
                var h = Encoding.UTF8.GetBytes(header);
                w.Write(h.Length); w.Write(h);
                w.Write(payload.Length); w.Write(payload);
                w.Flush();

                var r = new BinaryReader(pipe);
                var answer = r.ReadBytes(r.ReadInt32());
                if (answer.Length > 0)
                    using (var stdout = Console.OpenStandardOutput()) stdout.Write(answer, 0, answer.Length);
            }
        }

        /// "id:name" of each process from our parent upwards, so the notch can bring the right window forward.
        static List<string> Ancestors()
        {
            var parents = new Dictionary<int, int>();
            var names = new Dictionary<int, string>();
            var snap = CreateToolhelp32Snapshot(2, 0);
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            if (Process32FirstW(snap, ref e))
                do { parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; names[(int)e.th32ProcessID] = e.szExeFile; } while (Process32NextW(snap, ref e));
            CloseHandle(snap);

            var chain = new List<string>();
            var seen = new HashSet<int>();
            var pid = (int)GetCurrentProcessId();
            while (parents.TryGetValue(pid, out var p) && p > 4 && names.ContainsKey(p) && seen.Add(p) && chain.Count < 24) { chain.Add(p + ":" + names[p].Replace(',', '_').Replace(':', '_')); pid = p; }
            return chain;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool WaitNamedPipe(string name, uint timeout);
        [DllImport("kernel32")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32")] static extern uint GetCurrentProcessId();
    }
}
