using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace SimpleDhcpServer
{
    public class Lease
    {
        public IPAddress Ip = IPAddress.Any;
        public byte[] Mac = Array.Empty<byte>();
        public string HostName = "";
        public DateTime ExpiresAt;

        public string MacString => string.Join(":", Mac.Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Tracks the address pool and confirmed leases. Thread-safe (the DHCP
    /// server handles each packet on a worker thread).
    /// </summary>
    public class LeaseManager
    {
        private readonly List<uint> _pool = new();
        private readonly Dictionary<uint, Lease> _leases = new();
        private readonly object _lock = new();

        public TimeSpan LeaseTime { get; set; } = TimeSpan.FromHours(12);

        public void ConfigurePool(IPAddress start, IPAddress end, IEnumerable<IPAddress> excluded)
        {
            lock (_lock)
            {
                _pool.Clear();
                uint s = ToUint(start), e = ToUint(end);
                var excl = excluded.Select(ToUint).ToHashSet();
                for (uint i = s; i <= e; i++)
                {
                    if (!excl.Contains(i)) _pool.Add(i);
                }
            }
        }

        public IReadOnlyList<Lease> AllLeases
        {
            get { lock (_lock) { return _leases.Values.ToList(); } }
        }

        /// <summary>Picks an address to offer: existing lease for this MAC, the
        /// client's requested address if free, or the next free address.</summary>
        public IPAddress? OfferAddress(byte[] mac, IPAddress? requested)
        {
            lock (_lock)
            {
                CleanExpiredInternal();

                var existing = _leases.Values.FirstOrDefault(l => l.Mac.SequenceEqual(mac));
                if (existing != null) return existing.Ip;

                if (requested != null)
                {
                    uint reqUint = ToUint(requested);
                    if (_pool.Contains(reqUint) && !_leases.ContainsKey(reqUint))
                        return requested;
                }

                foreach (var ipUint in _pool)
                {
                    if (!_leases.ContainsKey(ipUint))
                        return FromUint(ipUint);
                }
                return null; // pool exhausted
            }
        }

        public bool CommitLease(byte[] mac, IPAddress ip, string hostName)
        {
            lock (_lock)
            {
                uint ipUint = ToUint(ip);
                if (!_pool.Contains(ipUint)) return false;

                if (_leases.TryGetValue(ipUint, out var existing) && !existing.Mac.SequenceEqual(mac))
                    return false; // already leased to a different client

                _leases[ipUint] = new Lease
                {
                    Ip = ip,
                    Mac = mac,
                    HostName = hostName,
                    ExpiresAt = DateTime.UtcNow + LeaseTime
                };
                return true;
            }
        }

        public void ReleaseByMac(byte[] mac)
        {
            lock (_lock)
            {
                var toRemove = _leases.Where(kv => kv.Value.Mac.SequenceEqual(mac))
                                       .Select(kv => kv.Key).ToList();
                foreach (var k in toRemove) _leases.Remove(k);
            }
        }

        public void CleanExpired()
        {
            lock (_lock) CleanExpiredInternal();
        }

        private void CleanExpiredInternal()
        {
            var expired = _leases.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow)
                                  .Select(kv => kv.Key).ToList();
            foreach (var k in expired) _leases.Remove(k);
        }

        private static uint ToUint(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        private static IPAddress FromUint(uint v) => new(new[]
        {
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v
        });
    }
}
