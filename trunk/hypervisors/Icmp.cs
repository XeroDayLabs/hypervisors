using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

/// <summary>
/// Taken from www.pinvoke.net/default.aspx/icmp.icmpsendecho
/// </summary>
public class Icmp
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ICMP_OPTIONS
    {
        public byte Ttl;
        public byte Tos;
        public byte Flags;
        public byte OptionsSize;
        public IntPtr OptionsData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ICMP_ECHO_REPLY
    {
        public int Address;
        public int Status;
        public int RoundTripTime;
        public short DataSize;
        public short Reserved;
        public IntPtr DataPtr;
        public ICMP_OPTIONS Options;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 250)]
        public string Data;
    }

    [DllImport("icmp.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();
    [DllImport("icmp.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("icmp.dll", SetLastError = true)]
    private static extern int IcmpSendEcho(IntPtr icmpHandle, int destinationAddress, string requestData, short requestSize, ref ICMP_OPTIONS requestOptions, ref ICMP_ECHO_REPLY replyBuffer, int replySize, int timeout);

    public static bool Ping(IPAddress ip)
    {
        ICMP_OPTIONS icmpOptions = new ICMP_OPTIONS();
        icmpOptions.Ttl = 255;
        ICMP_ECHO_REPLY icmpReply = new ICMP_ECHO_REPLY();
        string sData = "x";

        IntPtr icmpHandle = IcmpCreateFile();
        try
        {
            int iReplies = IcmpSendEcho(icmpHandle, 
                BitConverter.ToInt32(ip.GetAddressBytes(), 0), sData,
                (short) sData.Length, ref icmpOptions, ref icmpReply, Marshal.SizeOf(icmpReply), 30);

            if (iReplies == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            IcmpCloseHandle(icmpHandle);
        }

        if (icmpReply.Status == 0)
            return true;

        return false;
    }
}