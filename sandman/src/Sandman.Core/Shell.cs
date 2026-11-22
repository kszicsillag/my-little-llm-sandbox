using Microsoft.Azure.ContainerApps.Sandbox.Models;

namespace Sandman.Core;

/// <summary>
/// Interactive PTY sessions (sbx.sh `shell`, and the device-code login in `code`). Terminal-agnostic: the caller
/// supplies raw input/output streams and the current terminal size; putting the local terminal into raw mode
/// is the caller's job (see Sandman.Cli's RawTerminal).
/// </summary>
public static class Shell
{
    /// <summary>Login shell as the sandbox user, starting in its home (sbx.sh: sudo -E -u dev ... bash -l).</summary>
    public static IReadOnlyList<string> LoginShell { get; } = ["bash", "-lc", "cd ~ 2>/dev/null; exec bash -l"];

    public static async Task<int> RunAsync(SbxSandbox sandbox, IReadOnlyList<string> command,
        Stream input, Stream output, Func<(int Width, int Height)> size, CancellationToken ct = default)
    {
        var (w, h) = size();
        // Exec sessions run as root unless told otherwise; running as the user directly (instead of sbx.sh's
        // root shell + sudo) keeps the manifest env and HOME, and logins land in /home/dev.
        var request = new SandboxExecStartRequest(command[0])
        {
            User = SandmanClient.SandboxUser,
            AllocateTerminal = true,
            EnableStandardInput = true,
            Width = (uint)Math.Max(w, 1),
            Height = (uint)Math.Max(h, 1),
        };
        foreach (var a in command.Skip(1)) request.Arguments.Add(a);
        request.Environment["TERM"] = Environment.GetEnvironmentVariable("TERM") is { Length: > 0 } t ? t : "xterm-256color";

        await using var session = await sandbox.Resource.StartSandboxExecSessionAsync(request, cancellationToken: ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Keystrokes → sandbox. A blocked console read can't always be cancelled; it's abandoned when we return.
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var n = await input.ReadAsync(buf, stop.Token);
                    if (n == 0) { await session.CloseInputAsync(stop.Token); break; }
                    await session.SendInputAsync(BinaryData.FromBytes(buf.AsMemory(0, n).ToArray()), stop.Token);
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
            catch (Exception) { /* session gone; the receive loop reports why */ }
        }, CancellationToken.None);

        // Terminal resizes → sandbox (polling works the same on Windows and Unix).
        _ = Task.Run(async () =>
        {
            var last = (w, h);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(250, stop.Token);
                    var now = size();
                    if (now == last || now.Width < 1 || now.Height < 1) continue;
                    last = now;
                    await session.ResizeAsync((uint)now.Height, (uint)now.Width, stop.Token);
                }
            }
            catch (Exception) { }
        }, CancellationToken.None);

        int? exit = null;
        try
        {
            while (true)
            {
                var e = await session.ReceiveAsync(ct);
                if (e is null) break;
                if (e.Type == SandboxExecEventType.StandardOutput || e.Type == SandboxExecEventType.StandardError)
                {
                    var bytes = e.Data is { } d ? d.ToMemory() : System.Text.Encoding.UTF8.GetBytes(e.Text ?? "");
                    await output.WriteAsync(bytes, ct);
                    await output.FlushAsync(ct);
                }
                else if (e.Type == SandboxExecEventType.ExitCode) exit = e.ExitCode;
                else if (e.Type == SandboxExecEventType.Error) throw new SandmanException($"shell session failed: {e.Text ?? e.Data?.ToString()}");
                else if (e.Type == SandboxExecEventType.Closed) break;
            }
        }
        finally { stop.Cancel(); }
        return exit ?? 0;
    }
}
