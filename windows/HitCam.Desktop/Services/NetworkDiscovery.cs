using System.Diagnostics;
using System.Runtime.InteropServices;
using HitCam.Core.Protocol;

namespace HitCam.Desktop.Services;

/// <summary>
/// Announces this PC on the local network as <c>_hitcam._tcp</c> (mDNS / DNS-SD, "Bonjour"), so phones find it without
/// typing an address. Uses Windows' own mDNS responder (dnsapi, Windows 10 1809+), which the firewall already allows.
/// TXT: <c>id</c> (server id), <c>name</c>, <c>v</c> (protocol version), <c>port</c>, <c>addr</c> (the PC's IPv4
/// addresses, comma-separated: an iPhone browsing gets no address otherwise without connecting). The id is only a claim: anyone on the network
/// can announce any id, so phones still pair by PIN (or QR) before they trust it.
/// </summary>
public sealed class NetworkDiscovery : IDisposable
{
    private const uint DnsRequestPending = 9506;

    private IntPtr _instance;
    private DnsServiceRegisterRequest _request;
    private bool _registered;

    public string InstanceName { get; }

    public NetworkDiscovery(string serverName, string serverId, int port, IReadOnlyList<System.Net.IPAddress>? addresses = null)
    {
        InstanceName = $"{Sanitize(serverName)}.{ProtocolInfo.BonjourServiceType}.local";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;
        try
        {
            _instance = DnsServiceConstructInstance(InstanceName, $"{Sanitize(System.Net.Dns.GetHostName())}.local", IntPtr.Zero, IntPtr.Zero,
                (ushort)port, 0, 0, 5, ["id", "name", "v", "port", "addr"],
                [serverId, serverName, ProtocolInfo.Version.ToString(), port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 string.Join(",", (addresses ?? []).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))]);
            if (_instance == IntPtr.Zero)
                return;
            _request = new DnsServiceRegisterRequest
            {
                Version = 1,
                InterfaceIndex = 0,
                ServiceInstance = _instance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(Completed),
            };
            var status = DnsServiceRegister(ref _request, IntPtr.Zero);
            _registered = status == DnsRequestPending;
            if (!_registered)
                Trace.TraceWarning($"HitCam: network announcement failed ({status})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning($"HitCam: no network announcement on this Windows ({ex.Message})");
        }
    }

    /// <summary>Whether the announcement is on (false before Windows 10 1809 or if it failed).</summary>
    public bool IsRegistered => _registered;

    /// <summary>DNS labels take no dots; other characters are fine in DNS-SD instance names.</summary>
    private static string Sanitize(string name) => string.IsNullOrWhiteSpace(name) ? "HitCam" : name.Replace('.', '-').Trim();

    public void Dispose()
    {
        if (_registered)
        {
            // Deregistering finishes asynchronously and may still read the instance: it is left to the process (a few
            // hundred bytes, once).
            DnsServiceDeRegister(ref _request, IntPtr.Zero);
            _registered = false;
            _instance = IntPtr.Zero;
        }
        else if (_instance != IntPtr.Zero)
        {
            DnsServiceFreeInstance(_instance);
            _instance = IntPtr.Zero;
        }
    }

    // The completion callbacks (register and deregister) hand over a copy of the instance, which is ours to free. Static:
    // it must outlive every request.
    private static readonly RegisterComplete Completed = (_, _, instance) =>
    {
        if (instance != IntPtr.Zero)
            DnsServiceFreeInstance(instance);
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port,
        ushort priority, ushort weight, uint propertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceDeRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsServiceFreeInstance(IntPtr instance);
}
