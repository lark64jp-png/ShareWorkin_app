using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ShareWorkin.SMB;

public sealed record LanCandidate(IPAddress Address, string? HostName);

public static class LanScanner
{
    private const int SmbPort = 445;
    // Windows PC は 445 と並んで 135(RPC Endpoint Mapper) も開いていることがほぼ常。
    // NASNE/LinkStation 等の家電系 SMB は 135 を持たない。
    // ShareWorkin の用途は Windows PC 同士なので 445 のみのホストは候補から除外する。
    private const int RpcPort = 135;
    private const int ProbeTimeoutMs = 1000;
    private const int MaxParallel = 64;

    public static IReadOnlyList<IPNetwork2> EnumerateLocalSubnets()
    {
        List<IPNetwork2> nets = new();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props = nic.GetIPProperties();
            foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                int prefix = addr.PrefixLength;
                if (prefix < 16 || prefix > 30) continue;

                nets.Add(new IPNetwork2(addr.Address, prefix));
            }
        }
        return nets;
    }

    public static async Task<IReadOnlyList<LanCandidate>> ScanAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IPNetwork2> subnets = EnumerateLocalSubnets();
        if (subnets.Count == 0)
        {
            return Array.Empty<LanCandidate>();
        }

        List<IPAddress> targets = new();
        foreach (IPNetwork2 subnet in subnets)
        {
            targets.AddRange(subnet.EnumerateUsable());
        }

        SwkLogger.Info($"LanScanner: probing {targets.Count} addresses");

        SemaphoreSlim gate = new(MaxParallel);
        List<Task<ProbeOutcome>> probes = targets
            .Select(ip => ProbeAsync(ip, gate, cancellationToken))
            .ToList();

        ProbeOutcome[] outcomes = await Task.WhenAll(probes).ConfigureAwait(false);

        int smbOnly = outcomes.Count(o => o.SmbOpen && !o.RpcOpen);
        int both = outcomes.Count(o => o.SmbOpen && o.RpcOpen);
        SwkLogger.Debug($"LanScanner: 445 only={smbOnly} (excluded as non-Windows SMB), 445+135={both} (included)");

        return outcomes
            .Where(o => o.Candidate is not null)
            .Select(o => o.Candidate!)
            .ToArray();
    }

    private readonly record struct ProbeOutcome(bool SmbOpen, bool RpcOpen, LanCandidate? Candidate);

    private static async Task<ProbeOutcome> ProbeAsync(IPAddress address, SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool smbOpen = await TryConnectAsync(address, SmbPort, token).ConfigureAwait(false);
            if (!smbOpen)
            {
                return new ProbeOutcome(false, false, null);
            }

            bool rpcOpen = await TryConnectAsync(address, RpcPort, token).ConfigureAwait(false);
            if (!rpcOpen)
            {
                SwkLogger.Debug($"LanScanner: skip {address} (445 open, 135 closed; likely NAS/家電)");
                return new ProbeOutcome(true, false, null);
            }

            string? hostName = null;
            try
            {
                IPHostEntry entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
                hostName = entry.HostName;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                hostName = null;
            }

            return new ProbeOutcome(true, true, new LanCandidate(address, hostName));
        }
        finally
        {
            try { gate.Release(); } catch { }
        }
    }

    private static async Task<bool> TryConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        try
        {
            using TcpClient client = new();
            Task connect = client.ConnectAsync(address, port, token).AsTask();
            Task delay = Task.Delay(ProbeTimeoutMs, token);
            Task done = await Task.WhenAny(connect, delay).ConfigureAwait(false);
            return done == connect && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class IPNetwork2
{
    public IPAddress Address { get; }
    public int PrefixLength { get; }

    public IPNetwork2(IPAddress address, int prefixLength)
    {
        Address = address;
        PrefixLength = prefixLength;
    }

    public IEnumerable<IPAddress> EnumerateUsable()
    {
        if (Address.AddressFamily != AddressFamily.InterNetwork)
        {
            yield break;
        }

        byte[] addressBytes = Address.GetAddressBytes();
        uint addr = ((uint)addressBytes[0] << 24) | ((uint)addressBytes[1] << 16) | ((uint)addressBytes[2] << 8) | addressBytes[3];
        uint mask = PrefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - PrefixLength);
        uint network = addr & mask;
        uint broadcast = network | ~mask;

        long total = (long)broadcast - (long)network - 1;
        if (total <= 0 || total > 4096)
        {
            yield break;
        }

        for (uint v = network + 1; v < broadcast; v++)
        {
            byte[] bytes =
            {
                (byte)((v >> 24) & 0xFF),
                (byte)((v >> 16) & 0xFF),
                (byte)((v >> 8) & 0xFF),
                (byte)(v & 0xFF),
            };
            yield return new IPAddress(bytes);
        }
    }
}
