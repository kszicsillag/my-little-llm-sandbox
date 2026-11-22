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
