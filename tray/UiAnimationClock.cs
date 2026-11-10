using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
namespace ZeroIDE.Mcp;
// Scoped timer precision only while a visible surface is actually animating.
// Never change the system timer configuration permanently; every successful request is balanced.
internal sealed class UiAnimationClock : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _previous;
    private bool _precision;
    public double Seconds => _watch.Elapsed.TotalSeconds;
    public double Step() { double now = Seconds, dt = now - _previous; _previous = now; return Math.Max(.001, dt); }
    public void SetActive(bool active)
    {
        if (active == _precision) return;
        if (active) { try { _precision = timeBeginPeriod(1) == 0; } catch { } }
        else { try { timeEndPeriod(1); } catch { } _precision = false; }
    }
    public void Dispose() => SetActive(false);
    public static double Follow(double current, double target, double dt, double response = .09) => target + (current - target) * Math.Exp(-Math.Max(0, dt) / response);
    public static void DrawMarquee(Graphics g, RectangleF track, Color color, double seconds)
    {
        Theme.FillRound(g, track, track.Height / 2, Theme.WithAlpha(color, 30));
        var state = g.Save(); using var clip = Theme.RoundRect(track, track.Height / 2); g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
        float width = Math.Max(26, track.Width * .27f);
        float t = (float)((seconds % 1.45) / 1.45);
        float x = track.X - width + t * (track.Width + width);
        Theme.FillRoundGradient(g, new(x, track.Y, width, track.Height), track.Height / 2, Theme.WithAlpha(color, 110), color);
        g.Restore(state);
    }
}

// WM_TIMER rounds intervals and rearms around expensive paint work. Post frames
// on a steady background cadence, with at most ONE UI callback waiting at a time.
internal sealed class UiFrameTimer : IDisposable
{
    private readonly System.Windows.Forms.Control _owner;
    private System.Threading.Timer? _timer;
    private int _pending;
    private volatile bool _enabled, _disposed;
    public int Interval { get; set; } = 16;
    public bool Enabled => _enabled;
    public event EventHandler? Tick;
    public UiFrameTimer(System.Windows.Forms.Control owner) => _owner = owner;
    public void Start()
    {
        if (_disposed || _enabled) return;
        _enabled = true;
        _timer = new System.Threading.Timer(_ => PostFrame(), null, 0, Interval);
    }
    private void PostFrame()
    {
        if (!_enabled || _disposed || _owner.IsDisposed || !_owner.IsHandleCreated) return;
        if (System.Threading.Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        try {
            _owner.BeginInvoke(new Action(() => {
                try { if (_enabled && !_disposed && !_owner.IsDisposed) Tick?.Invoke(this, EventArgs.Empty); }
                finally { System.Threading.Volatile.Write(ref _pending, 0); }
            }));
        } catch (InvalidOperationException) { System.Threading.Volatile.Write(ref _pending, 0); }
    }
    public void Stop() { _enabled = false; _timer?.Dispose(); _timer = null; }
    public void Dispose() { _disposed = true; Stop(); }
}
