using System.Diagnostics;
using System.Security.Principal;

namespace NtfyTray.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    public bool IsPrimary { get; }
    public SingleInstanceGuard()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Missing user SID");
        mutex = new Mutex(true, $"Local\\NtfyTray.{sid}.{Process.GetCurrentProcess().SessionId}", out var created);
        IsPrimary = created;
    }
    public void Dispose() { if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); }
}
