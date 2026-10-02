using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public static class PermanentAccess
    {
        public const string ServiceName = "LumeRemoteHost";
        public static bool Installed { get { using (ServiceController service = new ServiceController(ServiceName)) { try { var status = service.Status; return true; } catch (InvalidOperationException) { return false; } } } }
        public static void Disable()
        {
            if (!File.Exists(TrustedStore.Machine.HostFile)) return;
            try { TrustedStore.Machine.Disable(); }
            catch (IOException)
            {
                // Already privileged callers (including --disable-host) wrote directly and failed:
                // starting another elevated copy would only repeat that.
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw;
                // The settings channel did not answer (busy or stopped): disabling must still be
                // possible, so ask Windows for administrator permission and disable directly.
                Process process;
                try { process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--disable-host") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); }
                catch (Win32Exception) { throw new IOException("Access is still enabled: the settings service did not answer and administrator approval was not given. Use Remove Windows service in Settings."); }
                using (process)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new IOException("Access could not be disabled. Use Remove Windows service in Settings.");
                }
            }
        }
        public static Task Install(bool remove, bool update = false)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "permanent-access.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Extract the full Lume Remote package before enabling permanent access.");
            string owner = WindowsIdentity.GetCurrent().User.Value;
            ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -OwnerSid " + owner + (remove ? " -Remove" : update ? " -Update" : "")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process process = Process.Start(info);
            return Task.Run(delegate { using (process) { process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Permanent-access setup failed. See setup.log next to the app. No firewall or Windows sign-in settings were changed."); } });
        }
        public static void Initialize(string owner, bool enable = true)
        {
            new SecurityIdentifier(owner);
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator setup is required.");
            if (!enable && !File.Exists(TrustedStore.Machine.HostFile)) throw new InvalidOperationException("No installed host settings to update.");
            TrustedStore.Machine.ChangeHost(delegate(HostPreferences host)
            { if (File.Exists(TrustedStore.Machine.HostFile) && host.OwnerSid != owner) throw new UnauthorizedAccessException("A different Windows user owns this host."); host.OwnerSid = owner; if (enable) host.Enabled = true; });
        }
        public static void RunAgent(string eventName)
        {
            const string prefix = "Global\\LumeHostStop-";
            if (!WindowsIdentity.GetCurrent().IsSystem || !eventName.StartsWith(prefix, StringComparison.Ordinal) || !Invitation.IsHex(eventName.Substring(prefix.Length), 32)) throw new UnauthorizedAccessException("Only the installed Windows service can start the host agent.");
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(eventName, EventWaitHandleRights.Synchronize))
            using (PersistentHost host = new PersistentHost(TrustedStore.Machine, delegate(HostPreferences preferences)
            {
                using (DesktopAttachment desktop = new DesktopAttachment())
                { Screen[] screens = Screen.AllScreens; return new MonitorSource(screens[Math.Min(preferences.Display, screens.Length - 1)].Bounds, Profile.All[3]); }
            }, delegate { }))
            {
                Task running = host.Run();
                try
                {
                    while (!stop.WaitOne(2000) && !running.IsCompleted)
                    {
                        HostPreferences preferences = TrustedStore.Machine.ReadHost(); SYSTEM_POWER_STATUS power;
                        bool keepAwake = preferences.Enabled && preferences.KeepAwake && GetSystemPowerStatus(out power) && power.ACLineStatus == 1;
                        SetThreadExecutionState(keepAwake ? 0x80000001u : 0x80000000u);
                    }
                }
                finally { SetThreadExecutionState(0x80000000); host.Dispose(); running.Wait(10000); }
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    }

    // The service runs only its own protected executable in the active console
    // session. Windows still enforces sign-in and secure attention requirements.
    sealed class WindowsHostService : ServiceBase
    {
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Task running;
        public WindowsHostService() { ServiceName = PermanentAccess.ServiceName; CanStop = true; AutoLog = false; }
        protected override void OnStart(string[] args) { stop.Reset(); running = Task.Run((Action)Supervise); }
        protected override void OnStop() { RequestAdditionalTime(15000); stop.Set(); if (running != null) running.Wait(14000); }
        void Supervise()
        {
            Process worker = null; EventWaitHandle workerStop = null; uint session = UInt32.MaxValue;
            try
            {
                EnablePrivilege("SeTcbPrivilege"); EnablePrivilege("SeAssignPrimaryTokenPrivilege"); EnablePrivilege("SeIncreaseQuotaPrivilege");
                while (!stop.WaitOne(1000))
                {
                    uint current = WTSGetActiveConsoleSessionId();
                    // Owner-only policy: run the console worker only when the console
                    // belongs to the owner, or when nobody is signed in (logon/lock)
                    // and no other user is signed in anywhere. End it otherwise.
                    string owner = ReadOwnerSid();
                    bool allowed = owner != null && AllowedForConsole(current, owner);
                    if (worker != null && (worker.HasExited || current != session || !allowed)) { StopWorker(worker, workerStop); worker = null; workerStop = null; }
                    if (worker != null || current == UInt32.MaxValue || !allowed) continue;
                    try
                    {
                        string name = "Global\\LumeHostStop-" + Guid.NewGuid().ToString("N"); EventWaitHandleSecurity security = new EventWaitHandleSecurity();
                        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
                        bool created; workerStop = new EventWaitHandle(false, EventResetMode.ManualReset, name, out created, security);
                        worker = StartWorker(current, name); session = current;
                    }
                    catch (Exception error) { if (workerStop != null) workerStop.Dispose(); workerStop = null; WriteStatus("Host agent waiting: " + error.Message); stop.WaitOne(5000); }
                }
            }
            catch (Exception error) { WriteStatus("Host service failed: " + error.Message); }
            finally { StopWorker(worker, workerStop); }
        }
        static void WriteStatus(string message) { try { string path = Path.Combine(TrustedStore.MachineDirectory, "status.txt"); TrustedStore.CheckNoReparse(path); File.WriteAllText(path, DateTime.UtcNow.ToString("u") + "\r\n" + message); } catch { } }
        static string ReadOwnerSid()
        {
            try { if (!File.Exists(TrustedStore.Machine.HostFile)) return null; return TrustedStore.Machine.ReadHost().OwnerSid; } catch { return null; }
        }
        // Allowed when the console user is the owner, or when there is no interactive
        // console user (logon/lock/secure desktop) and no other user is signed in.
        static bool AllowedForConsole(uint session, string owner)
        {
            IntPtr token;
            if (WTSQueryUserToken(session, out token))
            {
                try { using (WindowsIdentity identity = new WindowsIdentity(token)) return identity.User != null && identity.User.Value == owner; }
                catch { return false; }
                finally { CloseHandle(token); }
            }
            return NoOtherUserSignedIn(owner);
        }
        static bool NoOtherUserSignedIn(string owner)
        {
            IntPtr info; int count;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) return false; // fail closed
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    WTS_SESSION_INFO entry = (WTS_SESSION_INFO)Marshal.PtrToStructure((IntPtr)(info.ToInt64() + (long)i * size), typeof(WTS_SESSION_INFO));
                    // WTSActive=0, WTSConnected=1, WTSDisconnected=4.
                    if (entry.State != 0 && entry.State != 1 && entry.State != 4) continue;
                    IntPtr token;
                    if (WTSQueryUserToken((uint)entry.SessionId, out token))
                    {
                        try { using (WindowsIdentity identity = new WindowsIdentity(token)) if (identity.User != null && identity.User.Value != owner) return false; }
                        catch { return false; }
                        finally { CloseHandle(token); }
                    }
                }
                return true;
            }
            finally { WTSFreeMemory(info); }
        }
        static void StopWorker(Process process, EventWaitHandle signal)
        {
            try { if (signal != null) signal.Set(); if (process != null && !process.HasExited && !process.WaitForExit(10000)) process.Kill(); }
            catch (InvalidOperationException) { }
            finally { if (process != null) process.Dispose(); if (signal != null) signal.Dispose(); }
        }
        static Process StartWorker(uint session, string signal)
        {
            IntPtr own = IntPtr.Zero, token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x000B, out own) || !DuplicateTokenEx(own, 0xF01FF, IntPtr.Zero, 2, 1, out token) || !SetTokenInformation(token, 12, ref session, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LumeRemote.exe");
                STARTUPINFO start = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), desktop = "winsta0\\default" }; PROCESS_INFORMATION result;
                if (!CreateProcessAsUser(token, path, new StringBuilder("\"" + path + "\" --host-agent " + signal), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, AppDomain.CurrentDomain.BaseDirectory, ref start, out result)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { return Process.GetProcessById((int)result.processId); } finally { CloseHandle(result.process); CloseHandle(result.thread); }
            }
            finally { if (token != IntPtr.Zero) CloseHandle(token); if (own != IntPtr.Zero) CloseHandle(own); }
        }
        static void EnablePrivilege(string name)
        {
            IntPtr token; if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { TOKEN_PRIVILEGES privileges = new TOKEN_PRIVILEGES { count = 1, attributes = 2 }; if (!LookupPrivilegeValue(null, name, out privileges.luid) || !AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { CloseHandle(token); }
        }
        [StructLayout(LayoutKind.Sequential, Pack=4)] struct TOKEN_PRIVILEGES { public uint count; public long luid; public uint attributes; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO { public int cb; public string reserved, desktop, title; public uint x,y,width,height,xChars,yChars,fill,flags; public short show,reservedSize; public IntPtr reservedPointer,input,output,error; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public uint processId, threadId; }
        [StructLayout(LayoutKind.Sequential)] struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSEnumerateSessions(IntPtr server, uint reserved, uint version, out IntPtr sessionInfo, out int count);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr existing,uint access,IntPtr security,int level,int type,out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool SetTokenInformation(IntPtr token,int kind,ref uint information,int length);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcessAsUser(IntPtr token,string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref STARTUPINFO start,out PROCESS_INFORMATION result);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out long luid);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref TOKEN_PRIVILEGES privileges,int length,IntPtr previous,IntPtr returned);
    }

    sealed class DesktopAttachment : IDisposable
    {
        static readonly bool system = IsSystem();
        static bool IsSystem() { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.IsSystem; }
        IntPtr previous, current;
        public DesktopAttachment()
        {
            if (!system) return;
            previous = GetThreadDesktop(GetCurrentThreadId()); current = OpenInputDesktop(0, false, 0x01FF);
            if (current == IntPtr.Zero || !SetThreadDesktop(current)) { int error = Marshal.GetLastWin32Error(); if (current != IntPtr.Zero) CloseDesktop(current); current = IntPtr.Zero; throw new Win32Exception(error, "Windows did not allow access to the active desktop."); }
        }
        public void Dispose() { if (current != IntPtr.Zero) { SetThreadDesktop(previous); CloseDesktop(current); current = IntPtr.Zero; } }
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
        [DllImport("user32.dll", SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    }
}
