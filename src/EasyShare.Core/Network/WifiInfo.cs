using System.Runtime.InteropServices;
using EasyShare.Core.Logging;
using Serilog;

namespace EasyShare.Core.Network;

public enum WifiBand { Band24, Band5, Band6, Unknown }

public record WifiBandInfo(
    string InterfaceName,
    int Channel,
    WifiBand Band,
    string PhyDescription,
    uint RxRateMbps,
    uint TxRateMbps,
    uint SignalQuality);

public static class WifiInfo
{
    private static readonly Serilog.ILogger Log = EasyLogger.Log.ForContext("SourceContext", "WifiInfo");

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanOpenHandle(
        uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanQueryInterface(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        WLAN_INTF_OPCODE OpCode, IntPtr pReserved,
        out uint pdwDataSize, ref IntPtr ppData,
        out WLAN_OPCODE_VALUE_TYPE pWlanOpcodeValueType);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    private const uint WLAN_CLIENT_VERSION_V2 = 2;

    private enum WLAN_INTF_OPCODE : uint
    {
        CurrentConnection = 7,
        ChannelNumber = 819,
    }

    private enum WLAN_OPCODE_VALUE_TYPE : uint
    {
        QueryOnly = 0,
        SetByUser = 1,
        SetByGroupPolicy = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
        public WLAN_INTERFACE_INFO[]? InterfaceInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WLAN_INTERFACE_STATE isState;
    }

    private enum WLAN_INTERFACE_STATE
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public WLAN_INTERFACE_STATE isState;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_MAC_ADDRESS dot11Bssid;
        public DOT11_ASSOCIATION_TYPE dot11AssocType;
        public DOT11_PHY_TYPE dot11PhyType;
        public uint uDot11PhyIndex;
        public uint ulRxRate;
        public uint ulTxRate;
        public uint SignalQuality;
    }

    private enum DOT11_ASSOCIATION_TYPE
    {
        Null = 0,
        Reassociation = 1,
        Association = 2,
        IBSS = 3,
    }

    public enum DOT11_PHY_TYPE : uint
    {
        Unknown = 0,
        FHSS = 1,
        DSSS = 2,
        IRBaseband = 3,
        OFDM = 4,
        HRDSSS = 5,
        ERP = 6,
        HT = 7,
        VHT = 8,
        DMG = 9,
        HE = 10,
        EHT = 11,
    }

    [StructLayout(LayoutKind.Sequential, Size = 6)]
    private struct DOT11_MAC_ADDRESS { }

    public static WifiBandInfo? GetCurrentConnection()
    {
        var result = WlanOpenHandle(WLAN_CLIENT_VERSION_V2, IntPtr.Zero,
            out var negotiatedVersion, out var clientHandle);
        if (result != 0 || clientHandle == IntPtr.Zero)
        {
            Log.Debug("WlanOpenHandle fehlgeschlagen: {Result}", result);
            return null;
        }

        try
        {
            result = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out var interfaceListPtr);
            if (result != 0 || interfaceListPtr == IntPtr.Zero)
            {
                Log.Debug("WlanEnumInterfaces fehlgeschlagen: {Result}", result);
                return null;
            }

            try
            {
                var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST_HEADER>(interfaceListPtr);
                for (uint i = 0; i < header.NumberOfItems; i++)
                {
                    var infoPtr = interfaceListPtr + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST_HEADER>()
                        + (int)i * Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(infoPtr);

                    if (info.isState != WLAN_INTERFACE_STATE.Connected) continue;

                    var bandInfo = QueryConnectionInfo(clientHandle, info.InterfaceGuid, info.strInterfaceDescription);
                    if (bandInfo is not null) return bandInfo;
                }
            }
            finally
            {
                WlanFreeMemory(interfaceListPtr);
            }
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }

