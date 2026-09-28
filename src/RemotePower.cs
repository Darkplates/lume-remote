using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LumeRemote
{
    public enum PowerAction : byte { Lock = 1, Restart = 2, ShutDown = 3 }
    public static class RemotePower
    {
        public static Task Request(PowerAction action)
        {
            if (!Enum.IsDefined(typeof(PowerAction), action)) throw new InvalidDataException("Unknown power action.");
            // Allow the authenticated receipt to leave before Windows ends the session.
            Task execution = Task.Run(async delegate
            {
                await Task.Delay(1200).ConfigureAwait(false);
                if (action == PowerAction.Lock) { if (!LockWorkStation()) throw new InvalidOperationException("Windows could not lock this desktop."); }
                else
                {
                    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), action == PowerAction.Restart ? "/r /t 0" : "/s /t 0") { UseShellExecute = false, CreateNoWindow = true };
                    using (Process process = Process.Start(info)) { process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Windows refused the power request."); }
                }
            });
            execution.ContinueWith(delegate(Task done) { SessionLog.Write(SessionLog.UserDirectory, "host", "power_request_failed", done.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
            return Task.FromResult(true);
        }
        [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
    }
}
