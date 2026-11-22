using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;
using Microsoft.Azure.ContainerApps.Sandbox.Models;

namespace Sandman.Core;

/// <summary>
/// An allow-all window, as recorded by the marker rule `allow-all-&lt;opened&gt;-&lt;deadline&gt;` (unix seconds,
/// deadline 0 = no timer) on the reserved host allow-all.marker.invalid. Same format as sbx.sh, so a window
/// opened by either tool (from any machine) is visible to and enforced by the other.
/// </summary>
public sealed record AllowAllWindow(DateTimeOffset Opened, DateTimeOffset? Deadline)
{
    public bool IsExpired(DateTimeOffset now) => Deadline is { } d && now >= d;
}

/// <summary>What the tray / CLI need to know about a sandbox's egress.</summary>
public sealed record EgressState(bool HasRules, bool DefaultAllow, AllowAllWindow? Window)
{
    /// <summary>Hardened sandbox with egress opened by allow-all (standard sandboxes have no rules: always open, not our business).</summary>
    public bool IsOpen => HasRules && (DefaultAllow || Window is not null);

    public static EgressState None { get; } = new(false, false, null);
}

public static partial class Egress
{
    public const string MarkerHost = "allow-all.marker.invalid";
    public const string MarkerPrefix = "allow-all-";

    [GeneratedRegex(@"^allow-all-(\d+)-(\d+)$")]
    private static partial Regex MarkerRx();

    /// <summary>90 | 90s | 30m | 2h (sbx.sh's to_seconds).</summary>
    public static TimeSpan ParseTtl(string s)
    {
        var m = Regex.Match(s, @"^(\d+)([smh]?)$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0)
            throw new SandmanException($"bad duration '{s}' (use e.g. 30m, 2h)");
        return m.Groups[2].Value switch { "h" => TimeSpan.FromHours(n), "m" => TimeSpan.FromMinutes(n), _ => TimeSpan.FromSeconds(n) };
    }

    public static EgressState ReadState(SandboxEgressPolicy? policy) =>
        policy is null ? EgressState.None : ReadState(ToJson(policy));

    internal static EgressState ReadState(JsonObject policy)
    {
        var (defaultAction, rules) = Section(policy);
        var hasRules = rules is { Count: > 0 };
        AllowAllWindow? window = null;
        foreach (var r in rules ?? [])
        {
            var m = MarkerRx().Match(r?["name"]?.GetValue<string>() ?? "");
            if (!m.Success) continue;
            var opened = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value));
            var deadline = long.Parse(m.Groups[2].Value);
            window = new AllowAllWindow(opened, deadline == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(deadline));
        }
        return new EgressState(hasRules, string.Equals(defaultAction, "Allow", StringComparison.OrdinalIgnoreCase), window);
    }

    /// <summary>Policy with default Allow and a fresh marker (replacing any old one); profile rules still match first.</summary>
    internal static JsonObject WithAllowAll(JsonObject policy, DateTimeOffset now, DateTimeOffset? deadline)
    {
        var marker = $"{MarkerPrefix}{now.ToUnixTimeSeconds()}-{deadline?.ToUnixTimeSeconds() ?? 0}";
        return Edit(policy, "Allow", rules =>
        {
            DropMarkers(rules);
            rules.Add(new JsonObject
            {
                ["name"] = marker,
                ["match"] = new JsonObject { ["host"] = MarkerHost },
                ["action"] = new JsonObject { ["type"] = "Allow" },
            });
        });
    }

    internal static JsonObject WithLockdown(JsonObject policy) => Edit(policy, "Deny", DropMarkers);

    /// <summary>Append simple host Allow rules (keeps the default action).</summary>
    internal static JsonObject WithHostRules(JsonObject policy, IEnumerable<(string Name, string Host)> rules)
    {
        var list = rules.ToList();
        return Edit(policy, null, r =>
        {
            foreach (var (name, host) in list)
                r.Add(new JsonObject
                {
                    ["name"] = name,
                    ["match"] = new JsonObject { ["host"] = host },
                    ["action"] = new JsonObject { ["type"] = "Allow" },
                });
        });
    }

    /// <summary>Names of the policy's rules, excluding allow-all markers.</summary>
    internal static HashSet<string> RuleNames(JsonObject policy)
    {
        var (_, rules) = Section(policy);
        return (rules ?? []).Select(r => r?["name"]?.GetValue<string>())
            .Where(n => n is not null && !n.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            .Select(n => n!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The VS Code tunnel rules: the commented-out `vscode-*` / `ms-login*` block in hardened.yaml is the
    /// single source (SBX_VSCODE=1 uncomments it on `up`, allow-vscode patches it into a live policy).
    /// </summary>
    public static IReadOnlyList<(string Name, string Host)> VsCodeRules(string hardenedYamlPath) =>
        File.ReadLines(hardenedYamlPath)
            .Select(l => VsCodeRuleRx().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    [GeneratedRegex(@"^\s*#\s*- \{ name: ((?:vscode-|ms-login)[\w-]*), match: \{ host: ""([^""]+)"" \}, action: \{ type: Allow \} \}\s*$")]
    private static partial Regex VsCodeRuleRx();

    internal static JsonObject ToJson(SandboxEgressPolicy policy) =>
        JsonNode.Parse(ModelReaderWriter.Write(policy).ToString())!.AsObject();

    internal static RequestContent ToContent(JsonObject policy) =>
        RequestContent.Create(BinaryData.FromString(policy.ToJsonString()));

    // The service returns the HTTP policy twice: top-level defaultAction/rules (the shape `aca sandbox egress
    // export` uses) and mirrored under "http". Edit both identically so they can never disagree.
    private static JsonObject Edit(JsonObject policy, string? defaultAction, Action<JsonArray> editRules)
    {
        var copy = policy.DeepClone().AsObject();
        copy.Remove("validationWarnings");   // read-only, server generated
        foreach (var section in new[] { copy, copy["http"] as JsonObject })
        {
            if (section is null) continue;
            if (section["rules"] is not JsonArray rules) continue;
            if (defaultAction is not null) section["defaultAction"] = defaultAction;
            editRules(rules);
        }
        return copy;
    }

    private static void DropMarkers(JsonArray rules)
    {
        for (var i = rules.Count - 1; i >= 0; i--)
            if (rules[i]?["name"]?.GetValue<string>() is { } n && n.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                rules.RemoveAt(i);
    }

    private static (string? DefaultAction, JsonArray? Rules) Section(JsonObject policy)
    {
        // Prefer the explicit http section when present, fall back to the top-level shape.
        var s = policy["http"] as JsonObject is { } http && http["rules"] is JsonArray ? http : policy;
        return (s["defaultAction"]?.GetValue<string>(), s["rules"] as JsonArray);
    }
}
