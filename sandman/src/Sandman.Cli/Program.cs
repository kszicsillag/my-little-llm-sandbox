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

// up <standard|hardened|hardened-apikey> <repo-url> [name]: same env knobs as sbx.sh
// (SBX_IMAGE, SBX_CPU, SBX_MEMORY, SBX_ROOT_DISK, SBX_VOLUME_SIZE, SBX_VSCODE=1, SBX_ALLOW)
var upProfile = new Argument<string>("profile") { Description = "standard | hardened | hardened-apikey (a .sandbox/<profile>.yaml)" };
var upRepo = new Argument<string>("repo-url") { Description = "git repository to clone into the workspace" };
var upName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: repo name)" };
var up = new Command("up", "create volume (once) + sandbox from a profile, run bootstrap") { upProfile, upRepo, upName, dryRunOpt };
up.SetAction(async (pr, ct) => await Run(async client =>
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var o = SandmanClient.UpOptions.FromEnvironment(pr.GetValue(upProfile)!, pr.GetValue(upRepo)!, pr.GetValue(upName));
    var plan = await client.PlanUpAsync(o, ct);
    if (o.VsCode) Console.Error.WriteLine("SBX_VSCODE=1: VS Code tunnel hosts allowed");
    if (o.Allow.Count > 0) Console.Error.WriteLine($"SBX_ALLOW: {o.Allow.Count} extra rule(s) allowed");

    if (pr.GetValue(dryRunOpt))
    {
        Console.Error.WriteLine($"name {o.Name}, disk image {o.Image} ({plan.DiskId}), volume {plan.Volume} {(plan.VolumeExists ? "exists (reused)" : $"would be created ({o.VolumeSize})")}");
        Console.WriteLine(plan.Request.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine("(dry run: nothing created)");
        return;
    }

    var sb = await client.UpAsync(o, plan, Console.Error.WriteLine, Console.Out.Write, Console.Error.Write, ct);
    Console.WriteLine($"ready: sbx.sh shell {sb.Name}");
}));
root.Subcommands.Add(up);

// exec <name> <command...>: everything after the name is the remote command, verbatim (joined with
// spaces and run by bash -c, like sbx.sh's "$*"). Dispatched before the parser so `-la` etc. aren't sbx options;
// registered here only for --help.
var execCmd = new Command("exec", "run a command (bash -c, as dev), streaming output; exit code is passed through")
{
    new Argument<string>("name") { Description = "sandbox name" },
    new Argument<string[]>("command") { Arity = ArgumentArity.OneOrMore, Description = "command and arguments" },
};
root.Subcommands.Add(execCmd);
if (args is ["exec", var execName, .. var execRest] && execRest.Length > 0 && execName is not ("-h" or "--help" or "-?"))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var exit = 1;
    var rc = await Run(async client =>
    {
        var sb = await client.GetAsync(execName, cts.Token);
        await client.EnsureRunningAsync(sb, Console.Error.WriteLine, cts.Token);
        exit = await SandmanClient.ExecAsync(sb, string.Join(' ', execRest), Console.Out.Write, Console.Error.Write, cts.Token);
    });
    return rc != 0 ? rc : exit;
}

// down <name> / purge <name>: destructive, so the name is always required and we confirm
var yesOpt = new Option<bool>("--yes", "-y") { Description = "don't ask for confirmation" };
var downName = new Argument<string>("name") { Description = "sandbox name" };
var down = new Command("down", "delete the sandbox, KEEP the volume (\"rebuild\")") { downName, yesOpt };
down.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await client.GetAsync(pr.GetValue(downName)!, ct);
    if (!pr.GetValue(yesOpt) && !Confirm($"delete sandbox '{sb.Name}' (volume {SandmanClient.VolumeName(sb.Name)} is kept)?")) return;
    await client.DeleteAsync(sb, ct);
    Console.WriteLine($"deleted sandbox '{sb.Name}'; volume {SandmanClient.VolumeName(sb.Name)} kept (sbx up <profile> <repo-url> {sb.Name} brings it back)");
}));
root.Subcommands.Add(down);

var purgeName = new Argument<string>("name") { Description = "sandbox name" };
var purge = new Command("purge", "delete the sandbox AND its volume") { purgeName, yesOpt };
purge.SetAction(async (pr, ct) => await Run(async client =>
{
    var name = pr.GetValue(purgeName)!;
    var sb = (await client.ListAsync(ct)).FirstOrDefault(s => s.Name == name);
    var vol = SandmanClient.VolumeName(name);
    if (!pr.GetValue(yesOpt) && !Confirm($"PERMANENTLY delete {(sb is null ? "" : $"sandbox '{name}' and ")}volume {vol} (workspace, logins, state)?")) return;
    if (sb is not null) { await client.DeleteAsync(sb, ct); Console.WriteLine($"deleted sandbox '{name}'"); }
    Console.WriteLine(await client.DeleteVolumeAsync(name, ct) ? $"deleted volume {vol}" : $"no volume {vol}");
}));
root.Subcommands.Add(purge);

