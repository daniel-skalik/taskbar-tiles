using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Tests;

public class TaskbarTrackerTests
{
    // A window of another process (the tracker ignores its own) that opens on the given monitor, takes the foreground,
    // then goes full screen in place: no foreground change, so only Explorer's ABN_FULLSCREENAPP tells the tracker.
    // With a second device it then moves the focus to a window there, as when you work beside a presentation.
    const string FullScreenLater = """
        param([string]$device, [string]$other)
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $device } | Select-Object -First 1
        $form = New-Object System.Windows.Forms.Form
        $form.StartPosition = 'Manual'; $form.BackColor = 'Black'; $form.ShowInTaskbar = $false
        $form.Bounds = New-Object System.Drawing.Rectangle ($screen.Bounds.X + 100), ($screen.Bounds.Y + 100), 300, 200
        $timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 1500
        $timer.add_Tick({ $timer.Stop(); $form.FormBorderStyle = 'None'; $form.Bounds = $screen.Bounds })
        $form.add_Shown({ $form.Activate(); $timer.Start(); if ($other) { $away.Start() } })
        $away = New-Object System.Windows.Forms.Timer; $away.Interval = 3500
        $away.add_Tick({
            $away.Stop()
            $there = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $other } | Select-Object -First 1
            $side = New-Object System.Windows.Forms.Form
            $side.StartPosition = 'Manual'; $side.ShowInTaskbar = $false; $side.TopMost = $false
            $side.Bounds = New-Object System.Drawing.Rectangle ($there.Bounds.X + 100), ($there.Bounds.Y + 100), 300, 200
            $side.Show(); $side.Activate()
        })
        $close = New-Object System.Windows.Forms.Timer; $close.Interval = 10000; $close.add_Tick({ $form.Close() }); $close.Start()
        [System.Windows.Forms.Application]::Run($form)
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public void Tiles_hide_when_a_window_goes_full_screen_in_place_on_any_monitor()
    {
        using var dir = new TempDir();
        var script = dir.File("fullscreen.ps1");
        System.IO.File.WriteAllText(script, FullScreenLater);

        Sta.Run(() =>
        {
            using var tracker = new TaskbarTracker(Dispatcher.CurrentDispatcher);
            tracker.Start();
            PumpUntil(() => tracker.Current.Count > 0, TimeSpan.FromSeconds(5));
            Assert.NotEmpty(tracker.Current);

            var bars = tracker.Current.ToList();
            var devices = bars.Select(b =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                Assert.True(GetMonitorInfo(MonitorFromWindow(b.Handle, MONITOR_DEFAULTTONEAREST), ref info));
                return info.szDevice;
            }).ToList();

            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var device = devices[i];
                var other = devices.Count > 1 ? devices[(i + 1) % devices.Count] : "";
                bool HiddenNow() => tracker.Current.Single(t => t.DisplayId == bar.DisplayId).Hidden;

                using var child = Process.Start(new ProcessStartInfo("powershell.exe",
                    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, device, other]) { CreateNoWindow = true })!;
                try
                {
                    PumpUntil(() => false, TimeSpan.FromSeconds(1)); // windowed: the bar shows
                    Assert.False(HiddenNow(), $"{device}: hidden while the window was not full screen");
                    PumpUntil(HiddenNow, TimeSpan.FromSeconds(6));
                    Assert.True(HiddenNow(), $"{device}: still shown after a window went full screen on it");
                    if (other != "")
                    {
                        PumpUntil(() => false, TimeSpan.FromSeconds(3)); // the focus moves to the other monitor
                        Assert.True(HiddenNow(), $"{device}: shown again after the focus moved to {other}");
                    }
                }
                finally
                {
                    if (!child.HasExited) child.Kill();
                    child.WaitForExit(5000);
                }
                PumpUntil(() => !HiddenNow(), TimeSpan.FromSeconds(5));
            }
        });
    }

    static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }
}
