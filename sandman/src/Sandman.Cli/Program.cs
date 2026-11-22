using System.CommandLine;
using System.Text.RegularExpressions;
using Sandman.Core;

// sbx: sbx.sh's commands over the ACA Sandboxes .NET SDK. Commands are ported one slice at a time;
// anything not here yet still lives in .sandbox/sbx.sh.

var root = new RootCommand("sbx: devcontainer-style workflow for ACA Sandboxes (Sandman CLI)");

// ls
var ls = new Command("ls", "list sandboxes created by sbx");
ls.SetAction(async (_, ct) => await Run(async client =>
{
    var all = await client.ListAsync(ct);
    if (all.Count == 0) { Console.WriteLine("no sbx sandboxes"); return; }
    var w = Math.Max(4, all.Max(s => s.Name.Length));
    Console.WriteLine($"{"NAME".PadRight(w)}  {"STATE",-10}  {"EGRESS",-22}  ID");
    foreach (var s in all) Console.WriteLine($"{s.Name.PadRight(w)}  {s.State,-10}  {Describe(s.Egress),-22}  {s.Id}");
}));
root.Subcommands.Add(ls);

// stop [name]: name optional when exactly one sbx sandbox is running
var stopName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: the only running one)" };
var stop = new Command("stop", "stop the sandbox now (instead of waiting for idle auto-suspend)") { stopName };
stop.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await Resolve(client, pr.GetValue(stopName), ct);
    await client.StopAsync(sb, ct);
    Console.WriteLine($"stopped '{sb.Name}'");
}));
root.Subcommands.Add(stop);

// resume <name>: required, the "only running sandbox" shortcut can't apply to a stopped one
var resumeName = new Argument<string>("name") { Description = "sandbox name" };
var resume = new Command("resume", "start a stopped sandbox again") { resumeName };
resume.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await client.GetAsync(pr.GetValue(resumeName)!, ct);
    await client.ResumeAsync(sb, ct);
    Console.WriteLine($"resumed '{sb.Name}'");
}));
root.Subcommands.Add(resume);

// preview [name] [port|from-to] [email]
var previewArgs = new Argument<string[]>("args")
{
    Arity = new ArgumentArity(0, 3),
    Description = "[name] [port|from-to] [email]  (default port 8000, default email = signed-in Azure user)",
};
var preview = new Command("preview", "expose port(s) via sandbox ingress, Entra ID auth (never anonymous)") { previewArgs };
preview.SetAction(async (pr, ct) => await Run(async client =>
{
    var a = new Queue<string>(pr.GetValue(previewArgs) ?? []);
    var portRx = new Regex(@"^\d+(-\d+)?$");
    // Like sbx.sh: a port in the name's place means "no name given".
    string? name = a.Count > 0 && !portRx.IsMatch(a.Peek()) ? a.Dequeue() : null;
    string portSpec = a.Count > 0 ? a.Dequeue() : "8000";
    string? email = a.Count > 0 ? a.Dequeue() : null;

    var ports = ParsePorts(portSpec);
    var sb = await Resolve(client, name, ct);
    var me = await client.GetSignedInUserAsync(ct);
    email ??= me.Email ?? throw new SandmanException("no email given and none in the signed-in identity's token");
    var tenant = me.TenantId ?? throw new SandmanException("no tenant id in the signed-in identity's token");

    foreach (var port in ports)
    {
        var url = await client.PreviewAsync(sb, port, email, tenant, ct);
        Console.WriteLine(url?.ToString() ?? $"port {port}: added, but no URL returned yet");
    }
    Console.WriteLine($"(Entra ID auth: {email}, tenant {tenant})");
}));
root.Subcommands.Add(preview);

// egress [name]: show the egress policy as JSON (like `aca sandbox egress show`)
var egressName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: the only running one)" };
var egress = new Command("egress", "show the sandbox's egress policy (JSON)") { egressName };
egress.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await Resolve(client, pr.GetValue(egressName), ct);
    var policy = await SandmanClient.GetEgressPolicyAsync(sb, ct);
    Console.WriteLine(policy is null ? "(no egress policy)"
        : System.ClientModel.Primitives.ModelReaderWriter.Write(policy).ToString());
}));
root.Subcommands.Add(egress);