// learned [name]
var learnedName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: the only running one)" };
var learned = new Command("learned", "hosts reached only via the default Allow (candidates for SBX_ALLOW)") { learnedName };
learned.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await Resolve(client, pr.GetValue(learnedName), ct);
    var hosts = await SandmanClient.LearnedAsync(sb, ct);
    if (hosts.Count == 0) { Console.WriteLine("no requests outside the named rules in the decisions log"); return; }
    Console.WriteLine($"{"host",-45} {"hits",5}  {"methods",-12} {"last seen",-9}  sample path");
    foreach (var h in hosts)
        Console.WriteLine($"{h.Host,-45} {h.Hits,5}  {string.Join(",", h.Methods),-12} {h.LastSeen.ToLocalTime():HH:mm:ss}  {h.SamplePath}");
    Console.WriteLine("\nkeep what you need: SBX_ALLOW=host1,host2/path/* on the next `up` (path = GET/HEAD only)");
}));
root.Subcommands.Add(learned);

// allow-vscode [name]
var vsName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "sandbox name (default: the only running one)" };
var allowVsCode = new Command("allow-vscode", "add the VS Code tunnel hosts to a running hardened sandbox (no rebuild)") { vsName, dryRunOpt };
allowVsCode.SetAction(async (pr, ct) => await Run(async client =>
{
    var sb = await Resolve(client, pr.GetValue(vsName), ct);
    var dry = pr.GetValue(dryRunOpt);
    var added = await SandmanClient.AllowVsCodeAsync(sb, SandboxDir.Profile("hardened"), dry ? PrintDiff : null, ct);
    if (added.Count == 0) { Console.WriteLine("VS Code tunnel hosts already allowed"); return; }
    if (dry) return;
    Console.WriteLine($"added: {string.Join(" ", added)}");
    Console.WriteLine($"VS Code tunnel hosts allowed for '{sb.Name}'; next: sbx.sh code {sb.Name}");
}));
root.Subcommands.Add(allowVsCode);

// secrets: github / anthropic sandbox-group secrets for the hardened profiles (egress token injection)
var secrets = new Command("secrets", "set github / anthropic secrets for the hardened profiles");
secrets.SetAction(async (_, ct) => await Run(async client =>
{
    var tok = ReadSecret("GitHub fine-grained token (empty = skip): ");
    if (tok.Length > 0)
    {
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{tok}"));
        await client.SetSecretAsync("github", new Dictionary<string, string> { ["token"] = tok, ["basic"] = basic }, ct);
        Console.WriteLine("set secret github (token, basic)");
    }
    // hardened: `claude setup-token` (subscription); hardened-apikey: Console API key (pay-as-you-go).
    // Both live in the secret "anthropic"; setting it replaces all its keys, so send what was entered together.
    var oauth = ReadSecret("Claude subscription token from 'claude setup-token' (empty = skip): ");
    var key = ReadSecret("Anthropic API key, pay-as-you-go (empty = skip): ");
    var anthropic = new Dictionary<string, string>();
    if (oauth.Length > 0) anthropic["oauth"] = oauth;
    if (key.Length > 0) anthropic["key"] = key;
    if (anthropic.Count > 0)
    {
        await client.SetSecretAsync("anthropic", anthropic, ct);
        Console.WriteLine($"set secret anthropic ({string.Join(", ", anthropic.Keys)}; any other key in it was replaced)");
    }
}));
root.Subcommands.Add(secrets);

return await root.Parse(args).InvokeAsync();

static bool Confirm(string question)
{
    if (Console.IsInputRedirected) throw new SandmanException("refusing to delete without confirmation: pass --yes");
    Console.Write($"{question} [y/N] ");
    return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
}

// No echo. Piped stdin (scripts) reads a line instead.
static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected) { var line = Console.ReadLine() ?? ""; Console.Error.WriteLine(); return line.Trim(); }
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    Console.Error.WriteLine();
    return sb.ToString().Trim();
}

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
    catch (System.Net.WebSockets.WebSocketException e)
    {
        Console.Error.WriteLine($"sbx: exec session failed: {e.Message}");
        return 1;
    }
    catch (OperationCanceledException) { return 130; }
}
