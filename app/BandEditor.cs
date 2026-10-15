using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AsusFanStudio;

// 一条可拖拽的温度轴：4 个把手 = 进安静 / 离安静 / 离全速 / 进全速，中间灰色是滞回死区
sealed class BandEditor
{
    readonly Canvas _canvas;
    readonly TextBlock _legend;
    readonly Func<int[]> _get;
    readonly Action<int>[] _set;
    readonly string[] _names;
    readonly Action _onChanged;

    Rectangle _marker;
    TextBlock _markerText;
    int _dragging = -1;
    int[] _drawn;


    public BandEditor(Canvas canvas, TextBlock legend, Func<int[]> get, Action<int>[] set, string[] names, Action onChanged)
    {
        _canvas = canvas;
        _legend = legend;
        _get = get;
        _set = set;
        _names = names;
        _onChanged = onChanged;

        canvas.SizeChanged += (s, e) => Rebuild();
        canvas.PointerMoved += OnMoved;
        canvas.PointerReleased += OnReleased;
        canvas.PointerCaptureLost += OnReleased;
    }

    double ToX(double t, double w) => (t - Config.T_MIN) / (Config.T_MAX - Config.T_MIN) * w;
    double ToT(double x, double w) => Config.T_MIN + x / Math.Max(1, w) * (Config.T_MAX - Config.T_MIN);

    static double LabelWidth(string t)
    {
        double px = 0;
        foreach (char c in t) px += c > 0x2E80 ? 12 : 6.6;   // 中日韩按全宽、数字符号按半宽估
        return px + 4;
    }

    static double ClampX(double x, double textW, double w) => Math.Clamp(x, 0, Math.Max(0, w - textW));

