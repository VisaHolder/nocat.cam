using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Sends pictures to the "nocat.cam" virtual camera. It uses OBS's virtual camera driver (renamed),
/// which reads frames from a shared-memory queue called OBSVirtualCamVideo:
///
///   header (96 bytes): u32 write_idx, u32 read_idx, u32 state, u32 offsets[3], u32 type,
///                      u32 cx, u32 cy, (pad), u64 interval (100 ns units), reserved
///   3 frame slots at offsets[i]: u64 timestamp + 24 pad, then NV12 (Y plane, then interleaved UV)
///   write: fill slot (write_idx+1) % 3, then bump write_idx; state 2 = ready, 3 = stopping.
/// </summary>
public sealed unsafe class VirtualCam : IDisposable
{
    const string Name = "OBSVirtualCamVideo";
    const int HeaderSize = 96, FrameHeader = 32;
    const uint Starting = 1, Ready = 2, Stopping = 3;

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    readonly int[] _offsets = new int[3];
    public int Width { get; }
    public int Height { get; }
    public int FrameSize => Width * Height * 3 / 2;

    VirtualCam(int w, int h, int fps)
    {
        Width = w; Height = h;
        int size = Align(HeaderSize);
        for (int i = 0; i < 3; i++) { _offsets[i] = size; size = Align(size + FrameSize + FrameHeader); }

        _mmf = MemoryMappedFile.CreateNew(Name, size, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor();
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
        new Span<byte>(_base, HeaderSize).Clear();
        U32(8) = Starting;
        for (int i = 0; i < 3; i++) U32(12 + i * 4) = (uint)_offsets[i];
        U32(24) = 0;                     // type: video
        U32(28) = (uint)w;
        U32(32) = (uint)h;
        *(ulong*)(_base + 40) = (ulong)(10_000_000 / fps);
    }

    static int Align(int v) => (v + 31) & ~31;
    ref uint U32(int at) => ref *(uint*)(_base + at);

    /// <summary>Create the virtual camera, or null if something else (OBS itself) already has it.</summary>
    public static VirtualCam? TryCreate(int w, int h, int fps = 30)
    {
        try
        {
            using (MemoryMappedFile.OpenExisting(Name)) { }
            return null;                // already exists: OBS's own virtual camera is running
        }
        catch (FileNotFoundException) { }
        catch { return null; }
        try { return new VirtualCam(w, h, fps); } catch { return null; }
    }

    /// <summary>Copy one NV12 frame (Width x Height, Y then UV) into the next slot.</summary>
    public void Send(ReadOnlySpan<byte> nv12)
    {
        uint next = U32(0) + 1;
        byte* slot = _base + _offsets[next % 3];
        *(long*)slot = DateTime.UtcNow.Ticks;
        nv12[..FrameSize].CopyTo(new Span<byte>(slot + FrameHeader, FrameSize));
        Thread.MemoryBarrier();
        U32(0) = next;
        U32(8) = Ready;
    }

    public void Dispose()
    {
        try { U32(8) = Stopping; } catch { }
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
