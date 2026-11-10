using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Which programs are showing the nocat.cam virtual camera right now. Readers (Discord, browsers...)
/// open the shared picture queue "OBSVirtualCamVideo" only while they actually show the camera, so
/// we look through other programs' open handles of that kind and ask Windows for each one's name.
/// </summary>
public static unsafe class VcamUsers
{
    const string Name = "OBSVirtualCamVideo";

    [StructLayout(LayoutKind.Sequential)]
    struct Entry
    {
        public nint Object; public nint Pid; public nint Handle; public uint Access;
        public ushort Creator; public ushort TypeIndex; public uint Attributes; public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UStr { public ushort Length; public ushort Max; public nint Buffer; }

    [DllImport("ntdll")] static extern uint NtQuerySystemInformation(int cls, void* buf, int len, out int ret);
    [DllImport("ntdll")] static extern uint NtQueryObject(nint h, int cls, void* buf, int len, out int ret);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32")] static extern nint GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] static extern bool DuplicateHandle(nint srcProc, nint src, nint dstProc, out nint dst, uint access, bool inherit, uint options);

    /// <summary>PIDs other than ours holding the virtual camera picture open.</summary>
    public static HashSet<int> Readers()
    {
        var found = new HashSet<int>();
        nint mine = OpenFileMappingW(4, false, Name);
        if (mine == 0) return found;               // nobody is sending, so nobody can be reading
        int me = Environment.ProcessId;
        var procs = new Dictionary<int, nint>();
        byte[]? table = null;
        try
        {
            int size = 1 << 22;
            while (true)
            {
                table = new byte[size];
                uint st;
                fixed (byte* p = table) st = NtQuerySystemInformation(64, p, size, out int ret);
                if (st == 0xC0000004) { size = Math.Max(size * 2, 0); continue; }
                if (st != 0) return found;
                break;
            }
            fixed (byte* p = table)
            {
                long n = *(long*)p;
                var e = (Entry*)(p + 16);
                ushort type = 0; bool have = false;
                for (long i = 0; i < n; i++)
                    if (e[i].Pid == me && e[i].Handle == mine) { type = e[i].TypeIndex; have = true; break; }
                if (!have) return found;
                nint cur = GetCurrentProcess();
                var name = stackalloc byte[1024];
                for (long i = 0; i < n; i++)
                {
                    int pid = (int)e[i].Pid;
                    if (e[i].TypeIndex != type || pid == me || pid <= 4 || found.Contains(pid)) continue;
                    if (!procs.TryGetValue(pid, out var ph)) procs[pid] = ph = OpenProcess(0x40, false, pid);  // dup handle
                    if (ph == 0) continue;
                    if (!DuplicateHandle(ph, e[i].Handle, cur, out var dup, 0, false, 2)) continue;
                    try
                    {
                        if (NtQueryObject(dup, 1, name, 1024, out _) != 0) continue;
                        var u = (UStr*)name;
                        if (u->Buffer == 0) continue;
                        var s = new string((char*)u->Buffer, 0, u->Length / 2);
                        if (s.EndsWith("\\" + Name, StringComparison.Ordinal)) found.Add(pid);
                    }
                    finally { CloseHandle(dup); }
                }
            }
            return found;
        }
        finally
        {
            foreach (var h in procs.Values) if (h != 0) CloseHandle(h);
            CloseHandle(mine);
        }
    }
}