    public void Rebuild()
    {
        double w = _canvas.ActualWidth;
        if (w < 60) return;
        _canvas.Children.Clear();

        const double top = 44, h = 46;
        int[] v = _get();
        _drawn = (int[])v.Clone();

        Color turbo = Config.AllowTurbo ? Palette.Turbo : Palette.Neutral;
        Zone(Config.T_MIN, v[0], Palette.Quiet, "安静");
        Zone(v[1], v[2], Palette.Balanced, "高效");
        Zone(v[3], Config.T_MAX, turbo, Config.AllowTurbo ? "全速" : "全速(自动关闭)");

        // 滞回死区拆成两半，各自染成相邻档位的淡色，色条读起来是连续的
        Dead(v[0], v[1], Palette.Quiet, Palette.Balanced);
        Dead(v[2], v[3], Palette.Balanced, turbo);

        void Zone(double from, double to, Color c, string label)
        {
            double x1 = ToX(from, w), x2 = ToX(to, w);
            if (x2 - x1 < 1) return;
            var r = new Rectangle { Width = x2 - x1, Height = h, Fill = new SolidColorBrush(c), RadiusX = 10, RadiusY = 10 };
            Canvas.SetLeft(r, x1);
            Canvas.SetTop(r, top);
            _canvas.Children.Add(r);
            if (x2 - x1 > 56)
            {
                var t = new TextBlock { Text = label, FontSize = 13, Foreground = new SolidColorBrush(Palette.OnBlock) };
                Canvas.SetLeft(t, x1 + 12);
                Canvas.SetTop(t, top + 14);
                _canvas.Children.Add(t);
            }
        }

        void Dead(double from, double to, Color left, Color right)
        {
            double mid = (from + to) / 2.0;
            Half(from, mid, left); Half(mid, to, right);
            void Half(double a, double b, Color c)
            {
                double x1 = ToX(a, w), x2 = ToX(b, w);
                if (x2 - x1 < 1) return;
                var r = new Rectangle { Width = x2 - x1, Height = h - 18, Fill = new SolidColorBrush(Palette.Soft(c, 130)), RadiusX = 7, RadiusY = 7 };
                Canvas.SetLeft(r, x1);
                Canvas.SetTop(r, top + 9);
                _canvas.Children.Add(r);
            }
        }

        for (int t = 35; t <= 105; t += 10)
        {
            var tick = new Rectangle { Width = 1, Height = 6, Fill = new SolidColorBrush(Palette.Tick) };
            Canvas.SetLeft(tick, ToX(t, w));
            Canvas.SetTop(tick, top + h + 4);
            _canvas.Children.Add(tick);
            var lt = new TextBlock { Text = t + "°", FontSize = 11, Foreground = new SolidColorBrush(Palette.Tick) };
            Canvas.SetLeft(lt, ClampX(ToX(t, w) - 11, 22, w));
            Canvas.SetTop(lt, top + h + 12);
            _canvas.Children.Add(lt);
        }

        Color[] cols = { Palette.Quiet, Palette.Balanced, Palette.Balanced, turbo };
        double lastRight = 0;
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var hb = new Border
            {
                Width = 16,
                Height = h + 20,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(cols[i]),
                BorderBrush = new SolidColorBrush(Palette.OnBlock),
                BorderThickness = new Thickness(1.5),
                Tag = idx
            };
            Canvas.SetLeft(hb, ToX(v[i], w) - 8);
            Canvas.SetTop(hb, top - 10);
            hb.PointerPressed += OnPressed;
            _canvas.Children.Add(hb);

            // 标签排在同一条水平线上，平时只写温度；正在拖的那个才带上名字，避免四组文字错层打架
            string text = (_dragging == i ? _names[i] + " " : "") + v[i] + "°";
            double tw = LabelWidth(text);
            double lx = ClampX(ToX(v[i], w) - tw / 2, tw, w);
            if (lx < lastRight) lx = ClampX(lastRight, tw, w);
            lastRight = lx + tw + 16;
            var lab = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Palette.Label)
            };
            Canvas.SetLeft(lab, lx);
            Canvas.SetTop(lab, 4);
            _canvas.Children.Add(lab);
        }

        _marker = new Rectangle { Width = 3, Height = h + 14, Fill = new SolidColorBrush(Palette.Marker) };
        Canvas.SetTop(_marker, top - 6);
        Canvas.SetZIndex(_marker, 10);
        _canvas.Children.Add(_marker);
        _markerText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Palette.Marker)
        };
        Canvas.SetTop(_markerText, top + h + 30);
        Canvas.SetZIndex(_markerText, 11);
        _canvas.Children.Add(_markerText);

        Legend(v);
    }

    void Legend(int[] v)
    {
        _legend.Text = "≤ " + v[0] + "°C 进安静      " + v[0] + "–" + v[1] + "°C 保持当前      ≥ " + v[1] + "°C 进高效      "
            + v[2] + "–" + v[3] + "°C 保持当前      "
            + (Config.AllowTurbo ? "≥ " + v[3] + "°C 进全速，≤ " + v[2] + "°C 回落高效" : "全速不会自动进入（可在策略里开启）");
    }

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not int i) return;
        _dragging = i;
        _canvas.CapturePointer(e.Pointer);
        Rebuild();
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging < 0) return;
        double w = _canvas.ActualWidth;
        int temp = (int)Math.Round(ToT(e.GetCurrentPoint(_canvas).Position.X, w));
        int[] v = _get();
        int i = _dragging;
        // 相邻把手至少隔 1°C，否则"保持当前"死区宽度为 0，档位会在阈值上反复横跳
        int lo = i == 0 ? Config.T_MIN : v[i - 1] + 1;
        int hi = i == 3 ? Config.T_MAX : v[i + 1] - 1;
        if (lo > hi) return;
        _set[i](Math.Clamp(temp, lo, hi));
        int[] now = _get();
        if (_drawn is not null && now.AsEnumerable().SequenceEqual(_drawn)) return;   // 指针抖动 0.x°C 时不重建
        Rebuild();
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        try { _canvas.ReleasePointerCapture(e.Pointer); } catch { }
        _onChanged();
        Rebuild();
    }

    public void SetMarker(double? temp, string suffix)
    {
        if (_marker is null) return;
        double w = _canvas.ActualWidth;
        if (!temp.HasValue)
        {
            _marker.Visibility = Visibility.Collapsed;
            _markerText.Visibility = Visibility.Collapsed;
            return;
        }
        _marker.Visibility = Visibility.Visible;
        _markerText.Visibility = Visibility.Visible;
        double x = ToX(Math.Clamp(temp.Value, Config.T_MIN, Config.T_MAX), w);
        Canvas.SetLeft(_marker, x - 1);
        _markerText.Text = "当前 " + suffix + " " + ((int)Math.Round(temp.Value)) + "°C";
        Canvas.SetLeft(_markerText, ClampX(x - 60, LabelWidth(_markerText.Text), w));
    }
}
