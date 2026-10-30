using System.Runtime.InteropServices;
namespace HttpFreeKit.Core;

internal static class WinDivertNative
{
    internal const int NetworkLayer = 0;
    internal const int SocketLayer = 3;
    internal const ulong SniffFlag = 1;
    internal const ulong ReceiveOnlyFlag = 4;
    internal const int SocketEventBind = 3;
    internal const int SocketEventConnect = 4;
    internal const int SocketEventClose = 7;
    private const string DllResourceName = "HttpFreeKit.WinDivert.dll";
    private const string DriverResourceName = "HttpFreeKit.WinDivert64.sys";

    internal const uint OutboundFlag = 1u << 17;
    internal const uint Ipv6Flag = 1u << 20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NetworkData
    {
        internal uint InterfaceIndex;
        internal uint SubInterfaceIndex;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct SocketData
    {
        [FieldOffset(0)] internal ulong EndpointId;
        [FieldOffset(8)] internal ulong ParentEndpointId;
        [FieldOffset(16)] internal uint ProcessId;
        [FieldOffset(52)] internal ushort LocalPort;
        [FieldOffset(54)] internal ushort RemotePort;
        [FieldOffset(56)] internal byte Protocol;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct Address
    {
        [FieldOffset(0)] internal long Timestamp;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal uint Reserved;
        [FieldOffset(16)] internal NetworkData Network;
        [FieldOffset(16)] internal SocketData Socket;

        internal bool IsOutbound
        {
            readonly get => (Flags & OutboundFlag) != 0;
            set => Flags = value ? Flags | OutboundFlag : Flags & ~OutboundFlag;
        }

        internal bool IsIpv6 => (Flags & Ipv6Flag) != 0;
        internal int Layer => (int)(Flags & 0xFF);
        internal int Event => (int)((Flags >> 8) & 0xFF);
    }

    internal static void ExtractRuntimeFiles()
    {
        string directory = AppContext.BaseDirectory;
        ExtractResource(DllResourceName, Path.Combine(directory, "WinDivert.dll"));
        ExtractResource(DriverResourceName, Path.Combine(directory, "WinDivert64.sys"));
    }

    private static void ExtractResource(string resourceName, string destinationPath)
    {
        using Stream resource = typeof(WinDivertNative).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded WinDivert resource '{resourceName}' was not found.");
        using var contents = new MemoryStream();
        resource.CopyTo(contents);
        byte[] bytes = contents.ToArray();

        if (File.Exists(destinationPath) && File.ReadAllBytes(destinationPath).AsSpan().SequenceEqual(bytes))
            return;

        string temporaryPath = destinationPath + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertOpen", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern IntPtr Open(string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecv", CallingConvention = CallingConvention.StdCall,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Recv(IntPtr handle, [Out] byte[] packet, uint packetLength,
        out uint receivedLength, out Address address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertSend", CallingConvention = CallingConvention.StdCall,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Send(IntPtr handle, [In] byte[] packet, uint packetLength,
        out uint sentLength, ref Address address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertClose", CallingConvention = CallingConvention.StdCall,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Close(IntPtr handle);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCalcChecksums",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CalcChecksums([In, Out] byte[] packet, uint packetLength,
        ref Address address, ulong flags);
}
