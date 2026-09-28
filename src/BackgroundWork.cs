using System;
using System.Threading;
using System.Threading.Tasks;

namespace LumeRemote
{
    internal static class BackgroundWork
    {
        // Blocking session loops must not occupy the pool needed by UI continuations,
        // consent receipts, heartbeat timers and file acknowledgements.
        public static Task Run(Action action)
        { return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default); }
        public static Task<T> Run<T>(Func<T> action)
        { return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default); }
    }
}
