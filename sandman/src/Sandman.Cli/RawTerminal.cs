using System.Diagnostics;
using System.Runtime.InteropServices;
using Sandman.Core;

namespace Sandman.Cli;

/// <summary>
/// Puts the local terminal into raw mode for an interactive remote shell, and restores it on Dispose
/// (also on Ctrl-C-free exits and unhandled exceptions, via ProcessExit).
/// Windows: console modes (VT input/output, no line editing, no echo, Ctrl-C as input) + UTF-8 code pages.
/// Linux/macOS: `stty raw -echo`, restored from `stty -g`.
/// </summary>
internal sealed partial class RawTerminal : IDisposable
{
    private readonly Action _restore;
    private int _restored;

    private RawTerminal(Action restore)
    {
        _restore = restore;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public static RawTerminal Enter()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new SandmanException("shell needs an interactive terminal (stdin/stdout are redirected); use `sbx exec` for scripts");
        return OperatingSystem.IsWindows() ? EnterWindows() : EnterUnix();
    }

    public static (int Width, int Height) Size()
    {
        try { return (Console.WindowWidth, Console.WindowHeight); }
        catch { return (80, 24); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _restored, 1) == 0) _restore();
    }

    // ---- Unix ----

    private static RawTerminal EnterUnix()
    {
        var saved = Stty("-g", capture: true).Trim();
        Stty("raw -echo");
        return new RawTerminal(() => Stty(saved));
    }

    private static string Stty(string args, bool capture = false)
    {
        // stdin is inherited (not redirected), so stty acts on our terminal.
        var psi = new ProcessStartInfo("stty") { UseShellExecute = false, RedirectStandardOutput = capture };
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new SandmanException("can't run stty");
        var output = capture ? p.StandardOutput.ReadToEnd() : "";
        p.WaitForExit();
        if (p.ExitCode != 0) throw new SandmanException($"stty {args} failed ({p.ExitCode})");
        return output;
    }

    // ---- Windows ----

    private const int StdInput = -10, StdOutput = -11;
    private const uint EnableProcessedInput = 0x1, EnableLineInput = 0x2, EnableEchoInput = 0x4,
        EnableVirtualTerminalInput = 0x200, EnableVirtualTerminalProcessing = 0x4, DisableNewlineAutoReturn = 0x8;

    private static RawTerminal EnterWindows()
    {
        var hin = GetStdHandle(StdInput);
        var hout = GetStdHandle(StdOutput);
        if (!GetConsoleMode(hin, out var inMode) || !GetConsoleMode(hout, out var outMode))
            throw new SandmanException("not a Windows console (GetConsoleMode failed)");
        var inCp = GetConsoleCP();
        var outCp = GetConsoleOutputCP();

        // Raw keystrokes as VT sequences; Ctrl-C becomes a byte for the remote side instead of killing sbx.
        SetConsoleMode(hin, (inMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput);
        SetConsoleMode(hout, outMode | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
        SetConsoleCP(65001);
        SetConsoleOutputCP(65001);

        return new RawTerminal(() =>
        {
            SetConsoleMode(hin, inMode);
            SetConsoleMode(hout, outMode);
            SetConsoleCP(inCp);
            SetConsoleOutputCP(outCp);
        });
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCP(uint wCodePageID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint wCodePageID);
}
