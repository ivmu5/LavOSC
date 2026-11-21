using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Laveyana.LavOSC;

internal sealed class OscHostAdvertisement : IAsyncDisposable
{
    private const uint RequestPending = 9506; // DNS_REQUEST_PENDING
    private static readonly CompleteCallback Callback = Complete;
    private readonly nint instance;
    private readonly Task<uint> registration;
    private int disposed;

    public OscHostAdvertisement(int port)
    {
        string host = Dns.GetHostName().Split('.')[0];
        instance = DnsServiceConstructInstance($"LavOSC ({host})._osc._udp.local", $"{host}.local",
            0, 0, checked((ushort)port), 0, 0, 0, 0, 0);
        if (instance == 0)
            throw new InvalidOperationException("Windows не смогла создать описание OSC-сервиса.");

        try
        {
            registration = InvokeAsync(register: true);
        }
        catch
        {
            DnsServiceFreeInstance(instance);
            throw;
        }
    }

    public async Task WaitUntilRegisteredAsync()
    {
        uint status = await registration.ConfigureAwait(false);
        if (status != 0)
            throw new Win32Exception((int)status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            if (await registration.ConfigureAwait(false) == 0)
            {
                uint status = await InvokeAsync(register: false).ConfigureAwait(false);
                if (status != 0)
                    throw new Win32Exception((int)status);
            }
        }
        finally
        {
            DnsServiceFreeInstance(instance);
        }
    }

    private Task<uint> InvokeAsync(bool register)
    {
        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        GCHandle handle = GCHandle.Alloc(completion);
        var request = new RegisterRequest
        {
            Version = 1,
            Instance = instance,
            Callback = Callback,
            Context = GCHandle.ToIntPtr(handle)
        };

        bool pending = false;
        try
        {
            uint status = register ? DnsServiceRegister(ref request, 0) : DnsServiceDeRegister(ref request, 0);
            pending = status == RequestPending;
            return pending ? completion.Task : Task.FromResult(status);
        }
        finally
        {
            // A pending operation owns its context until the native callback completes.
            if (!pending)
                handle.Free();
        }
    }

    private static void Complete(uint status, nint context, nint service)
    {
        var handle = GCHandle.FromIntPtr(context);
        var completion = (TaskCompletionSource<uint>)handle.Target!;
        handle.Free();
        if (service != 0)
            DnsServiceFreeInstance(service);
        completion.TrySetResult(status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CompleteCallback(uint status, nint context, nint instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint Instance;
        public CompleteCallback Callback;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint DnsServiceConstructInstance(string service, string host, nint ipv4, nint ipv6,
        ushort port, ushort priority, ushort weight, uint propertyCount, nint keys, nint values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceRegister(ref RegisterRequest request, nint cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceDeRegister(ref RegisterRequest request, nint cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsServiceFreeInstance(nint instance);
}
