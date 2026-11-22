using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.ContainerApps.Sandbox;
using Microsoft.Azure.ContainerApps.Sandbox.Models;

namespace Sandman.Core;

/// <summary>A sandbox created by sbx / Sandman: the ones carrying an `sbx=&lt;name&gt;` label.</summary>
public sealed record SbxSandbox(string Name, string Id, string State, SandboxResource Resource)
{
    public bool IsRunning => State == "Running";
    public bool IsStopped => State == "Stopped";

    /// <summary>Egress state from the list snapshot (may lag a just-applied change; use GetEgressStateAsync for a fresh read).</summary>
    public EgressState Egress => Core.Egress.ReadState(Resource.Data?.EgressPolicy);
}

/// <summary>The signed-in Entra identity, read from the access token's claims.</summary>
public sealed record SignedInUser(string? Email, string? TenantId);

/// <summary>sbx.sh's operations over the ACA Sandboxes .NET SDK (no aca / az / bash processes).</summary>
public sealed class SandmanClient
{
    public const string NameLabel = "sbx";

    private readonly TokenCredential _credential;
    private readonly SandboxGroupClient _group;

    public AcaConfig Config { get; }

    public SandmanClient(AcaConfig config, TokenCredential? credential = null)
    {
        Config = config;
        _credential = credential ?? DefaultCredential();
        _group = new SandboxGroupClient(config.Region, config.Subscription, config.ResourceGroup, config.Group, _credential);
    }

