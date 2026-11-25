using Sandman.Core;

namespace Sandman.Tray.Core;

/// <summary>
/// The tray's state and behaviour, independent of the UI toolkit: polls the sandboxes, locks down expired
/// allow-all windows, runs one operation per sandbox at a time, and describes the menu and the icon.
///
/// Allow-all deadlines live in the sandbox's own egress policy (marker rule, same format as sbx), so this
/// enforces windows opened by sbx, sbx.sh or another machine, and survives sleep / reboot: on every refresh,
/// any expired window is locked down.
///
/// Threading: state changes and <see cref="Changed"/> run on the <see cref="SynchronizationContext"/> passed
/// in (the head's UI thread or queue). Menu <see cref="MenuEntry.Click"/> actions may be invoked from any
/// thread; they post to that context themselves.
/// </summary>
public sealed class TrayController : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly (string Label, TimeSpan Ttl)[] AllowAllChoices =
        [("15 minutes", TimeSpan.FromMinutes(15)), ("30 minutes", TimeSpan.FromMinutes(30)),
         ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2))];

    private readonly SynchronizationContext _ui;
    private readonly HashSet<string> _busy = [];   // sandbox names with an operation in flight (on _ui only)
    private readonly Timer _poll;
    private readonly Timer _deadline;
    private SandmanClient? _client;
    private IReadOnlyList<SbxSandbox> _sandboxes = [];
    private string? _error;
    private int _refreshing;                       // 0/1, guards overlapping refreshes

    // VS Code tunnel per running sandbox (on _ui only): true / false, null = the check failed. Checked once per
    // running period (an exec might count as activity and hold off idle auto-suspend, so no polling), after our own
    // tunnel action, and on Refresh. Dropped when the sandbox stops: suspend ends the tunnel too.
    private readonly Dictionary<string, bool?> _tunnel = [];
    private readonly HashSet<string> _tunnelChecking = [];

    public TrayController(SynchronizationContext ui)
    {
        _ui = ui;
        _poll = new Timer(_ => _ = RefreshAsync());
        _deadline = new Timer(_ => _ = RefreshAsync());
    }

    /// <summary>Raised on the UI context after every refresh or state change: re-read the properties below.</summary>
    public event Action? Changed;

    public int RunningCount => _sandboxes.Count(s => s.IsRunning);

    public string ToolTip
    {
        get
        {
            if (_error is not null) return $"Sandman: {Truncate(_error, 100)}";
            var open = _sandboxes.Where(s => s.Egress.IsOpen).Select(s => s.Name).ToList();
            return $"Sandman: {RunningCount} running" + (open.Count > 0 ? $", egress OPEN: {string.Join(", ", open)}" : "");
        }
    }

    public void Start() => _poll.Change(TimeSpan.Zero, PollInterval);

    public void Dispose()
    {
        _poll.Dispose();
        _deadline.Dispose();
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            _client ??= new SandmanClient(AcaConfig.Load());
            _sandboxes = await _client.ListAsync().ConfigureAwait(false);
            _error = null;
        }
        catch (Exception e) { _error = Friendly(e); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }

        _ui.Post(_ =>
        {
            EnforceDeadlines();
            ScheduleNextDeadline();
            CheckTunnels();
            Changed?.Invoke();
        }, null);
    }

    /// <summary>Refresh from the menu: also re-checks the tunnels.</summary>
    private void RefreshAll() => _ui.Post(_ =>
    {
        _tunnel.Clear();
        _ = RefreshAsync();
    }, null);

    /// <summary>Forget tunnels of sandboxes that stopped; check running ones we know nothing about (UI context).</summary>
    private void CheckTunnels()
    {
        var running = _sandboxes.Where(s => s.IsRunning).ToList();
        foreach (var name in _tunnel.Keys.Except(running.Select(s => s.Name)).ToList()) _tunnel.Remove(name);
        if (_client is not { } client) return;

        foreach (var sb in running)
        {
            if (_tunnel.ContainsKey(sb.Name) || _busy.Contains(sb.Name) || !_tunnelChecking.Add(sb.Name)) continue;
            _ = Task.Run(async () =>
            {
                bool? up;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    up = await VsCode.IsTunnelRunningAsync(sb, cts.Token).ConfigureAwait(false);
                }
                catch { up = null; }
                _ui.Post(_ =>
                {
                    _tunnelChecking.Remove(sb.Name);
                    if (_sandboxes.Any(s => s.Name == sb.Name && s.IsRunning)) _tunnel[sb.Name] = up;
                    Changed?.Invoke();
                }, null);
            });
        }
    }

    /// <summary>
    /// The menu: per sandbox a header line, then its indented actions (Resume / Stop on the first level).
    /// <paramref name="nested"/>: the egress actions go into an "Egress" submenu (native menus). Flat: they're listed
    /// inline too, for menus where submenus don't work well (Avalonia's own tray menu on Windows).
    /// </summary>
    public IReadOnlyList<MenuEntry> BuildMenu(bool nested, Action quit)
    {
        var menu = new List<MenuEntry>();
        if (_error is not null) menu.Add(new($"⚠ {Truncate(_error, 70)}", Enabled: false));
        else if (_sandboxes.Count == 0) menu.Add(new("No sbx sandboxes", Enabled: false));

        foreach (var sb in _sandboxes)
        {
            menu.Add(new(Header(sb)));
            menu.AddRange(Actions(sb, nested).Select(a => a with { Label = "      " + a.Label }));
            menu.Add(MenuEntry.Separator);
        }
        if (_sandboxes.Count == 0) menu.Add(MenuEntry.Separator);

        menu.Add(new("Refresh", Click: RefreshAll));
        menu.Add(new("Quit Sandman", Click: quit));
        return menu;
    }

    private string Header(SbxSandbox sb)
    {
        var e = sb.Egress;
        var note = !e.IsOpen ? ""
            : e.Window?.Deadline is { } d ? $"  ⚠ egress open until {d.ToLocalTime():HH:mm}"
            : "  ⚠ egress OPEN, no timer";
        var state = _busy.Contains(sb.Name) ? "working…"
            : sb.IsRunning && _tunnel.GetValueOrDefault(sb.Name) == true ? $"{sb.State} · VS Code tunnel"
            : sb.State;
        return $"{(sb.IsRunning ? "●" : "○")}  {sb.Name}  ({state}){note}";
    }

    private List<MenuEntry> Actions(SbxSandbox sb, bool nested)
    {
        var items = new List<MenuEntry>();
        if (_busy.Contains(sb.Name)) return items;

        // ▶ with U+FE0E: text presentation, not the colour emoji.
        if (sb.IsResumable) items.Add(new("▶︎  Resume", Click: () => RunOp(sb, "resume", (c, ct) => c.ResumeAsync(sb, ct))));
        if (sb.IsRunning) items.Add(new("■  Stop", Click: () => RunOp(sb, "stop", (c, ct) => c.StopAsync(sb, ct))));
        if (sb.IsRunning || sb.IsResumable)
            items.Add(new(!sb.IsRunning ? "</>  Resume + VS Code tunnel"
                    : _tunnel.GetValueOrDefault(sb.Name) == true ? "</>  Open in VS Code" : "</>  VS Code tunnel",
                Click: () => RunOp(sb, "code", (c, ct) => CodeAsync(c, sb, ct))));

        var e = sb.Egress;
        if (!e.HasRules) return items;   // standard profile: open egress by design

        var egress = new List<MenuEntry>();
        if (e.IsOpen)
        {
            egress.Add(new("Lock down now", Click: () => RunOp(sb, "lockdown", (_, ct) => SandmanClient.LockdownAsync(sb, ct: ct))));
            if (nested) egress.Add(MenuEntry.Separator);
        }
        // Open: the same call moves the deadline to now + ttl, i.e. "lock down in <ttl>".
        var verb = e.IsOpen ? "Lock down in" : "Allow all egress for";
        foreach (var (label, ttl) in AllowAllChoices)
            egress.Add(new($"{verb} {label}", Click: () => RunOp(sb, "allow-all", (_, ct) => SandmanClient.AllowAllAsync(sb, ttl, ct: ct))));

        if (nested) items.Add(new("Egress", Children: egress));
        else items.AddRange(egress);
        return items;
    }

    /// <summary>
    /// sbx code from the tray: resume (and wait), start the tunnel, open it in desktop VS Code. The one-time device-code
    /// login needs a terminal, so if the tunnel isn't logged in yet this opens one running `sbx code name` instead.
    /// </summary>
    private async Task CodeAsync(SandmanClient client, SbxSandbox sb, CancellationToken ct)
    {
        await client.EnsureRunningAsync(sb, ct: ct, explicitResume: true);
        if (!await VsCode.IsLoggedInAsync(sb, ct))
        {
            Launcher.SbxInTerminal("code", sb.Name);
            return;
        }
        var tunnel = VsCode.TunnelName(sb.Name);
        await VsCode.SetIdleTimeoutAsync(sb, 3600, ct);
        await VsCode.StartTunnelAsync(sb, tunnel, ct);
        _ui.Post(_ => _tunnel[sb.Name] = true, null);   // before StartOp's refresh, which drops it if not Running
        var ws = await VsCode.WorkspacePathAsync(sb, ct);
        await Launcher.OpenTunnelAsync(tunnel, ws);
    }

    /// <summary>Lock down every sandbox whose allow-all window has expired (UI context).</summary>
    private void EnforceDeadlines()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var sb in _sandboxes)
            if (sb.Egress.IsOpen && sb.Egress.Window?.IsExpired(now) == true)
                StartOp(sb, "auto-lockdown", (_, ct) => SandmanClient.LockdownAsync(sb, ct: ct));
    }

    /// <summary>Wake up right at the next deadline instead of up to PollInterval late.</summary>
    private void ScheduleNextDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var next = _sandboxes
            .Select(s => s.Egress.IsOpen ? s.Egress.Window?.Deadline : null)
            .Where(d => d > now)
            .Min();
        _deadline.Change(next is { } d && d - now < PollInterval ? d - now + TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>From a menu click (any thread).</summary>
    private void RunOp(SbxSandbox sb, string verb, Func<SandmanClient, CancellationToken, Task> op) =>
        _ui.Post(_ => StartOp(sb, verb, op), null);

    /// <summary>One operation per sandbox at a time, then refresh (UI context).</summary>
    private void StartOp(SbxSandbox sb, string verb, Func<SandmanClient, CancellationToken, Task> op)
    {
        if (_client is not { } client || !_busy.Add(sb.Name)) return;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));   // resume alone may take 2
                await op(client, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) { _error = $"{verb} {sb.Name} failed: {Friendly(e)}"; }
            finally
            {
                _ui.Post(_ => _busy.Remove(sb.Name), null);
                await RefreshAsync().ConfigureAwait(false);
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
