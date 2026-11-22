namespace Sandman.Core;

/// <summary>
/// Locates the repo's `.sandbox/` folder (profiles *.yaml, image/): the single source of truth that
/// sbx.sh also reads. SBX_HOME overrides; otherwise search upward from the current directory, then from
/// the executable's directory (dotnet run / a published binary inside the repo).
/// </summary>
public static class SandboxDir
{
    public static string Find()
    {
        if (Environment.GetEnvironmentVariable("SBX_HOME") is { Length: > 0 } env)
            return IsSandboxDir(env) ? Path.GetFullPath(env)
                : throw new SandmanException($"SBX_HOME='{env}' is not a .sandbox folder (no hardened.yaml)");

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".sandbox");
                if (IsSandboxDir(candidate)) return candidate;
            }

        throw new SandmanException("can't find the .sandbox folder (run inside the repo, or set SBX_HOME=<repo>/.sandbox)");
    }

    public static string Profile(string name) => Path.Combine(Find(), $"{name}.yaml");

    private static bool IsSandboxDir(string path) => File.Exists(Path.Combine(path, "hardened.yaml"));
}
