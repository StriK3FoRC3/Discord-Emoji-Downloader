using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Discord Emoji Downloader")]
[assembly: System.Reflection.AssemblyProduct("Discord Emoji Downloader")]

namespace EmojiDownloader
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--verify")
            {
                try { Downloader.Verify().GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification.txt"), "PASS: links and IDs; existing files skipped before any network request; cross-format and legacy filename duplicates; automatic animated GIF / static WebP selection.\r\n"); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification.txt"), "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            using (var form = new MainForm())
            {
                if (args.Length > 0 && args[0] == "--ui-check")
                {
                    try { form.CheckUI(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-verification.txt"), "PASS: pixel scrolling animates over multiple frames and settles; line-number alignment; horizontal scrolling blocked; Clear; fixed default size; multiline paste and Undo; focus and caret; no wrapping; rounded controls at 100%, 125%, 150%, and 200%.\r\n"); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-verification.txt"), "FAIL: " + ex); Environment.ExitCode = 1; }
                    return;
                }
                if (args.Length > 0 && args[0] == "--preview")
                {
                    form.Show(); form.Refresh(); Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        bool rendered;
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            IntPtr dc = graphics.GetHdc();
                            try { rendered = PrintWindow(form.Handle, dc, 2); }
                            finally { graphics.ReleaseHdc(dc); }
                        }
                        if (!rendered || bitmap.GetPixel(20, 60).ToArgb() == Color.Black.ToArgb()) form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"));
                    }
                    return;
                }
                Application.Run(form);
            }
        }
    }

    sealed class InputBatch
    {
        public readonly List<string> Ids = new List<string>();
        public int Invalid;
        public int Duplicates;
    }

    static class Downloader
    {
        static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        static readonly Regex SavedName = new Regex(@"^(\d{5,25})(?:_\d+)?\.(?:gif|webp|png|jpe?g|avif)$", RegexOptions.IgnoreCase);
        static readonly Regex IdPath = new Regex(@"^/emojis/(\d{5,25})(?:\.(?:png|gif|webp|jpe?g|avif))?/?$", RegexOptions.IgnoreCase);
        public static InputBatch Parse(string text)
        {
            var result = new InputBatch(); var seen = new HashSet<string>();
            foreach (Match match in Regex.Matches(text, @"https?://[^\s<>`"" ,;]+|<a?:\w+:(\d{5,25})>|[^\s` ,;]+"))
            {
                string token = match.Value.TrimEnd(')', ']'); string id = null; Uri uri;
                if (Regex.IsMatch(token, @"^\d{5,25}$")) id = token;
                else if (token.StartsWith("<")) id = match.Groups[1].Value;
                else if (Uri.TryCreate(token, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http") &&
                    (uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)) && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo))
                { var m = IdPath.Match(uri.AbsolutePath); if (m.Success) id = m.Groups[1].Value; }
                if (string.IsNullOrEmpty(id)) result.Invalid++;
                else if (seen.Add(id)) result.Ids.Add(id);
                else result.Duplicates++;
            }
            return result;
        }
        public static string Extension(byte[] bytes)
        {
            if (bytes.Length >= 6 && System.Text.Encoding.ASCII.GetString(bytes, 0, 6).StartsWith("GIF8")) return ".gif";
            if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10) return ".png";
            if (bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ".webp";
            if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
            return null;
        }
        public static bool IsAnimatedWebP(byte[] bytes)
        {
            if (Extension(bytes) != ".webp") throw new IOException("Invalid WebP image.");
            // Walk actual RIFF chunks; image payload bytes may coincidentally contain 'ANIM'.
            bool animation = false;
            for (int offset = 12; offset < bytes.Length; )
            {
                if (bytes.Length - offset < 8) throw new IOException("Truncated WebP image.");
                string tag = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                uint length = BitConverter.ToUInt32(bytes, offset + 4);
                long next = offset + 8L + length + (length & 1);
                if (next > bytes.Length) throw new IOException("Truncated WebP image.");
                if (tag == "ANIM" || tag == "ANMF" || (tag == "VP8X" && length >= 1 && (bytes[offset + 8] & 2) != 0)) animation = true;
                offset = (int)next;
            }
            return animation;
        }
        static async Task<byte[]> SelectFormat(byte[] webp, Func<Task<byte[]>> gif)
        {
            if (!IsAnimatedWebP(webp)) return webp;
            byte[] bytes = await gif();
            if (Extension(bytes) != ".gif") throw new IOException("Discord could not provide this animated emoji as GIF.");
            using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream))
                if (image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time) < 2) throw new IOException("Discord returned a still image for an animated emoji.");
            return bytes;
        }
        public static async Task<byte[]> Fetch(string id, CancellationToken token)
        {
            byte[] webp = await FetchFormat(id, token, ".webp?size=1280&animated=true");
            return await SelectFormat(webp, delegate { return FetchFormat(id, token, ".gif?size=1280"); });
        }
        static async Task<byte[]> FetchFormat(string id, CancellationToken token, string format)
        {
            using (var response = await Client.GetAsync("https://cdn.discordapp.com/emojis/" + id + format, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new IOException("Image exceeds the 10 MB limit.");
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[8192]; int count;
                    while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, token)) > 0)
                    { if (buffer.Length + count > 10 * 1024 * 1024) throw new IOException("Image exceeds the 10 MB limit."); buffer.Write(chunk, 0, count); }
                    var data = buffer.ToArray();
                    if (Extension(data) != (format.StartsWith(".gif") ? ".gif" : ".webp")) throw new IOException("Discord returned an unexpected image format.");
                    return data;
                }
            }
        }
        public static string Save(string folder, string id, byte[] data)
        {
            Directory.CreateDirectory(folder);
            string ext = Extension(data); if (ext == null) throw new IOException("Invalid image data.");
            if (AlreadyDownloaded(folder, id)) return null;
            string target = Path.Combine(folder, id + ext);
            FileStream file;
            try { file = new FileStream(target, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) { if (File.Exists(target)) return null; throw; }
            try { using (file) file.Write(data, 0, data.Length); }
            catch { try { File.Delete(target); } catch { } throw; }
            return target;
        }
        public static bool AlreadyDownloaded(string folder, string id)
        {
            if (!Directory.Exists(folder)) return false;
            foreach (string file in Directory.EnumerateFiles(folder, id + "*"))
            {
                var match = SavedName.Match(Path.GetFileName(file));
                if (match.Success && match.Groups[1].Value == id) return true;
            }
            return false;
        }
        public static async Task<string> DownloadOne(string folder, string id, CancellationToken token, Func<string, CancellationToken, Task<byte[]>> fetch = null)
        {
            if (AlreadyDownloaded(folder, id)) return null;
            token.ThrowIfCancellationRequested();
            byte[] data = await (fetch == null ? Fetch(id, token) : fetch(id, token));
            token.ThrowIfCancellationRequested();
            return Save(folder, id, data);
        }
        public static async Task Verify()
        {
            var batch = Parse("https://cdn.discordapp.com/emojis/1088953028486369290.webp?size=96&animated=true\nhttps://media.discordapp.net/emojis/1088953028486369290.gif <a:test:123456789> https://evil.example/emojis/123456.png https://cdn.discordapp.com.evil.example/emojis/123456.png");
            if (batch.Ids.Count != 2 || batch.Duplicates != 1 || batch.Invalid != 2) throw new Exception("Parser verification failed.");
            var ids = Parse("1088953028486369290, 123456789\n1088953028486369290");
            if (ids.Ids.Count != 2 || ids.Duplicates != 1 || ids.Invalid != 0) throw new Exception("ID verification failed.");
            if (Extension(new byte[] { 1, 2, 3 }) != null) throw new Exception("Signature verification failed.");
            byte[] data = await Fetch("1088953028486369290", CancellationToken.None);
            if (Extension(data) != ".gif") throw new Exception("Expected GIF for sample emoji.");
            using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms))
            { if (img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time) < 2) throw new Exception("Sample is not animated."); }
            byte[] webp = await FetchFormat("1088953028486369290", CancellationToken.None, ".webp?size=1280&animated=true");
            if (!IsAnimatedWebP(webp)) throw new Exception("Animated WebP verification failed.");
            byte[] still = await FetchFormat("1088953028486369290", CancellationToken.None, ".webp?size=1280");
            if (IsAnimatedWebP(still)) throw new Exception("Expected a static WebP fixture.");
            byte[] selected = await SelectFormat(still, delegate { throw new Exception("Static image must not request GIF."); });
            if (!Object.ReferenceEquals(still, selected)) throw new Exception("Static WebP was changed.");
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification-downloads");
            var first = Save(folder, "1088953028486369290", data); var second = Save(folder, "1088953028486369290", data);
            if (first == null || second != null || !File.Exists(first)) throw new Exception("Duplicate save verification failed.");
            if (Save(folder, "1088953028486369290", selected) != null) throw new Exception("Duplicate saved in another format.");
            var duplicate = await DownloadOne(folder, "1088953028486369290", CancellationToken.None, delegate { throw new Exception("Duplicate must not make a network request."); });
            if (duplicate != null) throw new Exception("Duplicate download was not skipped.");
            string legacy = Path.Combine(folder, "123456789_1.gif"); File.WriteAllBytes(legacy, data);
            if (!AlreadyDownloaded(folder, "123456789") || AlreadyDownloaded(folder, "12345678")) throw new Exception("Legacy filename duplicate check failed.");
            var third = Save(folder, "987654321", selected);
            if (!third.EndsWith(".webp") || !File.Exists(third)) throw new Exception("WebP save verification failed.");
            File.Delete(first); File.Delete(third); File.Delete(legacy); Directory.Delete(folder);
        }
    }

    sealed class HiddenScrollTextBox : RichTextBox
    {
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        static extern IntPtr ReplaceText(IntPtr hwnd, int message, IntPtr wParam, string text);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr ScrollPoint(IntPtr hwnd, int message, IntPtr wParam, ref Point point);
        readonly System.Windows.Forms.Timer scrollTimer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch scrollClock = new System.Diagnostics.Stopwatch();
        readonly ContextMenuStrip editMenu = new ContextMenuStrip { BackColor = Color.FromArgb(29, 34, 49), ForeColor = Color.White, ShowImageMargin = false };
        double scrollTarget, scrollCurrent;
        bool resettingHorizontal;
        public HiddenScrollTextBox()
        {
            DetectUrls = false; EnableAutoDragDrop = false; AutoWordSelection = false;
            scrollTimer.Tick += delegate { AnimateScroll(); };
            var undo = editMenu.Items.Add("Undo", null, delegate { if (CanUndo) Undo(); });
            editMenu.Items.Add(new ToolStripSeparator());
            var cut = editMenu.Items.Add("Cut", null, delegate { Cut(); });
            var copy = editMenu.Items.Add("Copy", null, delegate { Copy(); });
            var paste = editMenu.Items.Add("Paste", null, delegate { SendMessage(Handle, 0x0302, IntPtr.Zero, IntPtr.Zero); });
            editMenu.Items.Add("Select All", null, delegate { SelectAll(); });
            editMenu.Opening += delegate
            {
                undo.Enabled = !ReadOnly && CanUndo; cut.Enabled = !ReadOnly && SelectionLength > 0; copy.Enabled = SelectionLength > 0;
                try { paste.Enabled = !ReadOnly && Clipboard.ContainsText(); } catch (ExternalException) { paste.Enabled = false; }
            };
            ContextMenuStrip = editMenu;
        }
        Point ScrollPosition
        {
            get { Point point = Point.Empty; ScrollPoint(Handle, 0x04DD, IntPtr.Zero, ref point); return point; }
        }
        void SetScrollPosition(int y)
        {
            resettingHorizontal = true;
            try { Point point = new Point(0, Math.Max(0, y)); ScrollPoint(Handle, 0x04DE, IntPtr.Zero, ref point); }
            finally { resettingHorizontal = false; }
        }
        void StopScroll() { scrollTimer.Stop(); scrollClock.Reset(); }
        void AnimateScroll()
        {
            double elapsed = Math.Min(100, scrollClock.Elapsed.TotalMilliseconds); scrollClock.Restart();
            scrollCurrent += (scrollTarget - scrollCurrent) * (1 - Math.Exp(-elapsed / 45));
            bool done = Math.Abs(scrollTarget - scrollCurrent) < 0.5;
            int requested = (int)Math.Round(done ? scrollTarget : scrollCurrent);
            SetScrollPosition(requested);
            if (done || Math.Abs(ScrollPosition.Y - requested) > 1) StopScroll();
            NotifyViewport();
        }
        void QueueScroll(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines == 0) return;
            if (!scrollTimer.Enabled) scrollTarget = scrollCurrent = ScrollPosition.Y;
            int maximum = Math.Max(0, GetPositionFromCharIndex(TextLength).Y + ScrollPosition.Y + Font.Height + 3 - ClientSize.Height);
            double distance = lines < 0 ? ClientSize.Height : lines * Font.Height;
            scrollTarget = Math.Max(0, Math.Min(maximum, scrollTarget - delta / 120.0 * distance));
            if (!scrollTimer.Enabled) { scrollClock.Restart(); scrollTimer.Start(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { scrollTimer.Stop(); scrollTimer.Dispose(); editMenu.Dispose(); }
            base.Dispose(disposing);
        }
        public event EventHandler ViewportChanged;
        public int FirstVisibleLine { get { return IsHandleCreated ? SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32() : 0; } }
        public int LineCount { get { return IsHandleCreated ? SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32() : 1; } }
        void LockHorizontal()
        {
            if (!IsHandleCreated || resettingHorizontal) return;
            Point position = ScrollPosition;
            if (position.X != 0) SetScrollPosition(position.Y);
        }
        void NotifyViewport()
        {
            LockHorizontal();
            if (ViewportChanged != null) ViewportChanged(this, EventArgs.Empty);
        }
        protected override void OnHScroll(EventArgs e)
        {
            base.OnHScroll(e);
            if (!resettingHorizontal) NotifyViewport();
        }
        protected override void OnVScroll(EventArgs e)
        {
            base.OnVScroll(e);
            if (!resettingHorizontal) NotifyViewport();
        }
        public new void ScrollToCaret()
        {
            StopScroll(); base.ScrollToCaret(); NotifyViewport();
        }
        protected override void WndProc(ref Message m)
        {
            if (resettingHorizontal) { base.WndProc(ref m); return; }
            switch (m.Msg)
            {
                case 0x000C: case 0x0005: case 0x0030: case 0x00B1: case 0x00B5: case 0x00B6: case 0x00B7: case 0x00C2:
                case 0x0100: case 0x0102: case 0x0115: case 0x0201: case 0x0304: case 0x00C7:
                    StopScroll(); break;
            }
            if (m.Msg == 0x0114 || m.Msg == 0x020E) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x00B6) m.WParam = IntPtr.Zero; // Allow vertical line scrolling only.
            if (m.Msg == 0x0302 && !ReadOnly) // Ctrl+V, Shift+Insert, and the Paste context menu.
            {
                try
                {
                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    if (text.Length > 0)
                    {
                        PasteLines(text); NotifyViewport(); m.Result = IntPtr.Zero; return;
                    }
                }
                catch (ExternalException) { /* Let the native control retry a busy clipboard. */ }
            }
            if (m.Msg == 0x020A || m.Msg == 0x020E) // Wheel / horizontal trackpad wheel.
            {
                int delta = unchecked((short)(m.WParam.ToInt64() >> 16));
                bool horizontal = m.Msg == 0x020E || (m.WParam.ToInt64() & 4) != 0;
                if (!horizontal) QueueScroll(delta);
                NotifyViewport(); m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case 0x000C: case 0x0005: case 0x0030: // Text, size, font.
                case 0x00B1: case 0x00B5: case 0x00B6: case 0x00B7: case 0x00C2:
                case 0x0100: case 0x0101: case 0x0102: case 0x0113: case 0x0115:
                case 0x0200: case 0x0201: case 0x0202: case 0x0304:
                    NotifyViewport(); break;
            }
        }
        void PasteLines(string text)
        {
            // Native multiline EDIT controls expect CRLF, even when the source uses bare LF.
            text = Regex.Replace(text, @"\r\n|[\r\n\u2028\u2029]", "\r\n");
            int available = Math.Max(0, MaxLength - (TextLength - SelectionLength));
            if (text.Length > available) text = text.Substring(0, available).TrimEnd('\r');
            ReplaceText(Handle, 0x00C2, new IntPtr(1), text); // Replace selection; preserve Undo.
            ScrollToCaret();
        }
        public void VerifyPasting()
        {
            string first = "https://cdn.discordapp.com/emojis/1088953028486369290.webp?size=16&example=" + new string('x', 240);
            foreach (string newline in new[] { "\n", "\r\n", "\r", "\u2028", "\u2029" })
            {
                Clear(); PasteLines(first + newline + newline + "https://media.discordapp.net/emojis/123456789.gif" + newline + "987654321");
                if (SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32() != 4 || Downloader.Parse(Text).Ids.Count != 3)
                    throw new Exception("Pasted line breaks or links were lost.");
                SelectAll(); PasteLines("123456789\n987654321");
                if (Lines.Length != 2 || Downloader.Parse(Text).Ids.Count != 2) throw new Exception("Multiline replacement paste failed.");
                Undo(); // Undo must restore the pasted batch.
                if (Downloader.Parse(Text).Ids.Count != 3) throw new Exception("Paste Undo failed.");
            }
            Clear();
        }
        public void VerifyScrolling()
        {
            Text = new string('x', 240) + "\r\n" + string.Join("\r\n", new string[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20" });
            Select(0, 0); ScrollToCaret();
            if (WordWrap || SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32() != 20) throw new Exception("Input wraps long lines.");
            int before = SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            SetScrollPosition(5);
            if (ScrollPosition.Y != 5) throw new Exception("Input does not support pixel scrolling.");
            SetScrollPosition(0);
            SendMessage(Handle, 0x020A, new IntPtr(unchecked(-360 << 16)), IntPtr.Zero);
            int lastPosition = ScrollPosition.Y, movingFrames = 0;
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (scrollTimer.Enabled && timeout.ElapsedMilliseconds < 1500)
            {
                Application.DoEvents();
                int position = ScrollPosition.Y;
                if (position < lastPosition) throw new Exception("Smooth scrolling reversed direction.");
                if (position != lastPosition) movingFrames++;
                lastPosition = position;
                System.Threading.Thread.Sleep(5);
            }
            if (SystemInformation.MouseWheelScrollLines != 0 && (movingFrames < 3 || scrollTimer.Enabled)) throw new Exception("Smooth scrolling did not animate and settle.");
            if (SystemInformation.MouseWheelScrollLines != 0 && SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32() <= before) throw new Exception("Vertical wheel scrolling failed.");
            using (var image = new Bitmap(Parent.Width, Parent.Height))
            {
                Parent.DrawToBitmap(image, Parent.ClientRectangle);
                if (((RoundedInputPanel)Parent).FirstPaintedLine != FirstVisibleLine + 1) throw new Exception("Line numbers lost vertical scroll alignment.");
            }
            Select(0, 0); ScrollToCaret();
            int xBefore = GetPositionFromCharIndex(0).X;
            SendMessage(Handle, 0x020A, new IntPtr(unchecked((-120 << 16) | 4)), IntPtr.Zero);
            int xAfter = GetPositionFromCharIndex(0).X;
            if (xAfter != xBefore) throw new Exception("Shift-wheel moved the text horizontally.");
            SendMessage(Handle, 0x020E, new IntPtr(120 << 16), IntPtr.Zero);
            Select(240, 0); ScrollToCaret();
            xAfter = GetPositionFromCharIndex(0).X;
            if (xAfter != xBefore) throw new Exception("Caret or trackpad moved the text horizontally: " + xBefore + " -> " + xAfter + "; scroll " + ScrollPosition);
            if ((GetWindowLong(Handle, -16) & 0x00300000) != 0) throw new Exception("Scrollbar became visible.");
            Clear();
        }
    }

    sealed class RoundedInputPanel : Panel
    {
        public bool InputFocused;
        public HiddenScrollTextBox Input;
        public int FirstPaintedLine { get; private set; }
        public void UpdateGutter()
        {
            if (Input == null) return;
            int digits = TextRenderer.MeasureText(Input.LineCount.ToString(), Input.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int width = Math.Max(28, digits + 16);
            if (Padding.Left != width) Padding = new Padding(width, Padding.Top, Padding.Right, Padding.Bottom);
            Invalidate();
        }
        public RoundedInputPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(17, 20, 30); Padding = new Padding(10);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, Width - 3, Height - 3); int diameter = 20;
            using (var path = new GraphicsPath())
            {
                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90); path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
                using (var fill = new SolidBrush(Color.FromArgb(29, 34, 49))) e.Graphics.FillPath(fill, path);
                if (InputFocused) using (var pen = new Pen(Color.FromArgb(139, 127, 246))) e.Graphics.DrawPath(pen, path);
            }
            if (Input == null || !Input.IsHandleCreated) return;
            FirstPaintedLine = Input.FirstVisibleLine + 1;
            using (var divider = new Pen(Color.FromArgb(48, 54, 73)))
                e.Graphics.DrawLine(divider, Input.Left - 6, Input.Top, Input.Left - 6, Input.Bottom - 1);
            int last = Math.Min(Input.LineCount, Input.FirstVisibleLine + Input.Height / Input.Font.Height + 2);
            var clipping = e.Graphics.Save();
            e.Graphics.SetClip(new Rectangle(4, Input.Top, Input.Left - 14, Input.Height), CombineMode.Intersect);
            for (int line = Input.FirstVisibleLine; line < last; line++)
            {
                int index = Input.GetFirstCharIndexFromLine(line); if (index < 0) break;
                int y = Input.Top + Input.GetPositionFromCharIndex(index).Y;
                if (y + Input.Font.Height + 2 <= Input.Top || y >= Input.Bottom) continue;
                TextRenderer.DrawText(e.Graphics, (line + 1).ToString(), Input.Font,
                    new Rectangle(4, y, Input.Left - 14, Input.Font.Height + 2), Color.FromArgb(121, 134, 162),
                    TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            e.Graphics.Restore(clipping);
        }
    }

    sealed class FlatButton : Button
    {
        public FlatButton(string text, Color color)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text; BackColor = color; ForeColor = Color.White; FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0; Font = new Font("Segoe UI", 10, FontStyle.Bold);
            Cursor = Cursors.Hand; Height = 34; UseVisualStyleBackColor = false;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Inset the entire outline so every rounded edge stays inside the control.
            var rect = new Rectangle(2, 2, Width - 5, Height - 5);
            using (var shape = new GraphicsPath())
            {
                int diameter = Math.Min(16, rect.Height);
                shape.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90); shape.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                shape.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90); shape.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90); shape.CloseFigure();
                using (var brush = new SolidBrush(Enabled ? BackColor : Color.FromArgb(39, 44, 62))) e.Graphics.FillPath(brush, shape);
                if (Focused) using (var pen = new Pen(Color.FromArgb(174, 166, 255))) e.Graphics.DrawPath(pen, shape);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? ForeColor : Color.FromArgb(143, 151, 174), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class MainForm : Form
    {
        [DllImport("user32.dll")]
        static extern bool GetCaretPos(out Point position);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Keep native window controls while matching the caption to the app on Windows 11.
            try
            {
                int dark = 1, background = ColorTranslator.ToWin32(Color.FromArgb(17, 20, 30)), text = ColorTranslator.ToWin32(Color.FromArgb(229, 233, 246));
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(Handle, 35, ref background, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref background, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        readonly HiddenScrollTextBox input;
        readonly FlatButton download;
        readonly FlatButton clear;
        readonly Label hint;
        readonly ToolTip statusTip = new ToolTip();
        CancellationTokenSource cancellation;
        readonly string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloaded-Emojis");
        static readonly Size DefaultClientSize = new Size(461, 188);
        public MainForm()
        {
            Text = "Discord Emoji Downloader"; ClientSize = DefaultClientSize;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(17, 20, 30); ForeColor = Color.White;
            Font = new Font("Segoe UI", 10); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            Controls.Add(layout);
            var inputPanel = new RoundedInputPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            input = new HiddenScrollTextBox { Multiline = true, ScrollBars = RichTextBoxScrollBars.None, WordWrap = false,
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 34, 49), ForeColor = Color.FromArgb(229, 233, 246),
                BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10), MaxLength = 200000, TabIndex = 0 };
            inputPanel.Input = input;
            input.ViewportChanged += delegate { inputPanel.Invalidate(); };
            input.TextChanged += delegate { inputPanel.UpdateGutter(); };
            input.FontChanged += delegate { inputPanel.UpdateGutter(); };
            hint = new Label { Text = "Paste emoji links or IDs…", AutoSize = true, BackColor = input.BackColor,
                ForeColor = Color.FromArgb(150, 160, 182), Location = new Point(28, 10) };
            inputPanel.Layout += delegate { hint.Location = new Point(inputPanel.Padding.Left, inputPanel.Padding.Top); };
            inputPanel.Controls.Add(input); inputPanel.Controls.Add(hint); hint.BringToFront();
            inputPanel.UpdateGutter();
            hint.Click += delegate { input.Focus(); };
            inputPanel.Click += delegate { input.Focus(); };
            input.GotFocus += delegate { hint.Visible = false; inputPanel.InputFocused = true; inputPanel.Invalidate(); };
            input.LostFocus += delegate { hint.Visible = input.Text.Length == 0; inputPanel.InputFocused = false; inputPanel.Invalidate(); };
            layout.Controls.Add(inputPanel, 0, 0);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            download = new FlatButton("Download", Color.FromArgb(100, 90, 235)) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Enabled = false, TabIndex = 1 };
            var open = new FlatButton("Open Folder", Color.FromArgb(46, 52, 71)) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), TabIndex = 2 };
            clear = new FlatButton("Clear", Color.FromArgb(46, 52, 71)) { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 3 };
            clear.MouseEnter += delegate { if (clear.Enabled) { clear.BackColor = Color.FromArgb(230, 125, 137); clear.ForeColor = Color.FromArgb(65, 27, 32); } };
            clear.MouseLeave += delegate { clear.BackColor = Color.FromArgb(46, 52, 71); clear.ForeColor = Color.White; };
            clear.EnabledChanged += delegate { if (!clear.Enabled) { clear.BackColor = Color.FromArgb(46, 52, 71); clear.ForeColor = Color.White; } };
            clear.Click += delegate { input.Clear(); input.Focus(); };
            row.Controls.Add(download, 0, 0); row.Controls.Add(open, 1, 0); row.Controls.Add(clear, 2, 0); layout.Controls.Add(row, 0, 2);
            input.TextChanged += delegate { hint.Visible = !input.Focused && input.Text.Length == 0; download.Enabled = cancellation == null && !string.IsNullOrWhiteSpace(input.Text); if (cancellation == null) download.Text = "Download"; };
            download.Click += async delegate { await Download(); };
            open.Click += delegate
            {
                try { Directory.CreateDirectory(folder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open folder"); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (cancellation != null) { e.Cancel = true; cancellation.Cancel(); download.Text = "Stopping…"; } };
            FormClosed += delegate { statusTip.Dispose(); };
        }
        public void CheckUI()
        {
            Show(); Activate(); input.Focus(); Application.DoEvents();
            Point caret;
            if (!input.Focused || hint.Visible || !GetCaretPos(out caret)) throw new Exception("Input focus or native caret missing.");
            input.VerifyScrolling();
            input.VerifyPasting();
            input.Text = "123456789\r\n\r\n987654321";
            using (var image = new Bitmap(input.Parent.Width, input.Parent.Height))
            {
                input.Parent.DrawToBitmap(image, input.Parent.ClientRectangle);
                if (((RoundedInputPanel)input.Parent).FirstPaintedLine != input.FirstVisibleLine + 1 || input.LineCount != 3 || Downloader.Parse(input.Text).Ids.Count != 2)
                    throw new Exception("Line numbers changed the text or did not match the viewport.");
            }
            using (var image = new Bitmap(Width, Height))
            { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "line-numbers-preview.png")); }
            input.Text = "123456789\r\n987654321"; clear.PerformClick();
            if (input.TextLength != 0 || !input.Focused || download.Enabled) throw new Exception("Clear button failed.");
            float dpi = CurrentAutoScaleDimensions.Width / 96f;
            Size logical = new Size((int)Math.Round(ClientSize.Width / dpi), (int)Math.Round(ClientSize.Height / dpi));
            if (FormBorderStyle != FormBorderStyle.FixedSingle || MaximizeBox || logical != DefaultClientSize) throw new Exception("Fixed default window size failed.");
            float previous = 1;
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Scale(new SizeF(scale / previous, scale / previous)); previous = scale;
                PerformLayout(); Application.DoEvents();
                using (var image = new Bitmap(input.Parent.Width, input.Parent.Height))
                {
                    input.Parent.DrawToBitmap(image, input.Parent.ClientRectangle);
                    foreach (var corner in new[] { new Point(1, 1), new Point(image.Width - 2, 1), new Point(1, image.Height - 2), new Point(image.Width - 2, image.Height - 2) })
                        if (image.GetPixel(corner.X, corner.Y).ToArgb() != BackColor.ToArgb()) throw new Exception("Input corner is not rounded at " + scale);
                }
                foreach (Control button in download.Parent.Controls)
                {
                    if (button.Bottom > button.Parent.ClientSize.Height || button.Top < 0) throw new Exception("Button clipped at " + scale);
                    using (var image = new Bitmap(button.Width, button.Height))
                    {
                        button.DrawToBitmap(image, button.ClientRectangle);
                        foreach (var corner in new[] { new Point(2, 2), new Point(button.Width - 3, 2), new Point(2, button.Height - 3), new Point(button.Width - 3, button.Height - 3) })
                            if (image.GetPixel(corner.X, corner.Y).ToArgb() != button.Parent.BackColor.ToArgb()) throw new Exception("Rounded button corner clipped at " + scale);
                    }
                }
            }
        }
        async Task Download()
        {
            var batch = Downloader.Parse(input.Text);
            if (batch.Ids.Count == 0) { MessageBox.Show(this, "Paste a Discord emoji link or numeric emoji ID.", "Discord Emoji Downloader"); return; }
            cancellation = new CancellationTokenSource(); input.ReadOnly = true; download.Enabled = false; clear.Enabled = false;
            int saved = 0, attempted = 0, skipped = batch.Duplicates; bool stopped = false;
            var errors = new List<string>();
            if (batch.Invalid > 0) errors.Add(batch.Invalid + " invalid entries skipped.");
            try
            {
                foreach (string id in batch.Ids)
                {
                    if (cancellation.IsCancellationRequested) { stopped = true; break; }
                    download.Text = (++attempted) + " / " + batch.Ids.Count;
                    try
                    {
                        string file = await Downloader.DownloadOne(folder, id, cancellation.Token);
                        if (file == null) skipped++; else saved++;
                    }
                    catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) { stopped = true; break; } errors.Add(id + ": request timed out."); }
                    catch (Exception ex) { errors.Add(id + ": " + ex.Message); }
                }
            }
            finally
            {
                cancellation.Dispose(); cancellation = null; input.ReadOnly = false; clear.Enabled = true;
                download.Enabled = !string.IsNullOrWhiteSpace(input.Text); download.Text = stopped ? "Stopped" : saved == 0 && skipped > 0 ? "Skipped " + skipped : "Saved " + saved;
                statusTip.SetToolTip(download, saved + " saved; " + skipped + " duplicates skipped.");
            }
            if (errors.Count > 0) MessageBox.Show(this, saved + " saved.\r\n\r\n" + string.Join("\r\n", errors.GetRange(0, Math.Min(8, errors.Count)).ToArray()) + (errors.Count > 8 ? "\r\n…more errors omitted." : ""), "Download results");
        }
    }
}
