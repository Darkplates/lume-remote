using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace LumeRemote
{
    static class Brand
    {
        static readonly Image mark = Load();
        static Image Load()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LumeRemote.Brand.png"))
            { if (stream == null) return null; using (Image image = Image.FromStream(stream)) return new Bitmap(image); }
        }
        public static PictureBox Mark(int size) { return new PictureBox { Image = mark, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(size, size), Margin = new Padding(0, 0, 8, 0), TabStop = false }; }
        public static Icon Icon { get { try { return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; } } }
    }
}
