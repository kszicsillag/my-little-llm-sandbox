using System.Text.Json;

namespace Sandman.Core;

/// <summary>
/// Which sandbox group to talk to. Read from the aca CLI's own config (~/.aca/config.json,
/// written by `aca config sandbox set`), with the same ACA_* environment overrides sbx.sh honors.
/// </summary>
public sealed record AcaConfig(string Subscription, string ResourceGroup, string Region, string Group)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aca", "config.json");

    public static AcaConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        string? sub = null, rg = null, region = null, group = null;

        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("sandbox", out var s))
            {
                sub = Str(s, "subscription");
                rg = Str(s, "resource_group");
                region = Str(s, "region");
                group = Str(s, "group");
            }
        }

        sub = Env("ACA_SUBSCRIPTION") ?? sub;
        rg = Env("ACA_RESOURCE_GROUP") ?? rg;
        region = Env("ACA_REGION") ?? region;
        group = Env("ACA_SANDBOX_GROUP") ?? group;

        var missing = new[] { ("subscription", sub), ("resource_group", rg), ("region", region), ("group", group) }
            .Where(x => string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1).ToList();
        if (missing.Count > 0)
            throw new SandmanException(
                $"sandbox group config incomplete (missing: {string.Join(", ", missing)}). " +
                $"Run `aca config sandbox set --group <group>` or set ACA_SUBSCRIPTION / ACA_RESOURCE_GROUP / ACA_REGION / ACA_SANDBOX_GROUP. Looked in {path}");

        return new AcaConfig(sub!, rg!, region!, group!);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

/// <summary>An expected failure with a message meant for the user (no stack trace needed).</summary>
public sealed class SandmanException(string message) : Exception(message);
