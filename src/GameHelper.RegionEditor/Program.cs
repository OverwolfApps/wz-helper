using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GameHelper.Core.Config;
using WarzoneHelper.Game;

namespace GameHelper.RegionEditor
{
    // Transparent, topmost, fullscreen overlay that draws each config region as a
    // draggable/resizable box directly over whatever is behind it (a fullscreened
    // screenshot or the live game). Reads and writes the same settings.jsonc as the
    // agent, using the identical anchor math the analyzer uses to crop regions.
    //
    // Coordinates are relative to the game *client* area; for a borderless/fullscreen
    // game or a fullscreen screenshot, that is the whole screen — which is exactly the
    // overlay's canvas, so what you see is what the analyzer crops.
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new EditorWindow());
        }
    }

    internal sealed class EditorWindow : Window
    {
        // Palette cycled across regions so neighbouring boxes stay distinguishable.
        static readonly Color[] Palette =
        {
            Color.FromRgb(0x48,0xd5,0x97), Color.FromRgb(0xff,0xcf,0x5a), Color.FromRgb(0x5a,0xb0,0xff),
            Color.FromRgb(0xff,0x5a,0x6a), Color.FromRgb(0xc0,0x8a,0xff), Color.FromRgb(0x5a,0xff,0xe0),
            Color.FromRgb(0xff,0x9a,0x3a), Color.FromRgb(0x9a,0xd0,0x4a), Color.FromRgb(0xff,0x7a,0xc0),
        };

        readonly string _configPath;
        readonly WarzoneConfig _cfg;
        readonly Canvas _canvas = new Canvas();
        readonly Image _backdrop = new Image { Stretch = Stretch.Fill, Opacity = 1.0 };
        readonly List<Box> _boxes = new List<Box>();
        readonly TextBlock _status = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        Box _selected;

        public EditorWindow()
        {
            _configPath = HelperConfig.DefaultConfigPath("warzone");
            _cfg = (WarzoneConfig)HelperConfig.LoadOrCreate(_configPath, () => new WarzoneConfig());

            Title = "Game Helper — Region Editor";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Topmost = true;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)); // faint tint so you can see the overlay is live

            var root = new Grid();
            root.Children.Add(_backdrop);          // optional loaded screenshot, behind everything
            root.Children.Add(_canvas);            // region boxes
            root.Children.Add(BuildToolbar());     // top strip, drawn last = on top
            Content = root;

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => Rebuild();
        }

        UIElement BuildToolbar()
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1b, 0x1f, 0x2a)),
                Margin = new Thickness(0),
            };
            bar.Children.Add(new TextBlock
            {
                Text = "  🎯 Region Editor   ", Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            });
            bar.Children.Add(MakeButton("Open image", (s, e) => OpenImage()));
            bar.Children.Add(MakeButton("Reload", (s, e) => { _cfg_ReloadInto(); Rebuild(); }));
            bar.Children.Add(MakeButton("Save", (s, e) => Save()));

            bar.Children.Add(new TextBlock { Text = "  BG ", Foreground = Brushes.Silver, VerticalAlignment = VerticalAlignment.Center });
            var op = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            op.ValueChanged += (s, e) => _backdrop.Opacity = op.Value;
            bar.Children.Add(op);

            bar.Children.Add(new TextBlock
            {
                Text = "   drag = move · corner = resize · Esc = quit    ",
                Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center,
            });
            bar.Children.Add(_status);
            SetStatus("loaded " + _configPath);

            // Keep the toolbar itself from stealing region interactions below it.
            var host = new Border { Child = bar, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            return host;
        }

        static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(4, 4, 0, 4), Padding = new Thickness(8, 3, 8, 3),
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x2a, 0x38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x30, 0x40)),
            };
            b.Click += onClick;
            return b;
        }

        void SetStatus(string s) => _status.Text = "   " + s + "   ";

        // Reflection over ScreenRegions so every Region property is editable automatically.
        static IEnumerable<PropertyInfo> RegionProps() =>
            typeof(ScreenRegions).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        void _cfg_ReloadInto()
        {
            var fresh = (WarzoneConfig)HelperConfig.LoadOrCreate(_configPath, () => new WarzoneConfig());
            foreach (var p in RegionProps())
                p.SetValue(_cfg.Regions, p.GetValue(fresh.Regions));
        }

        void Rebuild()
        {
            _canvas.Children.Clear();
            _boxes.Clear();
            int i = 0;
            foreach (var p in RegionProps())
            {
                var region = (Region)p.GetValue(_cfg.Regions);
                if (region == null) continue;
                var box = new Box(this, p.Name, region, Palette[i % Palette.Length]);
                _boxes.Add(box);
                _canvas.Children.Add(box.Root);
                i++;
            }
            LayoutBoxes();
        }

        internal double CW => _canvas.ActualWidth;
        internal double CH => _canvas.ActualHeight;

        internal void LayoutBoxes()
        {
            foreach (var b in _boxes) b.Layout(CW, CH);
        }

        internal void Select(Box box)
        {
            _selected = box;
            foreach (var b in _boxes) b.SetSelected(b == box);
            SetStatus(box.Name + $"  anchor={box.Region.Anchor}  X={box.Region.X:0.###} Y={box.Region.Y:0.###} W={box.Region.W:0.###} H={box.Region.H:0.###}");
        }

        void OpenImage()
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dlg.ShowDialog() == true)
            {
                try { _backdrop.Source = new BitmapImage(new Uri(dlg.FileName)); SetStatus("image: " + dlg.FileName); }
                catch (Exception ex) { SetStatus("image error: " + ex.Message); }
            }
        }

        void Save()
        {
            try { _cfg.SaveJsonc(_configPath); SetStatus("saved → " + _configPath); }
            catch (Exception ex) { SetStatus("save error: " + ex.Message); }
        }
    }

    // One editable region: a colored border with a label and a corner resize grip.
    internal sealed class Box
    {
        readonly EditorWindow _owner;
        readonly Color _color;
        readonly Border _border;
        readonly TextBlock _label;
        readonly Thumb _move;
        readonly Thumb _resize;

        public string Name { get; }
        public Region Region { get; }
        public Grid Root { get; }

        public Box(EditorWindow owner, string name, Region region, Color color)
        {
            _owner = owner; Name = name; Region = region; _color = color;

            Root = new Grid();

            _border = new Border
            {
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(color),
                Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
                IsHitTestVisible = false,
            };
            Root.Children.Add(_border);

            _label = new TextBlock
            {
                Text = name, Foreground = new SolidColorBrush(color), FontSize = 11,
                Margin = new Thickness(2, -15, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
            };
            Root.Children.Add(_label);

            // Full-area move thumb (transparent).
            _move = new Thumb
            {
                Opacity = 0, Cursor = Cursors.SizeAll,
                Template = TransparentThumbTemplate(),
            };
            _move.DragDelta += OnMove;
            _move.DragStarted += (s, e) => _owner.Select(this);
            Root.Children.Add(_move);

            // Corner resize grip.
            _resize = new Thumb
            {
                Width = 12, Height = 12, Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Template = GripThumbTemplate(color),
            };
            _resize.DragDelta += OnResize;
            _resize.DragStarted += (s, e) => _owner.Select(this);
            Root.Children.Add(_resize);
        }

        public void SetSelected(bool sel)
        {
            _border.BorderThickness = new Thickness(sel ? 2.5 : 1.5);
            _border.Background = new SolidColorBrush(Color.FromArgb((byte)(sel ? 0x3a : 0x22), _color.R, _color.G, _color.B));
        }

        public void Layout(double cw, double ch)
        {
            if (cw <= 0 || ch <= 0) return;
            var r = ToRect(Region, cw, ch);
            Canvas.SetLeft(Root, r.X); Canvas.SetTop(Root, r.Y);
            Root.Width = Math.Max(6, r.Width); Root.Height = Math.Max(6, r.Height);
        }

        void OnMove(object sender, DragDeltaEventArgs e)
        {
            Canvas.SetLeft(Root, Canvas.GetLeft(Root) + e.HorizontalChange);
            Canvas.SetTop(Root, Canvas.GetTop(Root) + e.VerticalChange);
            Commit();
        }

        void OnResize(object sender, DragDeltaEventArgs e)
        {
            Root.Width = Math.Max(6, Root.Width + e.HorizontalChange);
            Root.Height = Math.Max(6, Root.Height + e.VerticalChange);
            Commit();
        }

        // Push the pixel rect back into the region's normalized X/Y/W/H for its anchor.
        void Commit()
        {
            double cw = _owner.CW, ch = _owner.CH;
            if (cw <= 0 || ch <= 0) return;
            FromRect(Region, Canvas.GetLeft(Root), Canvas.GetTop(Root), Root.Width, Root.Height, cw, ch);
            _owner.Select(this);
        }

        // === identical anchor math to the analyzer / browser tool ===
        static Rect ToRect(Region r, double cw, double ch)
        {
            double w = r.W * cw, h = r.H * ch;
            string a = (r.Anchor ?? "topleft").ToLowerInvariant();
            double x = a.Contains("right") ? cw - r.X * cw - w : a.Contains("left") ? r.X * cw : (cw - w) / 2 + r.X * cw;
            double y = a.Contains("bottom") ? ch - r.Y * ch - h : a.Contains("top") ? r.Y * ch : (ch - h) / 2 + r.Y * ch;
            return new Rect(x, y, w, h);
        }

        static void FromRect(Region r, double px, double py, double pw, double ph, double cw, double ch)
        {
            string a = (r.Anchor ?? "topleft").ToLowerInvariant();
            r.W = Round(pw / cw); r.H = Round(ph / ch);
            r.X = Round(a.Contains("right") ? (cw - px - pw) / cw : a.Contains("left") ? px / cw : (px - (cw - pw) / 2) / cw);
            r.Y = Round(a.Contains("bottom") ? (ch - py - ph) / ch : a.Contains("top") ? py / ch : (py - (ch - ph) / 2) / ch);
        }

        static double Round(double v) => Math.Round(v, 4);

        static ControlTemplate TransparentThumbTemplate()
        {
            var f = new FrameworkElementFactory(typeof(Border));
            f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            return new ControlTemplate(typeof(Thumb)) { VisualTree = f };
        }

        static ControlTemplate GripThumbTemplate(Color color)
        {
            var f = new FrameworkElementFactory(typeof(Border));
            f.SetValue(Border.BackgroundProperty, new SolidColorBrush(color));
            f.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = f };
        }
    }
}
