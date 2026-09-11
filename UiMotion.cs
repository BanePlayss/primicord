using System.Diagnostics;

namespace Primicord;

/// <summary>One UI clock for finite transitions; no timer runs when nothing moves.</summary>
internal static class UiMotion
{
    private static readonly HashSet<MotionValue> Active = new();
    private static readonly System.Windows.Forms.Timer Clock = new() { Interval = 16 };
    public static bool Reduced { get; set; }
    public static bool Enabled => !Reduced && SystemInformation.IsMenuAnimationEnabled;
    static UiMotion()
    {
        Clock.Tick += (_, _) =>
        {
            foreach (var motion in Active.ToArray())
                if (!motion.Tick()) Active.Remove(motion);
            if (Active.Count == 0) Clock.Stop();
        };
    }
    public static void Schedule(MotionValue motion) { Active.Add(motion); Clock.Start(); }
    public static void Remove(MotionValue motion) { Active.Remove(motion); if (Active.Count == 0) Clock.Stop(); }
    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((int)(a.A + (b.A-a.A)*t), (int)(a.R+(b.R-a.R)*t),
            (int)(a.G+(b.G-a.G)*t), (int)(a.B+(b.B-a.B)*t));
    }
}

internal sealed class MotionValue
{
    private readonly Control _owner;
    private double _from, _target, _duration, _delay;
    private long _start;
    private bool _spring;
    public double Value { get; private set; }
    public MotionValue(Control owner, double initial = 0)
    {
        _owner = owner; Value = initial;
        owner.Disposed += (_, _) => UiMotion.Remove(this);
    }
    public void Snap(double value) { UiMotion.Remove(this); Value = _target = value; }
    public void To(double target, int duration = 180, int delay = 0, bool spring = false)
    {
        if (!UiMotion.Enabled) { Snap(target); _owner.Invalidate(); return; }
        _from = Value; _target = target; _duration = Math.Max(1, duration); _delay = delay;
        _spring = spring; _start = Stopwatch.GetTimestamp(); UiMotion.Schedule(this);
    }
    public bool Tick()
    {
        if (_owner.IsDisposed) return false;
        double t = Math.Clamp((Stopwatch.GetElapsedTime(_start).TotalMilliseconds - _delay) / _duration, 0, 1);
        if (!UiMotion.Enabled || !_owner.Visible) t = 1;
        double eased = t >= 1 ? 1 : _spring ? 1 - Math.Exp(-7*t)*Math.Cos(10*t) : 1 - Math.Pow(1-t, 3);
        Value = _from + (_target-_from)*eased;
        _owner.Invalidate();
        return t < 1;
    }
}