    /// <summary>
    /// Reuse the `az login` session first (what sbx.sh relied on); fall back to the usual
    /// developer credentials (VS, VS Code, environment, managed identity). No browser pop-ups.
    /// </summary>
    public static TokenCredential DefaultCredential() =>
        new ChainedTokenCredential(
            new AzureCliCredential(),
            new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true }));

    public async Task<IReadOnlyList<SbxSandbox>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<SbxSandbox>();
        await foreach (var sb in _group.GetSandboxesAsync(null, null, ct))
        {
            var data = sb.Data;
            if (data?.Labels is null || !data.Labels.TryGetValue(NameLabel, out var name) || string.IsNullOrEmpty(name))
                continue;
            result.Add(new SbxSandbox(name, sb.Id, data.State?.ToString() ?? "Unknown", sb));
        }
        return result.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<SbxSandbox> GetAsync(string name, CancellationToken ct = default)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(s => s.Name == name)
            ?? throw new SandmanException($"no sandbox named '{name}'");
    }

    /// <summary>sbx.sh's "name may be omitted if exactly one sbx sandbox is running".</summary>
    public async Task<SbxSandbox> ResolveAsync(string? name, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(name)) return await GetAsync(name, ct);
        var running = (await ListAsync(ct)).Where(s => s.IsRunning).ToList();
        return running.Count == 1
            ? running[0]
            : throw new SandmanException(
                $"name required: {running.Count} sbx sandboxes running{(running.Count > 0 ? $" ({string.Join(" ", running.Select(r => r.Name))})" : "")}");
    }

    public async Task StopAsync(SbxSandbox sandbox, CancellationToken ct = default) =>
        await sandbox.Resource.StopAsync(ct);

    public async Task ResumeAsync(SbxSandbox sandbox, CancellationToken ct = default) =>
        await sandbox.Resource.ResumeAsync(ct);

    /// <summary>
    /// Make sure the sandbox runs (resume + wait) when Config.AutoResume allows it. Returns the fresh
    /// state: true = it was resumed. Throws if it's not running and can't be resumed.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(SbxSandbox sandbox, Action<string>? progress = null, CancellationToken ct = default)
    {
        var state = (await sandbox.Resource.GetAsync(ct)).Value.Data?.State?.ToString();
        if (state == "Running") return false;
        if (!Config.AutoResume)
            throw new SandmanException($"'{sandbox.Name}' is {state}; resume it first: sbx resume {sandbox.Name}");
        if (state is not ("Stopped" or "Idle"))
            throw new SandmanException($"'{sandbox.Name}' is {state}; try again in a moment");

        progress?.Invoke($"(resuming '{sandbox.Name}'...)");
        await sandbox.Resource.ResumeAsync(ct);
        for (var i = 0; i < 60; i++)
        {
            state = (await sandbox.Resource.GetAsync(ct)).Value.Data?.State?.ToString();
            if (state == "Running") return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new SandmanException($"'{sandbox.Name}' did not reach Running within 2 minutes (state: {state})");
    }

    /// <summary>
    /// Expose a port via sandbox ingress, restricted to one Entra identity in its tenant (never anonymous).
    /// Replaces sbx.sh's `az rest` workaround: `aca sandbox port add --email` omits tenantIds, the SDK doesn't.
    /// </summary>
    public async Task<Uri?> PreviewAsync(SbxSandbox sandbox, int port, string email, string tenantId, CancellationToken ct = default)
    {
        try { await sandbox.Resource.RemovePortAsync(new RemovePortContent { Port = port }, ct); }
        catch (Azure.RequestFailedException) { /* not exposed yet */ }

        var auth = new PortAuthConfig
        {
            EntraId = new PortAuthConfigEntraId { Enabled = true },
        };
        auth.EntraId.Emails.Add(email);
        auth.EntraId.TenantIds.Add(tenantId);

        await sandbox.Resource.AddPortAsync(new CreateSandboxPortContent(port) { Auth = auth }, ct);

        var ports = await sandbox.Resource.GetPortsAsync(ct);
        return ports.Value.Ports.FirstOrDefault(p => p.Port == port)?.Url;
    }

    /// <summary>The sandbox's current egress policy (fresh GET, not the possibly stale list snapshot).</summary>
    public static async Task<SandboxEgressPolicy?> GetEgressPolicyAsync(SbxSandbox sandbox, CancellationToken ct = default)
    {
        var fresh = await sandbox.Resource.GetAsync(ct);
        return fresh.Value.Data?.EgressPolicy;
    }

    public static async Task<EgressState> GetEgressStateAsync(SbxSandbox sandbox, CancellationToken ct = default) =>
        Egress.ReadState(await GetEgressPolicyAsync(sandbox, ct));

    /// <summary>
    /// Switch egress default to Allow (named rules still match first: token injection, block-repo-delete).
    /// ttl null = until lockdown. Returns the deadline.
    /// </summary>
    public static async Task<DateTimeOffset?> AllowAllAsync(SbxSandbox sandbox, TimeSpan? ttl, Action<string, string>? dryRun = null, CancellationToken ct = default)
    {
        var policy = await GetEgressPolicyAsync(sandbox, ct);
        var json = policy is null ? null : Egress.ToJson(policy);
        if (json is null || !Egress.ReadState(json).HasRules)
            throw new SandmanException($"'{sandbox.Name}' has no egress rules (standard profile allows all egress already)");

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = ttl is { } t ? now + t : null;
        var updated = Egress.WithAllowAll(json, now, deadline);
        if (dryRun is not null) { dryRun(json.ToJsonString(), updated.ToJsonString()); return deadline; }
        await sandbox.Resource.SetEgressPolicyAsync(Egress.ToContent(updated), new Azure.RequestContext { CancellationToken = ct });
        return deadline;
    }

    /// <summary>Default Deny again, marker removed, verified by re-reading. False = was already locked down.</summary>
    public static async Task<bool> LockdownAsync(SbxSandbox sandbox, Action<string, string>? dryRun = null, CancellationToken ct = default)
    {
        var policy = await GetEgressPolicyAsync(sandbox, ct);
        var json = policy is null ? null : Egress.ToJson(policy);
        var state = json is null ? EgressState.None : Egress.ReadState(json);
        if (!state.HasRules)
            throw new SandmanException($"'{sandbox.Name}' has no egress rules (standard profile): not touching it");
        if (!state.IsOpen) return false;

        var updated = Egress.WithLockdown(json!);
        if (dryRun is not null) { dryRun(json!.ToJsonString(), updated.ToJsonString()); return true; }
        await sandbox.Resource.SetEgressPolicyAsync(Egress.ToContent(updated), new Azure.RequestContext { CancellationToken = ct });

        var after = await GetEgressStateAsync(sandbox, ct);
        if (after.DefaultAllow)
            throw new SandmanException($"lockdown did not take effect for '{sandbox.Name}' (default is still Allow)");
        return true;
    }

    /// <summary>
    /// Run a shell command (bash -c) as <see cref="SandboxUser"/>, streaming output as it arrives. Returns
    /// the exit code. Uses an exec session, not the buffered ExecuteShellCommand.
    /// </summary>
    public static async Task<int> ExecAsync(SbxSandbox sandbox, string command,
        Action<string> stdout, Action<string> stderr, CancellationToken ct = default)
    {
        // Exec sessions run as root unless told otherwise (`aca sandbox exec` used the image USER).
        var request = new SandboxExecStartRequest("bash") { User = SandboxUser };
        request.Arguments.Add("-c");
        request.Arguments.Add(command);

        await using var session = await sandbox.Resource.StartSandboxExecSessionAsync(request, cancellationToken: ct);
        int? exit = null;
        while (true)
        {
            var e = await session.ReceiveAsync(ct);
            if (e is null) break;
            if (e.Type == SandboxExecEventType.StandardOutput) stdout(Payload(e));
            else if (e.Type == SandboxExecEventType.StandardError) stderr(Payload(e));
            else if (e.Type == SandboxExecEventType.ExitCode) exit = e.ExitCode;
            else if (e.Type == SandboxExecEventType.Error) throw new SandmanException($"exec failed: {Payload(e)}");
            else if (e.Type == SandboxExecEventType.Closed) break;
        }
        return exit ?? throw new SandmanException("exec session closed without an exit code");
    }

    /// <summary>The image's USER (see .sandbox/image/Dockerfile); SBX_USER overrides.</summary>
    public static string SandboxUser =>
        Environment.GetEnvironmentVariable("SBX_USER") is { Length: > 0 } u ? u : "dev";

    private static string Payload(SandboxExecEvent e) => e.Text ?? e.Data?.ToString() ?? "";

    /// <summary>Delete the sandbox (the volume persist-&lt;name&gt; is kept) and wait until it is gone.</summary>
    public async Task DeleteAsync(SbxSandbox sandbox, CancellationToken ct = default)
    {
        await sandbox.Resource.DeleteAsync(ct);
        for (var i = 0; i < 60; i++)
        {
            if ((await ListAsync(ct)).All(s => s.Id != sandbox.Id)) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new SandmanException($"'{sandbox.Name}' is still listed 2 minutes after delete");
    }

    public static string VolumeName(string sandboxName) => $"persist-{sandboxName}";

    /// <summary>Delete the volume persist-&lt;name&gt;. False = it didn't exist.</summary>
    public async Task<bool> DeleteVolumeAsync(string sandboxName, CancellationToken ct = default)
    {
        try
        {
            await _group.GetVolumeResource(VolumeName(sandboxName)).DeleteVolumeAsync(ct);
            return true;
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404) { return false; }
    }

    /// <summary>A host reached only via default Allow (no named rule matched): a candidate for SBX_ALLOW.</summary>
    public sealed record LearnedHost(string Host, int Hits, IReadOnlyList<string> Methods, DateTimeOffset LastSeen, string SamplePath);

    public static async Task<IReadOnlyList<LearnedHost>> LearnedAsync(SbxSandbox sandbox, CancellationToken ct = default)
    {
        var policy = await GetEgressPolicyAsync(sandbox, ct);
        var named = policy is null ? [] : Egress.RuleNames(Egress.ToJson(policy));
        var decisions = await sandbox.Resource.GetEgressDecisionsAsync(ct);

        return (decisions.Value.Http?.Allowed ?? [])
            .Where(x => x.MatchedRule is null || !named.Contains(x.MatchedRule))
            .Where(x => !string.IsNullOrEmpty(x.Host))
            .GroupBy(x => x.Host!)
            .Select(g => new LearnedHost(
                g.Key, g.Count(),
                g.Select(x => x.Method ?? "?").Distinct().Order().ToList(),
                g.Max(x => x.Timestamp),
                g.Select(x => (x.Path ?? "").Length > 60 ? x.Path![..60] : x.Path ?? "").Order().First()))
            .OrderByDescending(h => h.Hits)
            .ToList();
    }

    /// <summary>Add the VS Code tunnel host rules the live policy lacks. Returns the added rule names (empty = already there).</summary>
    public static async Task<IReadOnlyList<string>> AllowVsCodeAsync(SbxSandbox sandbox, string hardenedYamlPath,
        Action<string, string>? dryRun = null, CancellationToken ct = default)
    {
        var policy = await GetEgressPolicyAsync(sandbox, ct);
        var json = policy is null ? null : Egress.ToJson(policy);
        if (json is null || !Egress.ReadState(json).HasRules)
            throw new SandmanException($"'{sandbox.Name}' has no egress rules (standard profile allows all egress already)");

        var wanted = Egress.VsCodeRules(hardenedYamlPath);
        if (wanted.Count == 0)
            throw new SandmanException($"no commented-out vscode-*/ms-login* rules found in {hardenedYamlPath}");
        var existing = Egress.RuleNames(json);
        var missing = wanted.Where(r => !existing.Contains(r.Name)).ToList();
        if (missing.Count == 0) return [];

        var updated = Egress.WithHostRules(json, missing);
        if (dryRun is not null) dryRun(json.ToJsonString(), updated.ToJsonString());
        else await sandbox.Resource.SetEgressPolicyAsync(Egress.ToContent(updated), new Azure.RequestContext { CancellationToken = ct });
        return missing.Select(m => m.Name).ToList();
    }

    /// <summary>Create or replace a sandbox-group secret (all keys at once). Values are never logged.</summary>
    public async Task SetSecretAsync(string name, IDictionary<string, string> values, CancellationToken ct = default) =>
        await _group.SetSecretAsync(name, new SetSecretContent(values), ct);

    /// <summary>Email + tenant of whoever the credential signs in as (replaces `az account show`).</summary>
    public async Task<SignedInUser> GetSignedInUserAsync(CancellationToken ct = default)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct);
        var parts = token.Token.Split('.');
        if (parts.Length < 2) return new SignedInUser(null, null);

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var c = doc.RootElement;

        string? Claim(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new SignedInUser(
            Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name") ?? Claim("email"),
            Claim("tid"));
    }
}
