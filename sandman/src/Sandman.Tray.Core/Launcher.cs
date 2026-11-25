using System.Diagnostics;
using Sandman.Core;

namespace Sandman.Tray.Core;

/// <summary>Starting things outside the tray: URLs / URIs with their default handler, and sbx in a terminal.</summary>
public static class Launcher
{
    /// <summary>Open a URL or URI (vscode://, https://) with the system's handler.</summary>
    public static void Open(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Open the tunnel (at the workspace folder) in desktop VS Code via its `code` CLI, the same command sbx code
    /// prints; without `code` on PATH, in the browser (vscode.dev).
    /// </summary>
    public static async Task OpenTunnelAsync(string tunnel, string? workspace)
    {
        var folderUri = $"vscode-remote://tunnel+{tunnel}{workspace ?? "/"}";
        try
        {
            // Windows: `code` is code.cmd, so run it through cmd (no console window).
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "code", "--folder-uri", folderUri } }
                : new ProcessStartInfo("code") { ArgumentList = { "--folder-uri", folderUri } };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var p = Process.Start(psi)!;
            // The CLI hands over to the VS Code app and exits; non-zero (cmd: 9009) = no `code` on PATH.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode == 0) return;
        }
        catch (System.ComponentModel.Win32Exception) { /* no `code` */ }
        catch (OperationCanceledException) { return; }   // still running: VS Code is starting
        Open($"https://vscode.dev/tunnel/{tunnel}{workspace}");
    }

    /// <summary>
    /// Run `sbx args...` in a new terminal window, for interactive steps (device-code login). Uses the sbx next to the
    /// tray if there is one (both publish to ~/.sandman/bin), else sbx from PATH.
    /// </summary>
    public static void SbxInTerminal(params string[] args)
    {
        var local = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "sbx.exe" : "sbx");
        var sbx = File.Exists(local) ? local : "sbx";

        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // A new console window (Windows Terminal when it's the default terminal); /k keeps it open to read the output.
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("/k");
            psi.ArgumentList.Add(sbx);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Terminal.app runs a .command file; this one deletes itself.
            var script = Path.Combine(Path.GetTempPath(), $"sandman-{Guid.NewGuid():N}.command");
            File.WriteAllText(script, $"#!/bin/sh\nrm -f \"$0\"\n{Manifest.ShQuote(sbx)} {string.Join(' ', args.Select(Manifest.ShQuote))}\nexec \"${{SHELL:-/bin/sh}}\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("Terminal");
            psi.ArgumentList.Add(script);
        }
        else
        {
            // Debian's alternatives link; most desktops provide it.
            psi = new ProcessStartInfo("x-terminal-emulator") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(sbx);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        try { Process.Start(psi)?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new SandmanException($"couldn't open a terminal; run: sbx {string.Join(' ', args)}");
        }
    }
}
