using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumeRemote
{
    static class Theme
    {
        // High contrast keeps the user's system colours and native control chrome. Otherwise Lume follows the
        // Windows app mode (Settings > Personalization > Colors), read once at startup.
        public static readonly bool HighContrast = SystemInformation.HighContrast;
        public static readonly bool Light = !HighContrast && ReadLightMode();
        static bool ReadLightMode()
        {
            string forced = Environment.GetEnvironmentVariable("LUME_THEME");
            if (String.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return false;
            try { using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) { object value = key == null ? null : key.GetValue("AppsUseLightTheme"); return value is int && (int)value != 0; } }
            catch (Exception) { return false; }
        }
        static Color Pick(Color dark, Color light, Color system) { return HighContrast ? system : Light ? light : dark; }
        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        // Design tokens. Card = surface 1, Field = surface 2, Raised = surface 3, Muted = secondary text.
        public static readonly Color Background = Pick(Hex(0x12161C), Hex(0xF3F5F7), SystemColors.Window),
            Card = Pick(Hex(0x1A1F26), Hex(0xFFFFFF), SystemColors.Window),
            Field = Pick(Hex(0x242B33), Hex(0xEEF1F4), SystemColors.Window),
            Raised = Pick(Hex(0x2F3740), Hex(0xE2E7EC), SystemColors.Control),
            Border = Pick(Hex(0x3D4752), Hex(0xC9D1D9), SystemColors.WindowText),
            Text = Pick(Hex(0xF3F6F8), Hex(0x111827), SystemColors.WindowText),
            Muted = Pick(Hex(0xA7B0BA), Hex(0x4B5563), SystemColors.WindowText),
            Disabled = Pick(Hex(0x6B7683), Hex(0x9AA3AE), SystemColors.GrayText),
            Accent = Pick(Hex(0x6BE2C0), Hex(0x0E8A6B), SystemColors.Highlight),
            AccentHover = Pick(Hex(0x50D6B0), Hex(0x0B7A5E), SystemColors.Highlight),
            AccentPressed = Pick(Hex(0x3BC4AE), Hex(0x096A52), SystemColors.Highlight),
            AccentText = Pick(Hex(0x0B1512), Hex(0xFFFFFF), SystemColors.HighlightText),
            AccentSoft = Pick(Hex(0x1C3A35), Hex(0xDDF5EE), SystemColors.Window),
            Danger = Pick(Hex(0xF87171), Hex(0xC62828), SystemColors.WindowText),
            DangerSoft = Pick(Hex(0x3A2226), Hex(0xFDECEC), SystemColors.Window),
            Success = Pick(Hex(0x4ADE80), Hex(0x15803D), SystemColors.WindowText),
            SuccessSoft = Pick(Hex(0x173323), Hex(0xE3F6EA), SystemColors.Window),
            Warning = Pick(Hex(0xFBBF24), Hex(0xB45309), SystemColors.WindowText),
            Canvas = Pick(Hex(0x0A0D12), Hex(0x0A0D12), SystemColors.Window);
        public static readonly string FontName = InstalledFont("Segoe UI Variable Text", "Segoe UI");
        public static readonly string FontNameStrong = InstalledFont("Segoe UI Variable Display Semib", "Segoe UI Semibold");
        public static string InstalledFontName(string preferred, string fallback) { return InstalledFont(preferred, fallback); }
        static string InstalledFont(string preferred, string fallback)
        {
            try { using (System.Drawing.Text.InstalledFontCollection fonts = new System.Drawing.Text.InstalledFontCollection()) foreach (FontFamily family in fonts.Families) if (String.Equals(family.Name, preferred, StringComparison.OrdinalIgnoreCase)) return preferred; }
            catch (Exception) { }
            return fallback;
        }
        // Sizes at 96 DPI.
        public const int ButtonHeight = 36, RowHeight = 56, Radius = 8;

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
        { return new Label { Text = text, AutoSize = true, ForeColor = color, Font = new Font(size >= 14 ? FontNameStrong : FontName, size), Margin = new Padding(0, 0, 0, 10), MaximumSize = new Size(430, 0) }; }
        public static Button Button(string text, bool primary) { return Button(text, primary ? ButtonKind.Primary : ButtonKind.Secondary); }
        public static Button DangerButton(string text) { return Button(text, ButtonKind.Danger); }
        public static Button Button(string text, ButtonKind kind)
        {
            ReadableButton button = new ReadableButton { Text = text, Kind = kind, Height = ButtonHeight, Width = 180, FlatStyle = FlatStyle.Flat, Font = new Font(FontNameStrong, 10), Cursor = Cursors.Default, Margin = new Padding(0, 6, 8, 8) };
            button.FlatAppearance.BorderSize = HighContrast ? 1 : 0; button.FlatAppearance.BorderColor = Text; return button;
        }
        public static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle box, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath(); int d = Math.Max(1, radius * 2);
            if (radius <= 0) { path.AddRectangle(box); return path; }
            path.AddArc(box.X, box.Y, d, d, 180, 90); path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90); path.AddArc(box.X, box.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        public static TextBox Box(bool multiline)
        { return new TextBox { BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font(FontName, 10), Multiline = multiline, Width = 398, Height = multiline ? 96 : 29, Margin = new Padding(0, 0, 0, 14) }; }
        public static ComboBox Combo()
        { return new ThemedComboBox { Width = 398, Font = new Font(FontName, 10), Margin = new Padding(0, 0, 0, 14) }; }
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
                    int enabled = Light ? 0 : 1; if (DwmSetWindowAttribute(handle, 20, ref enabled, 4) != 0) DwmSetWindowAttribute(handle, 19, ref enabled, 4);
                }
                if (Light) return;
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

    // A rounded surface-1 card. Children keep Theme.Card as their background inside the padding.
    class CardPanel : Panel
    {
        public CardPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Padding = new Padding(20); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (Parent != null) BackColor = Parent.BackColor; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); if (Theme.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(Theme.Radius)))
            using (SolidBrush fill = new SolidBrush(Theme.Card)) using (Pen edge = new Pen(Theme.Light ? Theme.Border : Theme.Field)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
        }
    }

    // A navigation tab: text with an accent underline when selected.
    sealed class TabButton : Button
    {
        bool selected, hover;
        public bool Selected { get { return selected; } set { selected = value; AccessibleDescription = value ? "Selected" : null; Invalidate(); } }
        public TabButton() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; AccessibleRole = AccessibleRole.PageTab; Height = 40; Margin = new Padding(0, 0, 24, 0); Font = new Font(Theme.FontNameStrong, 10.5f); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Theme.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height - Theme.Px(4)), selected || hover ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (selected) using (SolidBrush line = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(line, 0, Height - Theme.Px(3), Width, Theme.Px(3));
            if (Focused && ShowFocusCues) using (Pen ring = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(ring, 0, 0, Width - 1, Height - 1);
        }
    }

    // A rounded status pill with a coloured dot ("Access on" / "Access off"). A plain Control with a fixed
    // height, so no Label auto-size rules can collapse it.
    sealed class StatusPill : Control
    {
        bool on;
        public bool On { get { return on; } set { on = value; Invalidate(); } }
        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false); TabStop = false; AccessibleRole = AccessibleRole.StaticText;
            Font = new Font(Theme.FontNameStrong, 9.5f); Size = new Size(150, 28); MinimumSize = new Size(60, 28); Margin = new Padding(0, 4, 0, 8);
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; FitText(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitText(); }
        void FitText() { Width = TextRenderer.MeasureText(Text ?? "", Font).Width + Theme.Px(40); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color parent = Parent != null ? Parent.BackColor : Theme.Card; e.Graphics.Clear(parent);
            if (Theme.HighContrast) { TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, SystemColors.WindowText, TextFormatFlags.VerticalCenter); return; }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = on ? Theme.SuccessSoft : Theme.Field, ink = on ? Theme.Success : Theme.Muted;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2)) using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            int dot = Theme.Px(8); using (SolidBrush brush = new SolidBrush(ink)) e.Graphics.FillEllipse(brush, Theme.Px(12), (Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Theme.Px(26), 0, Width - Theme.Px(30), Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // A text action drawn in the accent colour; used instead of LinkLabel so it renders the same everywhere.
    sealed class TextAction : Label
    {
        bool active;
        public bool Active { get { return active; } set { active = value; ForeColor = value ? Theme.Accent : Theme.Muted; Cursor = value ? Cursors.Hand : Cursors.Default; TabStop = value; AccessibleRole = value ? AccessibleRole.Link : AccessibleRole.StaticText; } }
        public TextAction() { AutoSize = true; Font = new Font(Theme.FontName, 10); ForeColor = Theme.Muted; Margin = new Padding(0, 0, 0, 6); }
        protected override void OnMouseEnter(EventArgs e) { if (active) Font = new Font(Font, FontStyle.Underline); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Font = new Font(Font, FontStyle.Regular); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { if (active) base.OnClick(e); }
    }

    // One line of a capability list: a check (allowed) or a dash (not allowed) followed by text.
    sealed class CapabilityRow : Label
    {
        readonly bool allowed;
        public CapabilityRow(string text, bool allowed) { this.allowed = allowed; Text = text; AutoSize = false; Height = 28; Width = 440; Font = new Font(Theme.FontName, 10); Margin = new Padding(0, 0, 0, 2); ForeColor = allowed ? Theme.Text : Theme.Muted; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Background); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int size = Theme.Px(18), y = (Height - size) / 2;
            using (SolidBrush disc = new SolidBrush(allowed ? Theme.AccentSoft : Theme.Field)) e.Graphics.FillEllipse(disc, 0, y, size, size);
            using (Pen mark = new Pen(allowed ? Theme.Accent : Theme.Muted, Theme.Px(2)))
            {
                if (allowed) e.Graphics.DrawLines(mark, new[] { new Point(Theme.Px(5), y + Theme.Px(9)), new Point(Theme.Px(8), y + Theme.Px(12)), new Point(Theme.Px(13), y + Theme.Px(6)) });
                else e.Graphics.DrawLine(mark, Theme.Px(5), y + size / 2, Theme.Px(13), y + size / 2);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(size + Theme.Px(10), 0, Width - size - Theme.Px(10), Height), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    enum ButtonKind { Primary, Secondary, Danger, Ghost }

    // Rounded, flat button with hover, pressed, disabled and keyboard-focus states drawn from the theme tokens.
    sealed class ReadableButton : Button
    {
        ButtonKind kind = ButtonKind.Secondary; bool hover, pressed;
        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public ReadableButton() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs args)
        {
            if (Theme.HighContrast) { base.OnPaint(args); return; }
            Graphics g = args.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color parent = Parent != null ? Parent.BackColor : Theme.Background; g.Clear(parent);
            Color fill, text, edge = Color.Empty;
            if (!Enabled) { fill = kind == ButtonKind.Ghost ? parent : Theme.Field; text = Theme.Disabled; }
            else if (kind == ButtonKind.Primary) { fill = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent; text = Theme.AccentText; }
            else if (kind == ButtonKind.Danger) { fill = pressed || hover ? Theme.DangerSoft : parent; text = Theme.Danger; edge = Theme.Danger; }
            else if (kind == ButtonKind.Ghost) { fill = pressed ? Theme.Raised : hover ? Theme.Field : parent; text = Theme.Text; }
            else { fill = pressed ? Theme.Border : hover ? Theme.Raised : Theme.Field; text = Theme.Text; edge = Theme.Border; }
            Rectangle box = new Rectangle(1, 1, Width - 3, Height - 3); int radius = Math.Min(Theme.Px(Theme.Radius), box.Height / 2);
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(box, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (!edge.IsEmpty) using (Pen pen = new Pen(edge)) g.DrawPath(pen, path);
                if (Focused && ShowFocusCues) using (Pen ring = new Pen(Theme.Accent, Theme.Px(2))) g.DrawPath(ring, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
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
