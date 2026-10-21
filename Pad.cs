using System.Runtime.InteropServices;

namespace Lumen;

// Minimal XInput wrapper: Xbox controllers and anything XInput-compatible (Steam Input, DS4Windows, etc.).
public static class Pad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint Get14(uint i, out XState s);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern uint Get91(uint i, out XState s);

    public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LB = 0x100, RB = 0x200,
                        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public const ushort Select = Back;   // the SELECT / View button (opens the game grid)

    const uint NotConnected = 1167;

    static int idx = -1;
    static long nextScan;
    static bool use91;

    static uint Get(uint i, out XState s)
    {
        try { return use91 ? Get91(i, out s) : Get14(i, out s); }
        catch (DllNotFoundException)
        {
            if (!use91) { use91 = true; return Get(i, out s); }
            s = default;
            return NotConnected;
        }
        catch { s = default; return NotConnected; }
    }

    // Returns false when no controller is connected. Safe to call every frame: while nothing is plugged in
    // it only looks for a controller about once a second (probing an empty slot is slow).
    public static bool Poll(out ushort buttons, out short lx, out short ly)
    {
        buttons = 0; lx = 0; ly = 0;
        if (idx < 0)
        {
            long now = Environment.TickCount64;
            if (now < nextScan) return false;
            nextScan = now + 1000;
            for (uint i = 0; i < 4; i++)
                if (Get(i, out _) == 0) { idx = (int)i; break; }
            if (idx < 0) return false;
        }
        if (Get((uint)idx, out var s) != 0) { idx = -1; return false; }
        buttons = s.Buttons; lx = s.LX; ly = s.LY;
        return true;
    }
}
