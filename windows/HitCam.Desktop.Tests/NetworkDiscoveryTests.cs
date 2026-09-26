using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

/// <summary>
/// The announcement as a phone would see it: a DNS-SD browse for _hitcam._tcp through Windows' mDNS, which answers
/// for its own registrations too. Skipped before Windows 10 1809.
/// </summary>
public sealed class NetworkDiscoveryTests
{
    [Fact]
    public void The_pc_is_announced_and_found_by_a_browse()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            Assert.Skip("DNS-SD needs Windows 10 1809");
        var name = $"HitCamTest-{Guid.NewGuid():N}"[..24];
        using var discovery = new NetworkDiscovery(name, "server-id", 47898);
        Assert.True(discovery.IsRegistered);
        Assert.Equal($"{name}._hitcam._tcp.local", discovery.InstanceName);

        var found = Browse("_hitcam._tcp.local", TimeSpan.FromSeconds(8), n => n.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.True(found, "the announcement was not found by a browse");
    }

    [Fact]
    public void Dots_in_the_pc_name_do_not_break_the_service_name()
    {
        using var discovery = new NetworkDiscovery("My.PC", "id", 47898);
        Assert.StartsWith("My-PC._hitcam._tcp", discovery.InstanceName);
    }

    // DnsServiceBrowse until a record name or PTR target matches, or the time is up.
    private static bool Browse(string query, TimeSpan timeout, Func<string, bool> match)
    {
        var names = new BlockingCollection<string>();
        Callback callback = (_, _, records) =>
        {
            for (var record = records; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, IntPtr.Size)) is { } owner)
                    names.Add(owner);
                // PTR data (type 12): the instance name, right after the fixed header.
                if ((ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size) == 12
                    && Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, 2 * IntPtr.Size + 16)) is { } target)
                    names.Add(target);
            }
            if (records != IntPtr.Zero)
                DnsRecordListFree(records, 1);
        };
        var request = new BrowseRequest { Version = 1, QueryName = query, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var cancel = new Cancel();
        Assert.Equal(9506u, DnsServiceBrowse(ref request, ref cancel));
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (names.TryTake(out var name, TimeSpan.FromMilliseconds(200)) && match(name))
                    return true;
            }
            return false;
        }
        finally
        {
            DnsServiceBrowseCancel(ref cancel);
            GC.KeepAlive(callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Callback(uint status, IntPtr context, IntPtr records);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public string QueryName;
        public IntPtr Callback;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Cancel
    {
        public IntPtr Reserved;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceBrowse(ref BrowseRequest request, ref Cancel cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceBrowseCancel(ref Cancel cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
