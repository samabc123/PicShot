using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class TransparentOverlay : Forms.Form
    {
        const int CardWidth = 120;
        const int CardHeight = 91;
        const int HangerWidth = 128;
        const int HangerStep = 132;
        const int CardTop = 17;
        static readonly Color KeyColor = Color.Magenta;
        const int GwlExStyle = -20;
        const int WsExLayered = 0x00080000;
        const uint LwaColorKey = 0x00000001;

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr handle, int index);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr handle, int index, int value);

        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(
            IntPtr handle,
            int colorKey,
            byte alpha,
            uint flags);

        readonly Dictionary<string, Bitmap> thumbnails = new Dictionary<string, Bitmap>();
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord pressedItem;
        Point pressPoint;
        bool dragging;
        bool suppressClick;
        bool expanded;

        public Action CaptureRequested;
        public Action ImportRequested;
        public Action AddFolderRequested;
        public Action CollapseRequested;
        public Action<ImageRecord> CopyRequested;
        public Action<ImageRecord> PinRequested;
        public Action<ImageRecord> OpenRequested;
        public Action<ImageRecord> LocateRequested;
        public Action<ImageRecord> SaveRequested;
        public Action<ImageRecord> DeleteRequested;
        public Action<ImageRecord> DragRequested;

        public TransparentOverlay(int left, int top, int width, int height)
        {
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            SetStyle(Forms.ControlStyles.UserPaint |
                Forms.ControlStyles.AllPaintingInWmPaint |
                Forms.ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            MouseDown += OverlayMouseDown;
            MouseMove += OverlayMouseMove;
            MouseUp += OverlayMouseUp;
            MouseClick += OverlayMouseClick;
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            int style = GetWindowLong(Handle, GwlExStyle);
            SetWindowLong(Handle, GwlExStyle, style | WsExLayered);
            SetLayeredWindowAttributes(
                Handle,
                ColorTranslator.ToWin32(KeyColor),
                0,
                LwaColorKey);
        }

        public void SetItems(IEnumerable<ImageRecord> records)
        {
            items = records == null ? new List<ImageRecord>() : records.ToList();
            Invalidate();
        }

        public void SetExpanded(bool value)
        {
            expanded = value;
            Height = value ? 132 : 6;
            Invalidate();
        }

        protected override void OnPaintBackground(Forms.PaintEventArgs args)
        {
            args.Graphics.Clear(KeyColor);
        }

        protected override void OnPaint(Forms.PaintEventArgs args)
        {
            args.Graphics.Clear(KeyColor);
            if (!expanded) return;

            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.None;
            using (var rope = new Pen(Color.FromArgb(144, 174, 181), 1))
                graphics.DrawLine(rope, 0, 15, Width, 15);

            DrawHandle(graphics);
            for (int index = 0; index < items.Count && index < 50; index++)
                DrawCard(graphics, items[index], index);
        }

        void DrawHandle(Graphics graphics)
        {
            int left = (Width - 162) / 2;
            using (var fill = new SolidBrush(Color.FromArgb(245, 252, 250)))
            using (var border = new Pen(Color.FromArgb(213, 231, 227), 1))
            {
                graphics.FillRectangle(fill, left, 6, 162, 18);
                graphics.DrawRectangle(border, left, 6, 161, 17);
            }
            using (var text = new SolidBrush(Color.FromArgb(135, 151, 154)))
            using (var font = new Font("Segoe UI", 9f))
                graphics.DrawString("···", font, text, left + 73, 6);
        }

        void DrawCard(Graphics graphics, ImageRecord item, int index)
        {
            int left = 4 + index * HangerStep;
            int center = left + HangerWidth / 2;

            using (var line = new SolidBrush(Color.FromArgb(157, 128, 102)))
                graphics.FillRectangle(line, center - 1, 0, 2, 12);
            using (var peg = new SolidBrush(Color.FromArgb(214, 163, 107)))
            using (var pegBorder = new Pen(Color.FromArgb(169, 121, 76), 1))
            {
                graphics.FillRectangle(peg, center - 8, 4, 17, 20);
                graphics.DrawRectangle(pegBorder, center - 8, 4, 16, 19);
            }

            var card = new Rectangle(left, CardTop, CardWidth, CardHeight);
            using (var paper = new SolidBrush(Color.FromArgb(243, 240, 232)))
            using (var border = new Pen(Color.FromArgb(200, 192, 176), 1))
            {
                graphics.FillRectangle(paper, card);
                graphics.DrawRectangle(border, card.X, card.Y, card.Width - 1, card.Height - 1);
            }

            var media = new Rectangle(left + 5, CardTop + 5, CardWidth - 10, 68);
            using (var background = new SolidBrush(Color.FromArgb(32, 39, 46)))
                graphics.FillRectangle(background, media);

            Bitmap bitmap = GetThumbnail(item);
            if (bitmap != null && item.IsAvailable)
            {
                Rectangle target = FitRectangle(bitmap.Size, media);
                graphics.DrawImage(bitmap, target);
            }
            else
            {
                using (var unavailable = new SolidBrush(Color.FromArgb(216, 101, 101)))
                using (var font = new Font("Segoe UI", 8f))
                    graphics.DrawString("离线", font, unavailable, media.X + 42, media.Y + 25);
            }

            DrawDeleteButton(graphics, left, CardTop);
            DrawPinButton(graphics, left, CardTop);
            string name = item.IsAvailable ? item.Name : "离线 · " + item.Name;
            using (var text = new SolidBrush(item.IsAvailable ? Color.FromArgb(90, 81, 71) : Color.FromArgb(180, 35, 24)))
            using (var font = new Font("Segoe UI", 7f))
                graphics.DrawString(TrimName(name), font, text, left + 5, CardTop + 76);
        }

        static string TrimName(string value)
        {
            if (value == null || value.Length <= 19) return value;
            return value.Substring(0, 18) + "...";
        }

        static Rectangle FitRectangle(Size source, Rectangle target)
        {
            if (source.Width <= 0 || source.Height <= 0) return target;
            double scale = Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(
                target.X + (target.Width - width) / 2,
                target.Y + (target.Height - height) / 2,
                width,
                height);
        }

        void DrawDeleteButton(Graphics graphics, int left, int top)
        {
            var rectangle = new Rectangle(left + 101, top + 7, 18, 18);
            using (var fill = new SolidBrush(Color.FromArgb(215, 107, 102)))
            using (var text = new SolidBrush(Color.White))
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold))
            {
                graphics.FillRectangle(fill, rectangle);
                graphics.DrawString("×", font, text, rectangle.X + 3, rectangle.Y - 1);
            }
        }

        void DrawPinButton(Graphics graphics, int left, int top)
        {
            var rectangle = new Rectangle(left + 92, top + 56, 22, 17);
            using (var fill = new SolidBrush(Color.FromArgb(220, 239, 232)))
            using (var text = new SolidBrush(Color.FromArgb(44, 107, 88)))
            using (var font = new Font("Segoe UI", 8f))
            {
                graphics.FillRectangle(fill, rectangle);
                graphics.DrawString("钉", font, text, rectangle.X + 5, rectangle.Y + 2);
            }
        }

        Bitmap GetThumbnail(ImageRecord item)
        {
            if (!item.IsAvailable || item.Thumbnail == null) return null;
            Bitmap cached;
            if (thumbnails.TryGetValue(item.Id, out cached)) return cached;

            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(item.Thumbnail));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    stream.Position = 0;
                    using (var source = new Bitmap(stream))
                        cached = new Bitmap(source);
                }
                thumbnails[item.Id] = cached;
                return cached;
            }
            catch { return null; }
        }

        ImageRecord HitItem(Point point, out bool delete, out bool pin)
        {
            delete = false;
            pin = false;
            for (int index = 0; index < items.Count && index < 50; index++)
            {
                int left = 4 + index * HangerStep;
                var card = new Rectangle(left, CardTop, CardWidth, CardHeight);
                if (!card.Contains(point)) continue;
                delete = new Rectangle(left + 101, CardTop + 7, 18, 18).Contains(point);
                pin = new Rectangle(left + 92, CardTop + 56, 22, 17).Contains(point);
                return items[index];
            }
            return null;
        }

        void OverlayMouseDown(object sender, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left || !expanded) return;
            bool delete;
            bool pin;
            pressedItem = HitItem(args.Location, out delete, out pin);
            pressPoint = args.Location;
            dragging = false;
            suppressClick = false;
        }

        void OverlayMouseMove(object sender, Forms.MouseEventArgs args)
        {
            if (pressedItem == null || args.Button != Forms.MouseButtons.Left || dragging) return;
            if (Math.Abs(args.X - pressPoint.X) < Forms.SystemInformation.DragSize.Width / 2 &&
                Math.Abs(args.Y - pressPoint.Y) < Forms.SystemInformation.DragSize.Height / 2)
                return;
            dragging = true;
            suppressClick = true;
            if (DragRequested != null) DragRequested(pressedItem);
            pressedItem = null;
        }

        void OverlayMouseUp(object sender, Forms.MouseEventArgs args)
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                pressedItem = null;
                dragging = false;
            }
            if (args.Button == Forms.MouseButtons.Right)
            {
                bool delete;
                bool pin;
                ImageRecord item = HitItem(args.Location, out delete, out pin);
                if (item != null) ShowItemMenu(item);
                else ShowShelfMenu();
            }
        }

        void OverlayMouseClick(object sender, Forms.MouseEventArgs args)
        {
            if (args.Button != Forms.MouseButtons.Left || !expanded) return;
            if (suppressClick)
            {
                suppressClick = false;
                return;
            }
            bool delete;
            bool pin;
            ImageRecord item = HitItem(args.Location, out delete, out pin);
            if (item != null)
            {
                if (delete && DeleteRequested != null) DeleteRequested(item);
                else if (pin && PinRequested != null) PinRequested(item);
                else if (CopyRequested != null) CopyRequested(item);
                return;
            }

            int handleLeft = (Width - 162) / 2;
            if (new Rectangle(handleLeft, 6, 162, 18).Contains(args.Location))
                ShowShelfMenu();
        }

        void ShowShelfMenu()
        {
            var menu = new Forms.ContextMenuStrip();
            AddMenuItem(menu, "截图", CaptureRequested);
            AddMenuItem(menu, "导入图片", ImportRequested);
            AddMenuItem(menu, "添加监听目录", AddFolderRequested);
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem(menu, "收起截图绳", CollapseRequested);
            menu.Show(this, PointToClient(Forms.Cursor.Position));
        }

        void ShowItemMenu(ImageRecord item)
        {
            var menu = new Forms.ContextMenuStrip();
            AddMenuItem(menu, "复制图片", delegate { if (CopyRequested != null) CopyRequested(item); });
            AddMenuItem(menu, "钉在桌面", delegate { if (PinRequested != null) PinRequested(item); });
            AddMenuItem(menu, "打开图片", delegate { if (OpenRequested != null) OpenRequested(item); });
            AddMenuItem(menu, "定位文件", delegate { if (LocateRequested != null) LocateRequested(item); });
            AddMenuItem(menu, "保存副本", delegate { if (SaveRequested != null) SaveRequested(item); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem(menu, "删除图片", delegate { if (DeleteRequested != null) DeleteRequested(item); });
            menu.Show(this, PointToClient(Forms.Cursor.Position));
        }

        static void AddMenuItem(Forms.ContextMenuStrip menu, string text, Action action)
        {
            var item = new Forms.ToolStripMenuItem(text);
            if (action != null) item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var bitmap in thumbnails.Values) bitmap.Dispose();
                thumbnails.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
