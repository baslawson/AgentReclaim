using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AgentReclaim.Core;
using AgentReclaim.Core.Analysis;

namespace AgentReclaim.App;

/// <summary>
/// Tray icon: the mascot's face inside a memory ring, with an orange dot when there are leftovers to clean. It gives a
/// little wiggle when memory was freed. The ring's colour uses the same limits as the window's gauge.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<(int Bucket, MeterState Meter, bool Leftovers, int Tilt), Icon> _icons = new();
    private (int Bucket, MeterState Meter, bool Leftovers)? _shown;
    private readonly System.Windows.Forms.Timer _wiggle = new() { Interval = 110 };
    private int _wiggleFrame = -1;
    private static readonly int[] WiggleTilts = [-14, 14, -10, 10, -5, 5, 0];
    private readonly ToolStripMenuItem _freeSafe;
    private readonly ToolStripMenuItem _pause;
    private readonly Action _open;
    private Action? _onBalloonClick;

    public TrayIcon(Action open, Action<bool> setPaused, Action freeSafeLeftovers, Action checkForUpdates, Action exit,
        Action runHealthChecks)
    {
        _open = open;
        var pause = _pause = new ToolStripMenuItem("Pause monitoring") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            setPaused(pause.Checked);
            _freeSafe!.Enabled = false; // re-enabled by the next poll once monitoring runs again
        };
        _freeSafe = new ToolStripMenuItem("Free safe leftovers…") { Enabled = false };
        _freeSafe.Click += (_, _) => freeSafeLeftovers();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AgentReclaim", null, (_, _) => open());
        menu.Items.Add(_freeSafe);
        menu.Items.Add(pause);
        menu.Items.Add("Run agent health checks", null, (_, _) => runHealthChecks());
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add("Check for updates", null, (_, _) => checkForUpdates());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = "AgentReclaim: starting…",
            ContextMenuStrip = menu,
            Icon = GetIcon(0, MeterState.Good, false, 0),
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => (_onBalloonClick ?? _open)();
        _wiggle.Tick += (_, _) => NextWiggleFrame();
    }

    public void Update(AnalysisResult result, IReadOnlyList<OrphanTree> safeLeftovers)
    {
        _freeSafe.Enabled = safeLeftovers.Count > 0 && !_pause.Checked;
        _freeSafe.Text = safeLeftovers.Count > 0
            ? $"Free safe leftovers ({safeLeftovers.Count}, {Format.Bytes(safeLeftovers.Sum(t => t.Root.TotalPrivateBytes))})…"
            : "Free safe leftovers (none right now)";

        var system = result.Snapshot.System;
        var state = ((int)Math.Round(system.MemoryLoad * 10), Personality.Meter(system.MemoryLoad), result.Orphans.Count > 0);
        if (_shown != state)
        {
            _shown = state;
            if (_wiggleFrame < 0) _icon.Icon = GetIcon(state.Item1, state.Item2, state.Item3, 0); // otherwise the wiggle ends on it
        }

        var text = $"AgentReclaim\nRAM {Format.Bytes(system.UsedMemoryBytes)} / {Format.Bytes(system.TotalMemoryBytes)}" +
                   $"\nAI tools {Format.Bytes(result.AgentPrivateBytes)}";
        if (result.Orphans.Count > 0)
            text += $"\n{result.Orphans.Count} leftover(s), {Format.Bytes(result.OrphanPrivateBytes)}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>A short wiggle, about a second, then back to the normal icon. Respects Windows' animation setting.</summary>
    public void Wiggle()
    {
        if (!Motion.Enabled || _wiggleFrame >= 0) return;
        _wiggleFrame = 0;
        _wiggle.Start();
    }

    private void NextWiggleFrame()
    {
        var (bucket, meter, leftovers) = _shown ?? (0, MeterState.Good, false);
        if (_wiggleFrame >= WiggleTilts.Length)
        {
            _wiggle.Stop();
            _wiggleFrame = -1;
            _icon.Icon = GetIcon(bucket, meter, leftovers, 0);
            return;
        }
        _icon.Icon = GetIcon(bucket, meter, leftovers, WiggleTilts[_wiggleFrame++]);
    }

    /// <summary>A Windows notification; clicking it opens the window (or does <paramref name="onClick"/>).</summary>
    public void ShowMessage(string title, string text, bool isError, Action? onClick = null)
    {
        _onBalloonClick = onClick;
        _icon.ShowBalloonTip(4000, title, text.Length > 255 ? text[..254] + "…" : text, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private Icon GetIcon(int bucket, MeterState meter, bool leftovers, int tilt)
    {
        var key = (bucket, meter, leftovers, tilt);
        if (_icons.TryGetValue(key, out var cached)) return cached;

        var gauge = meter switch
        {
            MeterState.Danger => Color.FromArgb(0xE5, 0x4B, 0x3F),
            MeterState.Warn => Color.FromArgb(0xE8, 0xA8, 0x2A),
            _ => Color.FromArgb(0x3F, 0xB8, 0x7A),
        };

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            g.TranslateTransform(16, 16);
            g.RotateTransform(tilt);
            g.TranslateTransform(-16, -16);

            using var track = new Pen(Color.FromArgb(0x80, 0x80, 0x80), 4);
            using var fill = new Pen(gauge, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(track, 2.5f, 2.5f, 27, 27);
            g.DrawArc(fill, 2.5f, 2.5f, 27, 27, -90, Math.Max(1, bucket) * 36);

            // The mascot's face: a dark visor disc with two eyes, readable on light and dark taskbars.
            using var visor = new SolidBrush(Color.FromArgb(0x14, 0x1B, 0x24));
            using var eye = new SolidBrush(Color.White);
            g.FillEllipse(visor, 7, 7, 18, 18);
            g.FillEllipse(eye, 10.5f, 12, 4.5f, 5.5f);
            g.FillEllipse(eye, 17, 12, 4.5f, 5.5f);

            g.ResetTransform();
            if (leftovers)
            {
                using var dot = new SolidBrush(Color.FromArgb(0xF0, 0x95, 0x4E));
                using var outline = new Pen(Color.White, 2);
                g.FillEllipse(dot, 18, 0, 13, 13);
                g.DrawEllipse(outline, 18, 0, 13, 13);
            }
        }

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        _icons[key] = icon;
        return icon;
    }

    public void Dispose()
    {
        _wiggle.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
