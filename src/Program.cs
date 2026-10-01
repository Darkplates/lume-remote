using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Lume Remote")]
[assembly: AssemblyDescription("Consent-based native Windows remote desktop")]
[assembly: AssemblyCompany("Lume Remote contributors")]
[assembly: AssemblyProduct("Lume Remote")]
[assembly: AssemblyVersion("0.12.1.0")]
[assembly: AssemblyFileVersion("0.12.1.0")]

namespace LumeRemote
{
    static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            Native.SetDefaultDllDirectories(0xA00); Native.SetProcessDPIAware();
            if (args.Length == 1 && args[0] == "--service") { System.ServiceProcess.ServiceBase.Run(new WindowsHostService()); return; }
            if (args.Length == 2 && args[0] == "--host-agent") { PermanentAccess.RunAgent(args[1]); return; }
            if (args.Length == 2 && args[0] == "--initialize-host") { try { PermanentAccess.Initialize(args[1]); } catch { Environment.ExitCode = 1; } return; }
            if (args.Length == 2 && args[0] == "--update-host") { try { PermanentAccess.Initialize(args[1], false); } catch { Environment.ExitCode = 1; } return; }
            if (args.Length == 1 && args[0] == "--disable-host") { try { PermanentAccess.Disable(); } catch { Environment.ExitCode = 1; } return; }
            if (args.Length == 1 && args[0] == "--request-host-update")
            {
                try { PermanentAccess.Install(false, true).GetAwaiter().GetResult(); MessageBox.Show("The installed host was updated. Your paired computers and access setting were kept.", "Lume Remote"); }
                catch (Exception error) { Environment.ExitCode = 1; MessageBox.Show(error.Message, "Lume - Update did not finish", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                return;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "Lume Remote", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            // A clicked invitation link opens only the viewer, alongside any running dashboard.
            if (Array.Exists(args, InvitationLinks.IsLaunch)) { if (args.Length == 1) InvitationLinks.Open(args[0]); return; }
            InvitationLinks.Register();
            Application.Run(new MainForm());
        }
    }
}
