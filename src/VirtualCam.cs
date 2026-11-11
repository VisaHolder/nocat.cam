using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace NocatCam;

/// <summary>
/// Sends pictures to the "nocat.cam" virtual camera. It uses OBS's virtual camera driver (renamed),
/// which reads frames from a shared-memory queue called OBSVirtualCamVideo:
///
///   header (96 bytes): u32 write_idx, u32 read_idx, u32 state, u32 offsets[3], u32 type,
///                      u32 cx, u32 cy, (pad), u64 interval (100 ns units), reserved
///   3 frame slots at offsets[i]: u64 timestamp + 24 pad, then NV12 (Y plane, then interleaved UV)
///   write: fill slot (write_idx+1) % 3, then bump write_idx; state 2 = ready, 3 = stopping.
///
/// The shared memory is always made big enough for 4K, so changing size or frame rate never has to
/// recreate it: apps already showing nocat.cam (Discord...) just pick up the new size and carry on.
/// </summary>
public sealed unsafe class VirtualCam : IDisposable
{
    const string Name = "OBSVirtualCamVideo";
    const int HeaderSize = 96, FrameHeader = 32;
    const uint Starting = 1, Ready = 2, Stopping = 3;
    const int MaxW = 3840, MaxH = 2160;

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    readonly long _capacity;
    readonly int[] _offsets = new int[3];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public int FrameSize => Width * Height * 3 / 2;

    static int Align(int v) => (v + 31) & ~31;
    static long Needed(int w, int h) => Align(HeaderSize) + 3L * Align(w * h * 3 / 2 + FrameHeader);
    ref uint U32(int at) => ref *(uint*)(_base + at);

    VirtualCam(MemoryMappedFile mmf)
    {
        _mmf = mmf;
        _view = _mmf.CreateViewAccessor();
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
        _capacity = (long)_view.SafeMemoryMappedViewHandle.ByteLength;
    }

    /// <summary>
    /// Start the virtual camera. Reuses the shared memory if apps still hold it from a moment ago.
    /// Null if OBS's own virtual camera is running (it owns the same memory then).
    /// </summary>
    public static VirtualCam? TryCreate(int w, int h, int fps, out string? why)
    {
        why = null;
        MemoryMappedFile? mmf = null;
        try
        {
            try { mmf = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.ReadWrite); }
            catch (FileNotFoundException) { }

            if (mmf != null && Process.GetProcessesByName("obs64").Length > 0)
            {
                mmf.Dispose();
                why = "OBS's own virtual camera is on - stop it in OBS";
                return null;
            }
            mmf ??= MemoryMappedFile.CreateNew(Name, Needed(MaxW, MaxH), MemoryMappedFileAccess.ReadWrite);
            var v = new VirtualCam(mmf);
            if (!v.Configure(w, h, fps)) { v.Dispose(); why = "virtual camera memory too small - restart the app showing nocat.cam"; return null; }
            return v;
        }
        catch (Exception e)
        {
            mmf?.Dispose();
            why = "couldn't start the virtual camera: " + e.Message;
            return null;
        }
    }

    /// <summary>Change size / frame rate in place. Readers see "stopping", then the new size.</summary>
    public bool Configure(int w, int h, int fps)
    {
        if (Needed(w, h) > _capacity) return false;
        bool resize = w != Width || h != Height;
        if (resize && Width != 0)
        {
            U32(8) = Stopping;                    // let readers close and reopen with the new size
            Thread.Sleep(300);
        }
        Width = w; Height = h; Fps = fps;
        int at = Align(HeaderSize);
        for (int i = 0; i < 3; i++) { _offsets[i] = at; at = Align(at + FrameSize + FrameHeader); }
        if (resize || U32(28) != (uint)w)
        {
            U32(0) = 0; U32(4) = 0;
            U32(8) = Starting;
            for (int i = 0; i < 3; i++) U32(12 + i * 4) = (uint)_offsets[i];
            U32(24) = 0;                          // type: video
            U32(28) = (uint)w;
            U32(32) = (uint)h;
        }
        *(ulong*)(_base + 40) = (ulong)(10_000_000 / fps);
        return true;
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
        U32(4) = next;                            // OBS's own writer keeps read_idx in step too
        U32(8) = Ready;
    }

    public void MarkStopping() { try { U32(8) = Stopping; } catch { } }

    public void Dispose()
    {
        try { U32(8) = Stopping; } catch { }
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
