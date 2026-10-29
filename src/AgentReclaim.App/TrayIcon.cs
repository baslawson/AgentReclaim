using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AgentReclaim.Core;
using AgentReclaim.Core.Analysis;

namespace AgentReclaim.App;

/// <summary>Tray icon drawn as a memory gauge, with an orange dot when there are leftovers to clean.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<(int Bucket, bool Leftovers), Icon> _icons = new();
    private (int Bucket, bool Leftovers)? _shown;
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
            Icon = GetIcon(0, false),
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => (_onBalloonClick ?? _open)();
    }

    public void Update(AnalysisResult result, IReadOnlyList<OrphanTree> safeLeftovers)
    {
        _freeSafe.Enabled = safeLeftovers.Count > 0 && !_pause.Checked;
        _freeSafe.Text = safeLeftovers.Count > 0
            ? $"Free safe leftovers ({safeLeftovers.Count}, {Format.Bytes(safeLeftovers.Sum(t => t.Root.TotalPrivateBytes))})…"
            : "Free safe leftovers (none right now)";

        var system = result.Snapshot.System;
        var state = ((int)Math.Round(system.MemoryLoad * 10), result.Orphans.Count > 0);
        if (_shown != state)
        {
            _icon.Icon = GetIcon(state.Item1, state.Item2);
            _shown = state;
        }

        var text = $"AgentReclaim\nRAM {Format.Bytes(system.UsedMemoryBytes)} / {Format.Bytes(system.TotalMemoryBytes)}" +
                   $"\nAI tools {Format.Bytes(result.AgentPrivateBytes)}";
        if (result.Orphans.Count > 0)
            text += $"\n{result.Orphans.Count} leftover(s), {Format.Bytes(result.OrphanPrivateBytes)}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
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

    private Icon GetIcon(int bucket, bool leftovers)
    {
        if (_icons.TryGetValue((bucket, leftovers), out var cached)) return cached;

        var gauge = bucket >= 9 ? Color.FromArgb(0xC6, 0x3B, 0x2F)
            : bucket >= 7 ? Color.FromArgb(0xD9, 0x9A, 0x1E)
            : Color.FromArgb(0x3C, 0x8D, 0x5A);

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var track = new SolidBrush(Color.FromArgb(0x55, 0x55, 0x55));
            using var fill = new SolidBrush(gauge);
            g.FillEllipse(track, 1, 1, 30, 30);
            g.FillPie(fill, 1, 1, 30, 30, -90, Math.Max(1, bucket) * 36);
            using var hole = new SolidBrush(Color.FromArgb(0x22, 0x22, 0x22));
            g.FillEllipse(hole, 9, 9, 14, 14);

            if (leftovers)
            {
                using var dot = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x2B));
                using var outline = new Pen(Color.White, 2);
                g.FillEllipse(dot, 18, 0, 13, 13);
                g.DrawEllipse(outline, 18, 0, 13, 13);
            }
        }

        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        _icons[(bucket, leftovers)] = icon;
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
