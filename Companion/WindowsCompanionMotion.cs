#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FederationCompanion;

/// <summary>Short, finite native transitions. No renderer or idle animation loop.</summary>
internal sealed class WindowsCompanionMotion : IDisposable
{
    private readonly Dictionary<Control, (System.Windows.Forms.Timer Timer, TaskCompletionSource Completion)> _active = new();
    private bool _disposed;

    internal static bool Enabled => !SystemInformation.HighContrast
        && SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled;

    // SPI_GETCLIENTAREAANIMATION: honor Windows Accessibility → Animation effects.
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    internal Task Animate(Control owner, Action<double> frame, int milliseconds = 180)
    {
        Cancel(owner);
        if (_disposed || owner.IsDisposed) return Task.CompletedTask;
        if (!Enabled) { frame(1); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        var clock = Stopwatch.StartNew();
        _active.Add(owner, (timer, completion));
        timer.Tick += (_, _) =>
        {
            if (owner.IsDisposed) { Cancel(owner); return; }
            var progress = Enabled ? Math.Clamp(clock.Elapsed.TotalMilliseconds / milliseconds, 0, 1) : 1;
            frame(1 - Math.Pow(1 - progress, 3));
            if (progress >= 1) Cancel(owner);
        };
        frame(0); timer.Start(); return completion.Task;
    }
    private void Cancel(Control owner)
    {
        if (!_active.Remove(owner, out var animation)) return;
        animation.Timer.Stop(); animation.Timer.Dispose(); animation.Completion.TrySetResult();
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var owner in _active.Keys.ToArray()) Cancel(owner);
    }
}

internal sealed class WindowsCompanionSurface : FlowLayoutPanel
{
    internal WindowsCompanionSurface() => DoubleBuffered = true;
}
#endif