// allow-all [name] [ttl]: like sbx.sh, a ttl in the name's place means "no name given"
var allowArgs = new Argument<string[]>("args") { Arity = new ArgumentArity(0, 2), Description = "[name] [ttl]  (ttl e.g. 30m, 2h; none = until lockdown)" };
var dryRunOpt = new Option<bool>("--dry-run") { Description = "print the policy change instead of applying it" };
var allowAll = new Command("allow-all", "hardened: switch egress default to Allow (named rules still apply first)") { allowArgs, dryRunOpt };
allowAll.SetAction(async (pr, ct) => await Run(async client =>
{
    var a = new Queue<string>(pr.GetValue(allowArgs) ?? []);
    var ttlRx = new Regex(@"^\d+[smh]?$");
    string? name = a.Count > 0 && !ttlRx.IsMatch(a.Peek()) ? a.Dequeue() : null;
    TimeSpan? ttl = a.Count > 0 ? Egress.ParseTtl(a.Dequeue()) : null;
    var dry = pr.GetValue(dryRunOpt);

    var sb = await Resolve(client, name, ct);
    var deadline = await SandmanClient.AllowAllAsync(sb, ttl, dry ? PrintDiff : null, ct);
    if (dry) return;
    Console.WriteLine(deadline is { } d
        ? $"egress OPEN for '{sb.Name}' until {d.ToLocalTime():HH:mm} (auto-lockdown while the Sandman tray is running, or on any later `sbx` / `sbx.sh lockdown`)"
        : $"egress OPEN for '{sb.Name}' until: sbx lockdown {sb.Name}");
    Console.WriteLine("still enforced: token injection (github/anthropic), block-repo-delete. NOT enforced: data exfiltration, untrusted downloads.");
}));
root.Subcommands.Add(allowAll);

// lockdown [name]
var lockName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: the only running one)" };
var lockdown = new Command("lockdown", "switch egress default back to Deny") { lockName, dryRunOpt };
lockdown.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await Resolve(client, pr.GetValue(lockName), ct);
    var dry = pr.GetValue(dryRunOpt);
    var changed = await SandmanClient.LockdownAsync(sb, dry ? PrintDiff : null, ct);
    if (dry) return;
    Console.WriteLine(changed ? $"egress locked down for '{sb.Name}' (default Deny)" : $"'{sb.Name}' is already locked down");
}));
root.Subcommands.Add(lockdown);

return await root.Parse(args).InvokeAsync();

static string Describe(EgressState e) =>
    !e.HasRules ? "open (no rules)"
    : !e.IsOpen ? "locked"
    : e.Window?.Deadline is { } d ? $"OPEN until {d.ToLocalTime():MM-dd HH:mm}"
    : "OPEN (no timer)";

// Dry run: show only what changes (rule names + default action), never rule contents (they reference secrets).
static void PrintDiff(string before, string after)
{
    static (string? Default, string? HttpDefault, List<string> Rules, List<string> HttpRules) Summ(string json)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        static List<string> Names(System.Text.Json.Nodes.JsonNode? rules) =>
            rules is System.Text.Json.Nodes.JsonArray a ? a.Select(r => r?["name"]?.GetValue<string>() ?? "?").ToList() : [];
        return (o["defaultAction"]?.GetValue<string>(), o["http"]?["defaultAction"]?.GetValue<string>(), Names(o["rules"]), Names(o["http"]?["rules"]));
    }
    var b = Summ(before); var a = Summ(after);
    Console.WriteLine($"defaultAction:      {b.Default} -> {a.Default}");
    Console.WriteLine($"http.defaultAction: {b.HttpDefault} -> {a.HttpDefault}");
    Console.WriteLine($"rules:      {b.Rules.Count} -> {a.Rules.Count}  removed: [{string.Join(", ", b.Rules.Except(a.Rules))}]  added: [{string.Join(", ", a.Rules.Except(b.Rules))}]");
    Console.WriteLine($"http.rules: {b.HttpRules.Count} -> {a.HttpRules.Count}  removed: [{string.Join(", ", b.HttpRules.Except(a.HttpRules))}]  added: [{string.Join(", ", a.HttpRules.Except(b.HttpRules))}]");
    Console.WriteLine($"(dry run: nothing applied; payload {after.Length} bytes)");
}

static async Task<SbxSandbox> Resolve(SandmanClient client, string? name, CancellationToken ct)
{
    var sb = await client.ResolveAsync(name, ct);
    if (string.IsNullOrEmpty(name)) Console.Error.WriteLine($"(using the only running sandbox: {sb.Name})");
    return sb;
}

static int[] ParsePorts(string spec)
{
    var m = Regex.Match(spec, @"^(\d+)(?:-(\d+))?$");
    if (!m.Success) throw new SandmanException($"bad port '{spec}'");
    int from = int.Parse(m.Groups[1].Value), to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : from;
    if (from > to || to - from >= 20) throw new SandmanException($"bad port range '{spec}' (from<=to, at most 20 ports)");
    return Enumerable.Range(from, to - from + 1).ToArray();
}

static async Task<int> Run(Func<SandmanClient, Task> body)
{
    try
    {
        await body(new SandmanClient(AcaConfig.Load()));
        return 0;
    }
    catch (SandmanException e) { Console.Error.WriteLine($"sbx: {e.Message}"); return 1; }
    catch (Azure.Identity.AuthenticationFailedException e)
    {
        Console.Error.WriteLine($"sbx: not signed in to Azure ({e.Message.Split('\n')[0]}). Run `az login`.");
        return 1;
    }
    catch (Azure.RequestFailedException e)
    {
        Console.Error.WriteLine($"sbx: {e.Status} {e.ErrorCode}: {e.Message.Split('\n')[0]}");
        return 1;
    }
    catch (OperationCanceledException) { return 130; }
}
