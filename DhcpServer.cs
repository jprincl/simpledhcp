using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDhcpServer
{
    public class DhcpServerConfig
    {
        public IPAddress ServerIp = IPAddress.Any;
        public IPAddress PoolStart = IPAddress.Any;
        public IPAddress PoolEnd = IPAddress.Any;
        public IPAddress SubnetMask = IPAddress.Parse("255.255.255.0");
        public IPAddress Gateway = IPAddress.Any;
        public IPAddress[] DnsServers = Array.Empty<IPAddress>();
        public TimeSpan LeaseTime = TimeSpan.FromHours(12);
    }

    /// <summary>
    /// Simple single-subnet DHCP server. Does not implement relay-agent
    /// (giaddr) forwarding — it always replies by broadcast on the local
    /// segment, which is what a small LAN / test network needs.
    /// </summary>
    public class DhcpServer
    {
        private UdpClient? _udp;
        private CancellationTokenSource? _cts;

        public LeaseManager Leases { get; } = new();
        public DhcpServerConfig Config { get; private set; } = new();
        public bool IsRunning { get; private set; }
        public event Action<string>? Log;

        public void Start(DhcpServerConfig config)
        {
            if (IsRunning) return;

            Config = config;
            Leases.LeaseTime = config.LeaseTime;
            Leases.ConfigurePool(config.PoolStart, config.PoolEnd, new[] { config.ServerIp, config.Gateway });

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 67));

            _cts = new CancellationTokenSource();
            IsRunning = true;
            Log?.Invoke("Server spuštěn, naslouchám na UDP/67…");
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            _udp?.Close();
            _udp = null;
            IsRunning = false;
            Log?.Invoke("Server zastaven.");
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp!.ReceiveAsync(token);
                }
                catch
                {
                    break; // socket closed / cancelled
                }

                try
                {
                    HandlePacket(result.Buffer);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Chyba při zpracování paketu: {ex.Message}");
                }
            }
        }

        private void HandlePacket(byte[] data)
        {
            if (data.Length < 240) return;
            var req = DhcpPacket.Parse(data, data.Length);
            if (req.Op != 1) return; // only BOOTREQUEST

            switch (req.MessageType)
            {
                case DhcpMessageType.Discover:
                    HandleDiscover(req);
                    break;
                case DhcpMessageType.Request:
                    HandleRequest(req);
                    break;
                case DhcpMessageType.Release:
                    Leases.ReleaseByMac(req.ClientMac);
                    Log?.Invoke($"RELEASE od {req.MacString}");
                    break;
                case DhcpMessageType.Decline:
                    Log?.Invoke($"DECLINE od {req.MacString}");
                    break;
            }
        }

        private void HandleDiscover(DhcpPacket req)
        {
            var offer = Leases.OfferAddress(req.ClientMac, req.RequestedIp);
            if (offer == null)
            {
                Log?.Invoke($"DISCOVER od {req.MacString} – v poolu není volná adresa!");
                return;
            }
            Log?.Invoke($"DISCOVER od {req.MacString} → nabízím {offer}");
            Send(BuildReply(req, offer, DhcpMessageType.Offer));
        }

        private void HandleRequest(DhcpPacket req)
        {
            var wanted = req.RequestedIp ?? req.ClientIp;
            if (wanted.Equals(IPAddress.Any))
            {
                Log?.Invoke($"REQUEST od {req.MacString} bez uvedené adresy – ignoruji");
                return;
            }

            if (Leases.CommitLease(req.ClientMac, wanted, req.RequestedHostName ?? ""))
            {
                Log?.Invoke($"REQUEST od {req.MacString} → ACK {wanted}");
                Send(BuildReply(req, wanted, DhcpMessageType.Ack));
            }
            else
            {
                Log?.Invoke($"REQUEST od {req.MacString} → NAK ({wanted} není dostupná)");
                Send(BuildNak(req));
            }
        }

        private DhcpPacket BuildReply(DhcpPacket req, IPAddress yourIp, DhcpMessageType type)
        {
            var reply = new DhcpPacket
            {
                Op = 2,
                Xid = req.Xid,
                Flags = req.Flags,
                YourIp = yourIp,
                ServerIp = Config.ServerIp,
                GatewayIp = req.GatewayIp,
                ClientMac = req.ClientMac
            };
            reply.Options[53] = new[] { (byte)type };
            reply.Options[54] = Config.ServerIp.GetAddressBytes();
            reply.Options[51] = ToBigEndian((uint)Config.LeaseTime.TotalSeconds);
            reply.Options[1] = Config.SubnetMask.GetAddressBytes();

            if (!Config.Gateway.Equals(IPAddress.Any))
                reply.Options[3] = Config.Gateway.GetAddressBytes();

            if (Config.DnsServers.Length > 0)
            {
                var dnsBytes = new byte[Config.DnsServers.Length * 4];
                for (int i = 0; i < Config.DnsServers.Length; i++)
                    Config.DnsServers[i].GetAddressBytes().CopyTo(dnsBytes, i * 4);
                reply.Options[6] = dnsBytes;
            }
            return reply;
        }

        private DhcpPacket BuildNak(DhcpPacket req)
        {
            var reply = new DhcpPacket
            {
                Op = 2,
                Xid = req.Xid,
                Flags = req.Flags,
                ServerIp = Config.ServerIp,
                ClientMac = req.ClientMac
            };
            reply.Options[53] = new[] { (byte)DhcpMessageType.Nak };
            reply.Options[54] = Config.ServerIp.GetAddressBytes();
            return reply;
        }

        private void Send(DhcpPacket reply)
        {
            var bytes = reply.ToBytes();
            // Send to the subnet-directed broadcast (e.g. 192.168.137.255) rather
            // than the global 255.255.255.255: this makes Windows route the
            // packet out via the interface actually connected to that subnet
            // (the direct cable to the switch) even when it isn't the default route.
            var broadcast = GetDirectedBroadcast(Config.ServerIp, Config.SubnetMask);
            _udp?.Send(bytes, bytes.Length, new IPEndPoint(broadcast, 68));
        }

        private static IPAddress GetDirectedBroadcast(IPAddress ip, IPAddress mask)
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
            return new IPAddress(result);
        }

        private static byte[] ToBigEndian(uint v) => new[]
        {
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v
        };
    }
}
