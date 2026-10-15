using System.Security.Cryptography;
using System.Text;
namespace KeywordViewer.Core;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private RegisteredWaitHandle? registration;
    public bool IsFirst { get; }
    public SingleInstanceGuard(string storageDirectory, Action activate)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storageDirectory).ToUpperInvariant())))[..24];
        var name = @"Local\LocalKeywordViewer-" + identity;
        activation = new(false, EventResetMode.AutoReset, name + "-activate");
        mutex = new(false, name);
        try { IsFirst = mutex.WaitOne(0); } catch (AbandonedMutexException) { IsFirst = true; }
        if (IsFirst) registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => activate(), null, Timeout.Infinite, false);
        else activation.Set();
    }
    public void Dispose()
    {
        registration?.Unregister(null); registration = null;
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose(); activation.Dispose();
    }
}
