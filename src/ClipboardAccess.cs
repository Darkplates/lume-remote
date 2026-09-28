using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LumeRemote
{
    public static class ClipboardAccess
    {
        static int busy;
        public static Task<string> Read()
        { return Read(delegate { return Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""; }); }
        internal static Task<string> Read(Func<string> read)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("The remote clipboard is busy. Try again shortly.");
            TaskCompletionSource<string> result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread worker = new Thread(delegate()
            {
                try
                {
                    string value = read();
                    if (System.Text.Encoding.UTF8.GetByteCount(value) > 262144) throw new InvalidOperationException("Clipboard text must be smaller than 256 KiB per action.");
                    result.TrySetResult(value);
                }
                catch (Exception) { result.TrySetException(new InvalidOperationException("The remote clipboard is unavailable or exceeds 256 KiB. Try copying a smaller text selection.")); }
                finally { Interlocked.Exchange(ref busy, 0); }
            }) { IsBackground = true, Name = "Lume clipboard reader" };
            worker.SetApartmentState(ApartmentState.STA); try { worker.Start(); } catch { Interlocked.Exchange(ref busy, 0); throw; }
            return result.Task;
        }
        public static Task<bool> Write(string text)
        {
            return Write(text, delegate(string value) { if (value.Length == 0) Clipboard.Clear(); else Clipboard.SetText(value, TextDataFormat.UnicodeText); });
        }
        internal static Task<bool> Write(string text, Action<string> write)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("The remote clipboard is busy. Try again shortly.");
            TaskCompletionSource<bool> result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread worker = new Thread(delegate()
            {
                try { write(text); result.TrySetResult(true); }
                catch (ExternalException) { result.TrySetException(new InvalidOperationException("The remote clipboard is in use. Try again shortly.")); }
                catch (Exception) { result.TrySetException(new InvalidOperationException("Windows could not update the remote clipboard.")); }
                finally { Interlocked.Exchange(ref busy, 0); }
            }) { IsBackground = true, Name = "Lume clipboard" };
            worker.SetApartmentState(ApartmentState.STA);
            try { worker.Start(); } catch { Interlocked.Exchange(ref busy, 0); throw; }
            return result.Task;
        }
    }
}
