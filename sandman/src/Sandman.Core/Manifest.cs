using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sandman.Core;

/// <summary>Inputs for rendering a .sandbox/&lt;profile&gt;.yaml into a create-sandbox request (sbx.sh `up`).</summary>
public sealed record ManifestOptions(
    string Name, string DiskId, string Cpu, string Memory, string RootDisk, string Volume,
    bool VsCode, IReadOnlyList<string> Allow);

/// <summary>
/// The profiles stay the source of truth (sbx.sh renders the same files). Steps, as in sbx.sh:
/// substitute the six known ${SBX_*} placeholders (envsubst with an explicit list), uncomment the VS Code
/// block for SBX_VSCODE=1, append SBX_ALLOW rules. Then YAML → JSON, and the one shape difference between
/// the `aca sandbox apply` manifest and the REST body: top-level `diskId` → `sourcesRef.diskImage.id`.
/// </summary>
public static partial class Manifest
{
    public static string Render(string template, ManifestOptions o)
    {
        var vars = new Dictionary<string, string>
        {
            ["SBX_DISK_ID"] = o.DiskId, ["SBX_CPU"] = o.Cpu, ["SBX_MEMORY"] = o.Memory,
            ["SBX_ROOT_DISK"] = o.RootDisk, ["SBX_NAME"] = o.Name, ["SBX_VOLUME"] = o.Volume,
        };
        var text = PlaceholderRx().Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        if (o.VsCode) text = VsCodeCommentRx().Replace(text, "$1$2");
        return text;
    }

    public static JsonObject ToRequest(string renderedYaml, ManifestOptions o)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(renderedYaml)); }
        catch (YamlException e) { throw new SandmanException($"profile is not valid YAML: {e.Message}"); }
        if (stream.Documents.Count == 0 || ToJson(stream.Documents[0].RootNode, null) is not JsonObject body)
            throw new SandmanException("profile is empty or not a YAML mapping");

        if (o.Allow.Count > 0) AppendAllowRules(body, o.Allow);

        if (body["diskId"] is JsonNode diskId)
        {
            body.Remove("diskId");
            body["sourcesRef"] = new JsonObject { ["diskImage"] = new JsonObject { ["id"] = diskId.DeepClone() } };
        }

        var unresolved = PlaceholderRx().Matches(body.ToJsonString()).Select(m => m.Value).Distinct().ToList();
        if (unresolved.Count > 0)
            throw new SandmanException($"profile has unresolved placeholders: {string.Join(", ", unresolved)}");
        return body;
    }

    /// <summary>SBX_ALLOW="host1,*.host2,host3/path/*": host-wide Allow, or path-restricted GET/HEAD (sbx.sh semantics).</summary>
    private static void AppendAllowRules(JsonObject body, IReadOnlyList<string> allow)
    {
        if (body["egressPolicy"]?["rules"] is not JsonArray rules)
            throw new SandmanException("SBX_ALLOW needs a profile with egress rules (hardened*)");
        var i = 0;
        foreach (var entry in allow)
        {
            i++;
            var slash = entry.IndexOf('/');
            var match = new JsonObject { ["host"] = slash < 0 ? entry : entry[..slash] };
            if (slash >= 0)
            {
                match["path"] = entry[slash..];
                match["methods"] = new JsonArray("GET", "HEAD");
            }
            rules.Add(new JsonObject
            {
                ["name"] = $"extra-{i}",
                ["match"] = match,
                ["action"] = new JsonObject { ["type"] = "Allow" },
            });
        }
    }

    // Maps whose values the API wants as strings even when YAML would read them as numbers/bools.
    private static readonly HashSet<string> StringMaps = ["labels", "environment", "resources"];

    private static JsonNode? ToJson(YamlNode node, string? parentKey) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
        {
            var key = ((YamlScalarNode)kv.Key).Value ?? "";
            var forceString = parentKey is not null && StringMaps.Contains(parentKey);
            var value = forceString && kv.Value is YamlScalarNode s ? JsonValue.Create(s.Value ?? "") : ToJson(kv.Value, key);
            return new KeyValuePair<string, JsonNode?>(key, value);
        })),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(c => ToJson(c, parentKey)).ToArray()),
        YamlScalarNode s => Scalar(s),
        _ => throw new SandmanException($"unsupported YAML node at {node.Start}"),
    };

    // YAML 1.2 core schema for plain scalars; quoted scalars are always strings.
    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style is not (ScalarStyle.Plain or ScalarStyle.Any)) return JsonValue.Create(v);
        if (v is "" or "~" or "null" or "Null" or "NULL") return null;
        if (v is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (v is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (IntRx().IsMatch(v) && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (FloatRx().IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    [GeneratedRegex(@"\$\{(SBX_[A-Z_]+)\}")]
    private static partial Regex PlaceholderRx();

    [GeneratedRegex(@"^(\s*)# (- \{ name: (?:vscode-|ms-login))", RegexOptions.Multiline)]
    private static partial Regex VsCodeCommentRx();

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntRx();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRx();

    /// <summary>Single-quote for the remote bash: ' → '\''.</summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
