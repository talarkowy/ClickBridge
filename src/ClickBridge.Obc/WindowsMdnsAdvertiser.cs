using ClickBridge.Core.Logging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClickBridge.Obc;

/// <summary>
/// Advertises a DNS-SD service through the mDNS responder built into Windows 10 1809+ (dnsapi
/// <c>DnsServiceRegister</c>), so we do not fight the OS for UDP port 5353.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsMdnsAdvertiser : IDisposable
{
    private const string Tag = "MDNS";
    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9506;
    private const uint ErrorSuccess = 0;

    private readonly IBridgeLog _log;
    private readonly RegisterCompleteCallback _callback;
    private readonly ManualResetEventSlim _completed = new();

    /// <summary>
    /// Advertisers whose native request is still pending when they are disposed. Windows may still invoke
    /// the callback, so the delegate and the native buffers must outlive the object.
    /// </summary>
    private static readonly List<WindowsMdnsAdvertiser> Abandoned = [];

    private IntPtr _instance;
    private IntPtr _request;
    private IntPtr _ip4;
    private uint _lastStatus;
    private bool _registered;
    private bool _callbackPending;

    public WindowsMdnsAdvertiser(IBridgeLog log)
    {
        _log = log;
        _callback = OnRegisterComplete; // kept in a field so the GC does not collect the delegate
    }

    /// <summary>Full service instance name, e.g. "ClickBridge._openbikecontrol._tcp.local".</summary>
    public string? ServiceName { get; private set; }

    public bool IsRegistered => _registered;

    /// <summary>Registers the service and waits (up to 5 s) for Windows to confirm it.</summary>
    public bool Register(
        string instanceName,
        string serviceType,
        ushort port,
        IPAddress ipv4,
        IReadOnlyDictionary<string, string> txt)
    {
        if (_registered)
        {
            throw new InvalidOperationException("Already registered.");
        }

        if (ipv4.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("An IPv4 address is required.", nameof(ipv4));
        }

        ServiceName = $"{instanceName}.{serviceType}.local";
        var hostName = $"{Dns.GetHostName()}.local";

        // IP4_ADDRESS is a DWORD holding the address in network byte order.
        _ip4 = Marshal.AllocHGlobal(4);
        Marshal.Copy(ipv4.GetAddressBytes(), 0, _ip4, 4);

        var keys = txt.Keys.ToArray();
        var values = keys.Select(k => txt[k]).ToArray();
        _instance = NativeMethods.DnsServiceConstructInstance(
            ServiceName, hostName, _ip4, IntPtr.Zero, port, 0, 0, (uint)keys.Length, keys, values);
        if (_instance == IntPtr.Zero)
        {
            _log.Error(Tag, "DnsServiceConstructInstance failed.");
            Release();
            return false;
        }

        _request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
        Marshal.StructureToPtr(
            new DnsServiceRegisterRequest
            {
                Version = DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = _instance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                QueryContext = IntPtr.Zero,
                Credentials = IntPtr.Zero,
                UnicastEnabled = 0,
            },
            _request,
            fDeleteOld: false);

        _completed.Reset();
        var result = NativeMethods.DnsServiceRegister(_request, IntPtr.Zero);
        if (result != DnsRequestPending)
        {
            _log.Error(Tag, $"DnsServiceRegister failed: {result}.");
            Release();
            return false;
        }

        _callbackPending = true;
        if (!_completed.Wait(TimeSpan.FromSeconds(5)))
        {
            _log.Warn(Tag, "No confirmation from Windows yet; the service may still appear.");
            _registered = true;
            return true;
        }

        _callbackPending = false;

        _registered = _lastStatus == ErrorSuccess;
        if (_registered)
        {
            _log.Info(Tag, $"Advertising '{ServiceName}' -> {hostName} {ipv4}:{port}");
        }
        else
        {
            _log.Error(Tag, $"Registration of '{ServiceName}' failed with status {_lastStatus}.");
            Release();
        }

        return _registered;
    }

    private void OnRegisterComplete(uint status, IntPtr queryContext, IntPtr instance)
    {
        _lastStatus = status;
        if (instance != IntPtr.Zero && instance != _instance)
        {
            NativeMethods.DnsServiceFreeInstance(instance);
        }

        _completed.Set();
    }

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        _completed.Reset();
        var result = NativeMethods.DnsServiceDeRegister(_request, IntPtr.Zero);
        if (result == DnsRequestPending)
        {
            _callbackPending = !_completed.Wait(TimeSpan.FromSeconds(2));
        }

        _log.Info(Tag, $"Stopped advertising '{ServiceName}'.");
    }

    private void Release()
    {
        if (_instance != IntPtr.Zero)
        {
            NativeMethods.DnsServiceFreeInstance(_instance);
            _instance = IntPtr.Zero;
        }

        if (_request != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_request);
            _request = IntPtr.Zero;
        }

        if (_ip4 != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_ip4);
            _ip4 = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Unregister();
        if (_callbackPending)
        {
            // A late callback would touch freed memory and a collected delegate. Leaking a few bytes is safer.
            lock (Abandoned)
            {
                Abandoned.Add(this);
            }

            return;
        }

        Release();
        _completed.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterCompleteCallback(uint status, IntPtr queryContext, IntPtr instance);

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

    private static class NativeMethods
    {
        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr DnsServiceConstructInstance(
            string serviceName,
            string hostName,
            IntPtr ip4,
            IntPtr ip6,
            ushort port,
            ushort priority,
            ushort weight,
            uint propertiesCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void DnsServiceFreeInstance(IntPtr instance);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    }
}
