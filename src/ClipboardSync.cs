using System;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    // Opt-in text sync; simultaneous edits pause rather than silently replacing either copy.
    public sealed class ClipboardSync : IDisposable
    {
        readonly Func<Task<string>> localRead, remoteRead;
        readonly Func<string, Task> localWrite, remoteWrite;
        readonly Func<bool> active;
        string previousLocal, previousRemote;
        int busy;
        volatile bool disposed;
        public ClipboardSync(Func<Task<string>> localRead, Func<string, Task> localWrite, Func<Task<string>> remoteRead, Func<string, Task> remoteWrite, Func<bool> active)
        { this.localRead = localRead; this.localWrite = localWrite; this.remoteRead = remoteRead; this.remoteWrite = remoteWrite; this.active = active; }
        public async Task Poll()
        {
            if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            try
            {
                if (!active()) { Dispose(); return; }
                string remote = await remoteRead().ConfigureAwait(false);
                string local = await localRead().ConfigureAwait(false);
                if (disposed || !active()) return;
                if (previousLocal == null) { previousLocal = local; previousRemote = remote; return; }
                bool changedLocal = local != previousLocal, changedRemote = remote != previousRemote;
                if (changedLocal && changedRemote && local != remote) throw new InvalidOperationException("Clipboard sync paused: both clipboards changed. Use Send or Get to choose which text to keep.");
                if (local != remote)
                {
                    if (changedLocal) { await remoteWrite(local).ConfigureAwait(false); remote = local; }
                    else if (changedRemote) { await localWrite(remote).ConfigureAwait(false); local = remote; }
                }
                previousLocal = local; previousRemote = remote;
            }
            catch { Dispose(); throw; }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
        public void Dispose() { disposed = true; }
    }
}
