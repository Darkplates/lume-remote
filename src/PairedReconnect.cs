using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    public static class PairedReconnect
    {
        public static bool Transient(Exception error)
        {
            if (error is InvalidDataException) return false;
            return error is IOException || error is TimeoutException || error is SocketException || error is WebSocketException;
        }
        public static int DelayMilliseconds(int attempt)
        { int[] seconds = { 1, 2, 5, 10, 20, 30 }; return seconds[Math.Max(0, Math.Min(seconds.Length - 1, attempt - 1))] * 1000; }
        public static async Task<PairedLink> Connect(Func<Action<string>, CancellationToken, Task<PairedLink>> connect,
            Action<string> status, CancellationToken cancellation, Func<int, CancellationToken, Task> delay = null)
        {
            if (delay == null) delay = Task.Delay;
            int attempt = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (attempt < Int32.MaxValue) attempt++;
                int milliseconds = DelayMilliseconds(attempt);
                status("Connection lost. Reconnecting in " + milliseconds / 1000 + " s (attempt " + attempt + "). Disconnect cancels.");
                await delay(milliseconds, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    // Each attempt negotiates a fresh P2P route, TLS identity and session secret.
                    PairedLink link = await connect(status, cancellation).ConfigureAwait(false);
                    if (cancellation.IsCancellationRequested) { link.Dispose(); cancellation.ThrowIfCancellationRequested(); }
                    return link;
                }
                catch (Exception error) { cancellation.ThrowIfCancellationRequested(); if (!Transient(error)) throw; }
            }
        }
    }
}
