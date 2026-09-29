using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumeRemote
{
    static class Theme
    {
        // High contrast keeps the user's system colours and native control chrome.
        public static readonly bool HighContrast = SystemInformation.HighContrast;
        public static readonly Color Background = HighContrast ? SystemColors.Window : Color.FromArgb(18, 22, 28),
            Card = HighContrast ? SystemColors.Window : Color.FromArgb(27, 33, 42),
            Field = HighContrast ? SystemColors.Window : Color.FromArgb(35, 43, 54),
            Text = HighContrast ? SystemColors.WindowText : Color.FromArgb(235, 242, 246),
            Muted = HighContrast ? SystemColors.WindowText : Color.FromArgb(159, 174, 189),
            Accent = HighContrast ? SystemColors.Highlight : Color.FromArgb(107, 226, 192),
            AccentText = HighContrast ? SystemColors.HighlightText : Background,
            Disabled = HighContrast ? SystemColors.GrayText : Muted,
            Border = HighContrast ? SystemColors.WindowText : Color.FromArgb(58, 70, 86),
            Canvas = HighContrast ? SystemColors.Window : Color.FromArgb(10, 13, 18);

        // Layouts are written in 96-DPI units. The process is system-DPI-aware (app.manifest), so one DPI,
        // the same one WinForms uses for AutoScaleMode.Dpi, applies to every window for the process lifetime.
        static int dpi;
        public static int Dpi
        {
            get
            {
                if (dpi == 0) { int value = 96; try { using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero)) value = (int)Math.Round(screen.DpiX); } catch (Exception) { } dpi = value > 0 ? value : 96; }
                return dpi;
            }
        }
        public static float Factor { get { return Dpi / 96F; } }
        // Only for sizes applied after a form has scaled (resize handlers, owner drawing, list columns).
        public static int Px(int value) { return (int)Math.Round(value * Dpi / 96.0); }
        // Call before adding controls; EndLayout scales everything added in between once.
        public static void BeginLayout(Form form) { form.SuspendLayout(); form.AutoScaleDimensions = new SizeF(96F, 96F); form.AutoScaleMode = AutoScaleMode.Dpi; }
        public static void EndLayout(Form form) { form.ResumeLayout(false); form.PerformLayout(); Apply(form); }

        public static Label Label(string text, float size, Color color)
        { return new Label { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Segoe UI", size), Margin = new Padding(0, 0, 0, 10), MaximumSize = new Size(430, 0) }; }
        public static Button Button(string text, bool primary)
        {
            Button button = new ReadableButton { Text = text, Height = 42, Width = 180, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Field, ForeColor = primary ? AccentText : Text, Font = new Font("Segoe UI Semibold", 10), Cursor = Cursors.Hand, Margin = new Padding(0, 6, 10, 10) };
            button.FlatAppearance.BorderSize = HighContrast ? 1 : 0; button.FlatAppearance.BorderColor = Text; return button;
        }
        public static TextBox Box(bool multiline)
        { return new TextBox { BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10), Multiline = multiline, Width = 398, Height = multiline ? 96 : 29, Margin = new Padding(0, 0, 0, 14) }; }
        public static ComboBox Combo()
        { return new ThemedComboBox { Width = 398, Font = new Font("Segoe UI", 10), Margin = new Padding(0, 0, 0, 14) }; }
        public static FlowLayoutPanel Column()
        { return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(26), BackColor = Card }; }

        // Dark title bar, scroll bars, list header and spin buttons for a form and every control added to it later.
        static readonly ConditionalWeakTable<Control, object> themed = new ConditionalWeakTable<Control, object>();
        static readonly object marker = new object();
        public static void Apply(Form form) { if (!HighContrast) Track(form); }
        static void Track(Control control)
        {
            object existing; if (themed.TryGetValue(control, out existing)) return; themed.Add(control, marker);
            Style(control);
            control.HandleCreated += delegate { StyleWindow(control); }; if (control.IsHandleCreated) StyleWindow(control);
            control.ControlAdded += delegate(object sender, ControlEventArgs e) { Track(e.Control); };
            foreach (Control child in control.Controls) Track(child);
        }
        static void Style(Control control)
        {
            ListView list = control as ListView;
            if (list != null && list.View == View.Details && !list.OwnerDraw)
            {
                list.OwnerDraw = true; list.DrawColumnHeader += PaintHeader;
                list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
                list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            }
            NumericUpDown number = control as NumericUpDown;
            if (number != null)
            {
                number.BackColor = Field; number.ForeColor = Text; number.BorderStyle = BorderStyle.FixedSingle;
                foreach (Control part in number.Controls) if (!(part is TextBox)) { part.Paint += PaintSpin; part.MouseDown += delegate(object sender, MouseEventArgs e) { ((Control)sender).Invalidate(); }; part.MouseUp += delegate(object sender, MouseEventArgs e) { ((Control)sender).Invalidate(); }; }
                number.EnabledChanged += delegate(object sender, EventArgs e) { foreach (Control part in ((Control)sender).Controls) part.Invalidate(); };
            }
        }
        static void StyleWindow(Control control)
        {
            IntPtr handle = control.Handle;
            try
            {
                Form form = control as Form;
                if (form != null)
                {
                    // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 20H1 and later, 19 on 1809-1909.
                    int enabled = 1; if (DwmSetWindowAttribute(handle, 20, ref enabled, 4) != 0) DwmSetWindowAttribute(handle, 19, ref enabled, 4);
                }
                if (control is ComboBox) SetWindowTheme(handle, "DarkMode_CFD", null);
                else if (control is TextBoxBase || control is ListBox || control is ListView || control is TreeView || (control is ScrollableControl && form == null)) SetWindowTheme(handle, "DarkMode_Explorer", null);
                if (control is ListView) { IntPtr header = SendMessage(handle, 0x101F, IntPtr.Zero, IntPtr.Zero); if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null); }
            }
            // Older Windows versions ignore unknown theme names; missing DWM or uxtheme leaves the light chrome.
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        static void PaintHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush fill = new SolidBrush(Card)) e.Graphics.FillRectangle(fill, e.Bounds);
            using (Pen line = new Pen(Border)) { e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1); e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + Px(4), e.Bounds.Right - 1, e.Bounds.Bottom - Px(5)); }
            TextFormatFlags align = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
            Rectangle text = new Rectangle(e.Bounds.X + Px(6), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Px(12)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, text, Muted, align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        static void PaintSpin(object sender, PaintEventArgs e)
        {
            // Paints over the light system spin buttons after they draw; pressed halves stay visible.
            Control buttons = (Control)sender; Rectangle area = buttons.ClientRectangle; if (area.Width < 2 || area.Height < 2) return;
            bool enabled = buttons.Parent == null || buttons.Parent.Enabled; int half = area.Height / 2;
            using (SolidBrush fill = new SolidBrush(Field)) e.Graphics.FillRectangle(fill, area);
            if (enabled && Control.MouseButtons == MouseButtons.Left)
            {
                Point mouse = buttons.PointToClient(Control.MousePosition);
                if (area.Contains(mouse)) using (SolidBrush pressed = new SolidBrush(Border)) e.Graphics.FillRectangle(pressed, mouse.Y < half ? new Rectangle(0, 0, area.Width, half) : new Rectangle(0, half, area.Width, area.Height - half));
            }
            using (Pen line = new Pen(Border)) e.Graphics.DrawLine(line, 0, 0, 0, area.Height);
            int size = Math.Max(2, Math.Min(Px(4), half / 2)), x = area.Width / 2, up = half / 2, down = half + (area.Height - half) / 2;
            using (SolidBrush glyph = new SolidBrush(enabled ? Text : Disabled))
            {
                e.Graphics.FillPolygon(glyph, new[] { new Point(x - size, up + size / 2 + 1), new Point(x + size + 1, up + size / 2 + 1), new Point(x, up - size / 2) });
                e.Graphics.FillPolygon(glyph, new[] { new Point(x - size, down - size / 2), new Point(x + size + 1, down - size / 2), new Point(x, down + size / 2 + 1) });
            }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string subApplication, string idList);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    sealed class ReadableButton : Button
    {
        protected override void OnPaint(PaintEventArgs args)
        {
            if (Enabled || Theme.HighContrast) { base.OnPaint(args); return; }
            args.Graphics.Clear(Theme.Field);
            TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // A drop-down list whose selection field, list, border and arrow follow the theme. WinForms paints the
    // flat border and arrow button with light system colours, so they are repainted after each WM_PAINT.
    sealed class ThemedComboBox : ComboBox
    {
        public ThemedComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            if (Theme.HighContrast) return;
            FlatStyle = FlatStyle.Flat; DrawMode = DrawMode.OwnerDrawFixed; BackColor = Theme.Field; ForeColor = Theme.Text;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, selected = !edit && (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? Theme.Accent : Theme.Field, fore = !Enabled ? Theme.Disabled : selected ? Theme.AccentText : Theme.Text;
            using (SolidBrush fill = new SolidBrush(back)) e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index >= 0 && e.Index < Items.Count)
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, new Rectangle(e.Bounds.X + Theme.Px(3), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Theme.Px(3)), e.Bounds.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (edit && Focused && (e.State & DrawItemState.NoFocusRect) == 0) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, fore, back);
            base.OnDrawItem(e);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x000F && DrawMode != DrawMode.Normal && IsHandleCreated) PaintChrome();
        }
        void PaintChrome()
        {
            Rectangle client = ClientRectangle; if (client.Width < 8 || client.Height < 6) return;
            int arrow = SystemInformation.HorizontalScrollBarArrowWidth;
            Rectangle button = new Rectangle(client.Right - arrow - 2, 1, arrow + 1, client.Height - 2);
            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                using (SolidBrush fill = new SolidBrush(Theme.Field)) graphics.FillRectangle(fill, button);
                using (Pen inner = new Pen(Theme.Field)) graphics.DrawRectangle(inner, 1, 1, client.Width - 3, client.Height - 3);
                using (Pen border = new Pen(Enabled && (Focused || DroppedDown) ? Theme.Accent : Theme.Border)) graphics.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);
                int size = Math.Max(3, Theme.Px(4)), x = button.X + button.Width / 2, y = button.Y + button.Height / 2;
                using (SolidBrush glyph = new SolidBrush(Enabled ? Theme.Text : Theme.Disabled))
                    graphics.FillPolygon(glyph, new[] { new Point(x - size, y - size / 2), new Point(x + size + 1, y - size / 2), new Point(x, y + size / 2 + 1) });
            }
        }
    }
}
