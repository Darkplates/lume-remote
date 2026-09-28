using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LumeRemote
{
    public static class WakeOnLan
    {
        public static string Normalize(string mac)
        {
            if (mac == null) throw new FormatException("Choose the target Ethernet MAC address.");
            mac = mac.Replace("-", "").Replace(":", "").Trim().ToUpperInvariant();
            if (!Invitation.IsHex(mac, 12) || mac == "000000000000" || mac == "FFFFFFFFFFFF" || (Convert.ToByte(mac.Substring(0, 2), 16) & 1) != 0) throw new FormatException("Enter a unicast Ethernet MAC address, for example 12-34-56-78-9A-BC.");
            return mac;
        }
        public static byte[] Packet(string mac)
        {
            mac = Normalize(mac); byte[] packet = new byte[102], address = new byte[6];
            for (int i = 0; i < 6; i++) { packet[i] = 255; address[i] = Convert.ToByte(mac.Substring(i * 2, 2), 16); }
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(address, 0, packet, 6 + i * 6, 6); return packet;
        }
        public static string EthernetMac()
        {
            try { NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.GetPhysicalAddress().GetAddressBytes().Length == 6).OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up).FirstOrDefault(); return adapter == null ? "" : Normalize(adapter.GetPhysicalAddress().ToString()); }
            catch { return ""; }
        }
        public static int Send(string mac)
        {
            byte[] packet = Packet(mac); int delivered = 0;
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || (network.NetworkInterfaceType != NetworkInterfaceType.Ethernet && network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)) continue;
                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null || IPAddress.IsLoopback(address.Address)) continue;
                    byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes(), broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                    try { using (UdpClient client = new UdpClient(new IPEndPoint(address.Address, 0))) { client.EnableBroadcast = true; for (int repeat = 0; repeat < 3; repeat++) client.Send(packet, packet.Length, new IPEndPoint(new IPAddress(broadcast), 9)); delivered++; } }
                    catch (SocketException) { }
                }
            }
            if (delivered == 0) throw new IOException("No local Ethernet/Wi-Fi network could send the wake packet."); return delivered;
        }
    }
}
