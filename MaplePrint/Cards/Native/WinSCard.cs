using System.Runtime.InteropServices;

namespace MaplePrint.Cards.Native;

/// <summary>
/// The Windows smart-card API (<c>winscard.dll</c>), which is how every PC/SC reader is reached —
/// the ACR122U included, through the CCID driver Windows installs for it by itself.
///
/// <para>Called directly rather than through a package: the handful of calls a contactless reader
/// needs are stable since Windows XP, and this is the exact set the reader was first proven with on
/// a counter PC. Linux and macOS ship the same API as pcsc-lite under the same names; supporting
/// them later means a second binding here (their handle and length types are 64-bit), nothing
/// above this file.</para>
/// </summary>
internal static class WinSCard
{
    public const int ScopeSystem = 2;
    public const int ShareShared = 2;
    public const int ProtocolT0 = 1;
    public const int ProtocolT1 = 2;
    public const int LeaveCard = 0;

    // Reader states, as GetStatusChange reports them.
    public const int StateUnaware = 0x0000;
    public const int StateChanged = 0x0002;
    public const int StateUnavailable = 0x0008;
    public const int StatePresent = 0x0020;

    public const int Success = 0;
    public static readonly int Cancelled = unchecked((int)0x80100002);
    public static readonly int Timeout = unchecked((int)0x8010000A);
    public static readonly int UnknownReader = unchecked((int)0x80100009);
    public static readonly int NoSmartcard = unchecked((int)0x8010000C);
    public static readonly int ReaderUnavailable = unchecked((int)0x80100017);
    public static readonly int NoService = unchecked((int)0x8010001D);
    public static readonly int ServiceStopped = unchecked((int)0x8010001E);
    public static readonly int NoReadersAvailable = unchecked((int)0x8010002E);
    public static readonly int UnresponsiveCard = unchecked((int)0x80100066);
    public static readonly int RemovedCard = unchecked((int)0x80100069);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ReaderState
    {
        public string Reader;
        public IntPtr UserData;
        public int CurrentState;
        public int EventState;
        public int AtrLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] Atr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoRequest
    {
        public int Protocol;
        public int PciLength;
    }

    [DllImport("winscard.dll")]
    public static extern int SCardEstablishContext(int scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);

    [DllImport("winscard.dll")]
    public static extern int SCardReleaseContext(IntPtr context);

    [DllImport("winscard.dll", EntryPoint = "SCardListReadersW", CharSet = CharSet.Unicode)]
    public static extern int SCardListReaders(IntPtr context, string? groups, char[]? readers, ref int length);

    [DllImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW", CharSet = CharSet.Unicode)]
    public static extern int SCardGetStatusChange(IntPtr context, int timeoutMs, [In, Out] ReaderState[] states, int count);

    [DllImport("winscard.dll", EntryPoint = "SCardConnectW", CharSet = CharSet.Unicode)]
    public static extern int SCardConnect(IntPtr context, string reader, int shareMode, int preferredProtocols, out IntPtr card, out int activeProtocol);

    [DllImport("winscard.dll")]
    public static extern int SCardDisconnect(IntPtr card, int disposition);

    [DllImport("winscard.dll")]
    public static extern int SCardTransmit(IntPtr card, ref IoRequest sendPci, byte[] send, int sendLength, IntPtr receivePci, byte[] receive, ref int receiveLength);
}
