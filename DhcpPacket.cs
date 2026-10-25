using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace SimpleDhcpServer
{
    // DHCP message types (RFC 2131, option 53)
    public enum DhcpMessageType : byte
    {
        Discover = 1,
        Offer = 2,
        Request = 3,
        Decline = 4,
        Ack = 5,
        Nak = 6,
        Release = 7,
        Inform = 8
    }

    /// <summary>
    /// Minimal parser/builder for BOOTP/DHCP packets. Supports only what a
    /// simple single-subnet DHCP server needs (no relay agent / giaddr handling).
    /// </summary>
    public class DhcpPacket
    {
        public byte Op;               // 1 = BOOTREQUEST, 2 = BOOTREPLY
        public byte HType = 1;        // 1 = Ethernet
        public byte HLen = 6;
        public byte Hops;
        public uint Xid;
        public ushort Secs;
        public ushort Flags;
        public IPAddress ClientIp = IPAddress.Any;   // ciaddr
        public IPAddress YourIp = IPAddress.Any;     // yiaddr
        public IPAddress ServerIp = IPAddress.Any;   // siaddr
        public IPAddress GatewayIp = IPAddress.Any;  // giaddr
        public byte[] ClientMac = new byte[6];
        public Dictionary<byte, byte[]> Options { get; } = new Dictionary<byte, byte[]>();

        public bool IsBroadcast => (Flags & 0x8000) != 0;

        public DhcpMessageType? MessageType
        {
            get
            {
                if (Options.TryGetValue(53, out var v) && v.Length == 1)
                    return (DhcpMessageType)v[0];
                return null;
            }
        }

        public string? RequestedHostName =>
            Options.TryGetValue(12, out var v) ? Encoding.ASCII.GetString(v) : null;

        public IPAddress? RequestedIp =>
            Options.TryGetValue(50, out var v) && v.Length == 4 ? new IPAddress(v) : null;

        public string MacString => string.Join(":", ClientMac.Select(b => b.ToString("X2")));

        public static DhcpPacket Parse(byte[] data, int length)
        {
            var p = new DhcpPacket
            {
                Op = data[0],
                HType = data[1],
                HLen = data[2],
                Hops = data[3],
                Xid = ReadUInt32(data, 4),
                Secs = ReadUInt16(data, 8),
                Flags = ReadUInt16(data, 10),
                ClientIp = new IPAddress(Slice(data, 12, 4)),
                YourIp = new IPAddress(Slice(data, 16, 4)),
                ServerIp = new IPAddress(Slice(data, 20, 4)),
                GatewayIp = new IPAddress(Slice(data, 24, 4)),
                ClientMac = Slice(data, 28, 6)
            };

            // Options start after the fixed 236-byte header + 4-byte magic cookie
            int idx = 240;
            while (idx < length)
            {
                byte code = data[idx];
                if (code == 255) break;       // End option
                if (code == 0) { idx++; continue; } // Pad
                if (idx + 1 >= length) break;
                byte len = data[idx + 1];
                if (idx + 2 + len > length) break;
                p.Options[code] = Slice(data, idx + 2, len);
                idx += 2 + len;
            }
            return p;
        }

        public byte[] ToBytes()
        {
            // 240-byte fixed part (header + magic cookie), then options, then the End marker;
            // BOOTP requires at least 300 bytes in total.
            int optionsLength = 0;
            foreach (var kv in Options)
                optionsLength += 2 + kv.Value.Length;
            var buf = new byte[Math.Max(300, 240 + optionsLength + 1)];
            buf[0] = Op;
            buf[1] = HType;
            buf[2] = HLen;
            buf[3] = Hops;
            WriteUInt32(buf, 4, Xid);
            WriteUInt16(buf, 8, Secs);
            WriteUInt16(buf, 10, Flags);
            ClientIp.GetAddressBytes().CopyTo(buf, 12);
            YourIp.GetAddressBytes().CopyTo(buf, 16);
            ServerIp.GetAddressBytes().CopyTo(buf, 20);
            GatewayIp.GetAddressBytes().CopyTo(buf, 24);
            ClientMac.CopyTo(buf, 28);

            // Magic cookie
            buf[236] = 99; buf[237] = 130; buf[238] = 83; buf[239] = 99;

            int idx = 240;
            foreach (var kv in Options)
            {
                buf[idx++] = kv.Key;
                buf[idx++] = (byte)kv.Value.Length;
                kv.Value.CopyTo(buf, idx);
                idx += kv.Value.Length;
            }
            buf[idx++] = 255; // End option

            return buf;
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }

        private static uint ReadUInt32(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        private static ushort ReadUInt16(byte[] d, int o) =>
            (ushort)((d[o] << 8) | d[o + 1]);

        private static void WriteUInt32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        private static void WriteUInt16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)(v >> 8); d[o + 1] = (byte)v;
        }
    }
}
