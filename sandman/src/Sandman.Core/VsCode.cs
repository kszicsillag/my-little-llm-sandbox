using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

namespace Sandman.Core;

/// <summary>sbx.sh `code`: VS Code Remote Tunnel in the sandbox (CLI pinned in the image).</summary>
public static class VsCode
{
    /// <summary>
    /// Device-code login, needs a PTY (Shell.RunAsync). Microsoft provider: a GitHub login conflicts with the
    /// hardened profile's github.com header injection.
    /// </summary>
    public static IReadOnlyList<string> LoginCommand { get; } = ["code", "tunnel", "user", "login", "--provider", "microsoft"];

    /// <summary>sbx-&lt;name&gt;, lowercase, '_' and '.' → '-', at most 20 chars (tunnel name limit). Same as sbx.sh, so the tunnel keeps its name.</summary>
    public static string TunnelName(string sandboxName)
    {
        var n = ("sbx-" + sandboxName).ToLowerInvariant().Replace('_', '-').Replace('.', '-');
        return n.Length <= 20 ? n : n[..20];
    }

    /// <summary>Tunnel traffic doesn't count as sandbox activity: give idle auto-suspend more room while coding.</summary>
    public static async Task SetIdleTimeoutAsync(SbxSandbox sandbox, int seconds, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["autoSuspendPolicy"] = new JsonObject { ["enabled"] = true, ["interval"] = seconds, ["mode"] = "Disk" },
        };
        await sandbox.Resource.SetLifecyclePolicyAsync(
            RequestContent.Create(BinaryData.FromString(body.ToJsonString())), new Azure.RequestContext { CancellationToken = ct });
    }

    public static async Task<bool> IsLoggedInAsync(SbxSandbox sandbox, CancellationToken ct = default) =>
        await SandmanClient.ExecAsync(sandbox, "code tunnel user show >/dev/null 2>&1", _ => { }, _ => { }, ct) == 0;

    // PID file, not pgrep: the exec's own command line would match.
    private const string TunnelRunning = "[ -f /tmp/code-tunnel.pid ] && kill -0 $(cat /tmp/code-tunnel.pid) 2>/dev/null";

    /// <summary>Whether the tunnel process started by <see cref="StartTunnelAsync"/> is alive.</summary>
    public static async Task<bool> IsTunnelRunningAsync(SbxSandbox sandbox, CancellationToken ct = default) =>
        await SandmanClient.ExecAsync(sandbox, TunnelRunning, _ => { }, _ => { }, ct) == 0;

    /// <summary>Start the tunnel unless it already runs. Returns the log tail.</summary>
    public static async Task<string> StartTunnelAsync(SbxSandbox sandbox, string tunnel, CancellationToken ct = default)
    {
        var t = Manifest.ShQuote(tunnel);
        var script =
            $"if {TunnelRunning}; then echo 'tunnel already running'; " +
            $"else nohup code tunnel --accept-server-license-terms --name {t} >/tmp/code-tunnel.log 2>&1 </dev/null & echo $! >/tmp/code-tunnel.pid; sleep 8; fi; " +
            "tail -4 /tmp/code-tunnel.log";
        var (exit, output) = await CaptureAsync(sandbox, script, ct);
        if (exit != 0) throw new SandmanException($"starting the tunnel failed ({exit}): {output.Trim()}");
        return output;
    }

    /// <summary>
    /// The real workspace path (not the ~/workspace symlink): Workspace Trust is per opened path, stored in desktop
    /// VS Code, so trusting /persist/workspace once covers every repo and every rebuild.
    /// </summary>
    public static async Task<string?> WorkspacePathAsync(SbxSandbox sandbox, CancellationToken ct = default)
    {
        var (_, output) = await CaptureAsync(sandbox, "readlink -f ~/workspace 2>/dev/null", ct);
        var ws = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(ws) ? null : ws;
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(SbxSandbox sandbox, string command, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var exit = await SandmanClient.ExecAsync(sandbox, command, s => sb.Append(s), s => sb.Append(s), ct);
        return (exit, sb.ToString());
    }
}