        return null;
    }

    private static WifiBandInfo? QueryConnectionInfo(IntPtr clientHandle, Guid interfaceGuid, string interfaceName)
    {
        IntPtr dataPtr = IntPtr.Zero;
        try
        {
            var result = WlanQueryInterface(
                clientHandle, ref interfaceGuid,
                WLAN_INTF_OPCODE.CurrentConnection,
                IntPtr.Zero, out var dataSize,
                ref dataPtr, out var _);

            if (result != 0 || dataPtr == IntPtr.Zero || dataSize < Marshal.SizeOf<WLAN_CONNECTION_ATTRIBUTES>())
            {
                Log.Debug("WlanQueryInterface(CurrentConnection) fehlgeschlagen: {Result}", result);
                return null;
            }

            var connAttrs = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
            var assoc = connAttrs.wlanAssociationAttributes;

            int channel = 0;
            IntPtr channelPtr = IntPtr.Zero;
            try
            {
                result = WlanQueryInterface(
                    clientHandle, ref interfaceGuid,
                    WLAN_INTF_OPCODE.ChannelNumber,
                    IntPtr.Zero, out var channelSize,
                    ref channelPtr, out var _);

                if (result == 0 && channelPtr != IntPtr.Zero)
                    channel = Marshal.ReadInt32(channelPtr);
            }
            finally
            {
                if (channelPtr != IntPtr.Zero)
                    WlanFreeMemory(channelPtr);
            }

            var band = DetermineBand(assoc.dot11PhyType, channel);
            var phyDesc = GetPhyDescription(assoc.dot11PhyType);

            Log.Debug("WLAN: {Interface}, PHY={Phy}, Channel={Channel}, Band={Band}, Rx={Rx}Mbps, Tx={Tx}Mbps, Signal={Signal}%",
                interfaceName, phyDesc, channel, band, assoc.ulRxRate / 5000, assoc.ulTxRate / 5000, assoc.SignalQuality);

            return new WifiBandInfo(
                interfaceName,
                channel,
                band,
                phyDesc,
                assoc.ulRxRate / 5000,
                assoc.ulTxRate / 5000,
                assoc.SignalQuality);
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                WlanFreeMemory(dataPtr);
        }
    }

    public static WifiBand DetermineBand(DOT11_PHY_TYPE phyType, int channel)
    {
        return phyType switch
        {
            DOT11_PHY_TYPE.OFDM => WifiBand.Band5,
            DOT11_PHY_TYPE.VHT => WifiBand.Band5,
            DOT11_PHY_TYPE.DMG => WifiBand.Unknown,
            DOT11_PHY_TYPE.DSSS or DOT11_PHY_TYPE.HRDSSS or DOT11_PHY_TYPE.ERP => WifiBand.Band24,
            DOT11_PHY_TYPE.HT or DOT11_PHY_TYPE.HE or DOT11_PHY_TYPE.EHT => channel switch
            {
                > 0 and <= 14 => WifiBand.Band24,
                > 14 => WifiBand.Band5,
                _ => WifiBand.Unknown,
            },
            _ => WifiBand.Unknown,
        };
    }

    public static string GetPhyDescription(DOT11_PHY_TYPE phyType) => phyType switch
    {
        DOT11_PHY_TYPE.FHSS => "802.11 (FHSS)",
        DOT11_PHY_TYPE.DSSS => "802.11b",
        DOT11_PHY_TYPE.IRBaseband => "802.11 (IR)",
        DOT11_PHY_TYPE.OFDM => "802.11a",
        DOT11_PHY_TYPE.HRDSSS => "802.11b",
        DOT11_PHY_TYPE.ERP => "802.11g",
        DOT11_PHY_TYPE.HT => "802.11n",
        DOT11_PHY_TYPE.VHT => "802.11ac",
        DOT11_PHY_TYPE.DMG => "802.11ad",
        DOT11_PHY_TYPE.HE => "802.11ax (Wi-Fi 6)",
        DOT11_PHY_TYPE.EHT => "802.11be (Wi-Fi 7)",
        _ => "Unbekannt",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST_HEADER
    {
        public uint NumberOfItems;
        public uint Offset;
    }
}
