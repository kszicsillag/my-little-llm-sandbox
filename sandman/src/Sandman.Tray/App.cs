using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Sandman.Core;

namespace Sandman.Tray;

/// <summary>
/// Tray-only app: the icon shows running sandboxes (amber + count) and open egress (red);
/// the menu lists sbx sandboxes with Stop / Resume / Allow all egress / Lock down.
///
/// Allow-all deadlines live in the sandbox's own egress policy (marker rule, same as sbx.sh), so this
/// enforces windows opened by sbx, sbx.sh or another machine, and survives sleep / reboot: on every
/// refresh, any expired window is locked down.
/// </summary>
public sealed class App : Application
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly (string Label, TimeSpan Ttl)[] AllowAllChoices =
        [("15 minutes", TimeSpan.FromMinutes(15)), ("30 minutes", TimeSpan.FromMinutes(30)),
         ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2))];

    private TrayIcon _tray = null!;
    private SandmanClient? _client;
    private IReadOnlyList<SbxSandbox> _sandboxes = [];
    private string? _error;
    private readonly HashSet<string> _busy = [];   // sandbox names with an operation in flight (UI thread only)
    private readonly DispatcherTimer _deadlineTimer = new();
    private int _refreshing;                       // 0/1, guards overlapping refreshes
    private (int Running, bool Open)? _lastIcon;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        _tray = new TrayIcon { Icon = TrayIconArt.Render(0, false), ToolTipText = "Sandman", IsVisible = true };
        TrayIcon.SetIcons(this, [_tray]);
        RebuildMenu();

        var poll = new DispatcherTimer { Interval = PollInterval };
        poll.Tick += (_, _) => _ = RefreshAsync();
        poll.Start();

        _deadlineTimer.Tick += (_, _) => { _deadlineTimer.Stop(); _ = RefreshAsync(); };

        _ = RefreshAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            _client ??= new SandmanClient(AcaConfig.Load());
            var list = await Task.Run(() => _client.ListAsync());
            _sandboxes = list;
            _error = null;
        }
        catch (Exception e) { _error = Friendly(e); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }

        Dispatcher.UIThread.Post(() =>
        {
            EnforceDeadlines();
            ScheduleNextDeadline();
            UpdateUi();
        });
    }

    /// <summary>Lock down every sandbox whose allow-all window has expired (UI thread).</summary>
    private void EnforceDeadlines()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var sb in _sandboxes)
            if (sb.Egress.IsOpen && sb.Egress.Window?.IsExpired(now) == true)
                RunOp(sb, "auto-lockdown", (_, ct) => SandmanClient.LockdownAsync(sb, ct: ct));
    }

    /// <summary>Wake up right at the next deadline instead of up to PollInterval late.</summary>
    private void ScheduleNextDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var next = _sandboxes
            .Select(s => s.Egress.IsOpen ? s.Egress.Window?.Deadline : null)
            .Where(d => d > now)
            .Min();
        _deadlineTimer.Stop();
        if (next is { } d && d - now < PollInterval)
        {
            _deadlineTimer.Interval = d - now + TimeSpan.FromSeconds(1);
            _deadlineTimer.Start();
        }
    }

    private void UpdateUi()
    {
        var running = _sandboxes.Count(s => s.IsRunning);
        var open = _sandboxes.Where(s => s.Egress.IsOpen).ToList();
        if (_lastIcon != (running, open.Count > 0))
        {
            _tray.Icon = TrayIconArt.Render(running, open.Count > 0);
            _lastIcon = (running, open.Count > 0);
        }

        _tray.ToolTipText = _error is not null
            ? $"Sandman: {Truncate(_error, 100)}"
            : $"Sandman: {running} running" + (open.Count > 0 ? $", egress OPEN: {string.Join(", ", open.Select(s => s.Name))}" : "");
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var menu = new NativeMenu();

        if (_error is not null)
            menu.Add(new NativeMenuItem($"⚠ {Truncate(_error, 70)}") { IsEnabled = false });
        else if (_sandboxes.Count == 0)
            menu.Add(new NativeMenuItem("No sbx sandboxes") { IsEnabled = false });

        foreach (var sb in _sandboxes)
            menu.Add(SandboxItem(sb));

        menu.Add(new NativeMenuItemSeparator());
        var refresh = new NativeMenuItem("Refresh");
        refresh.Click += (_, _) => _ = RefreshAsync();
        menu.Add(refresh);
        var quit = new NativeMenuItem("Quit Sandman");
        quit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Add(quit);

        _tray.Menu = menu;
    }

    private NativeMenuItem SandboxItem(SbxSandbox sb)
    {
        var busy = _busy.Contains(sb.Name);
        var e = sb.Egress;
        var egressNote = !e.IsOpen ? ""
            : e.Window?.Deadline is { } d ? $"  ⚠ egress open until {d.ToLocalTime():HH:mm}"
            : "  ⚠ egress OPEN, no timer";
        var item = new NativeMenuItem($"{(sb.IsRunning ? "●" : "○")}  {sb.Name}  ({(busy ? "working…" : sb.State)}){egressNote}");
        var sub = new NativeMenu();

        var resume = new NativeMenuItem("Resume") { IsEnabled = !busy && sb.IsStopped };
        resume.Click += (_, _) => RunOp(sb, "resume", (c, ct) => c.ResumeAsync(sb, ct));
        sub.Add(resume);
        var stop = new NativeMenuItem("Stop") { IsEnabled = !busy && sb.IsRunning };
        stop.Click += (_, _) => RunOp(sb, "stop", (c, ct) => c.StopAsync(sb, ct));
        sub.Add(stop);

        if (e.HasRules)   // hardened profile; standard sandboxes have open egress by design
        {
            sub.Add(new NativeMenuItemSeparator());
            if (e.IsOpen)
            {
                var lockNow = new NativeMenuItem("Lock down egress now") { IsEnabled = !busy };
                lockNow.Click += (_, _) => RunOp(sb, "lockdown", (_, ct) => SandmanClient.LockdownAsync(sb, ct: ct));
                sub.Add(lockNow);
            }

            var allow = new NativeMenuItem(e.IsOpen ? "Reset allow-all window" : "Allow all egress") { IsEnabled = !busy };
            var choices = new NativeMenu();
            foreach (var (label, ttl) in AllowAllChoices)
            {
                var c = new NativeMenuItem($"for {label}");
                c.Click += (_, _) => RunOp(sb, "allow-all", (_, ct) => SandmanClient.AllowAllAsync(sb, ttl, ct: ct));
                choices.Add(c);
            }
            allow.Menu = choices;
            sub.Add(allow);
        }

        item.Menu = sub;
        return item;
    }

    /// <summary>Run one operation per sandbox at a time, then refresh (UI thread).</summary>
    private void RunOp(SbxSandbox sb, string verb, Func<SandmanClient, CancellationToken, Task> op)
    {
        if (_client is not { } client || !_busy.Add(sb.Name)) return;
        RebuildMenu();
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await op(client, cts.Token);
            }
            catch (Exception e) { _error = $"{verb} {sb.Name} failed: {Friendly(e)}"; }
            finally
            {
                Dispatcher.UIThread.Post(() => _busy.Remove(sb.Name));
                await RefreshAsync();
            }
        });
    }

    private static string Friendly(Exception e) => e switch
    {
        SandmanException s => s.Message,
        Azure.Identity.AuthenticationFailedException => "not signed in to Azure: run `az login`",
        Azure.RequestFailedException r => $"{r.Status} {r.ErrorCode}",
        _ => e.Message.Split('\n')[0],
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
