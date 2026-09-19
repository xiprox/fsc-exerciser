namespace FsCopilot.Exerciser;

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

/// <summary>
/// The synced FS Copilot build: where it is, how it loads, and the three types it does not
/// hand out.
///
/// This tool compiles against fsc/FsCopilot.dll and never copies it, so nothing resolves that
/// assembly or its dependencies by default. The resolver below serves both from fsc/, which
/// also keeps them out of the way of this tool's own packages: Avalonia here can move without
/// asking FS Copilot's permission, because the FS Copilot code this tool calls never touches
/// it. Assemblies both sides really do use - System.Reactive, Serilog - are not declared by
/// this project at all, so there is one copy of each and it comes from the synced build.
///
/// FS Copilot loads into the default context, not a private one. It has to: types from a
/// second load context are different types, and every line here that names a PointerEvent
/// would stop compiling against the thing it actually talks to.
/// </summary>
public static class Fsc
{
    /// <summary>This repository's root: the nearest ancestor holding the solution. Found
    /// rather than counted, because how deep the output sits is a build setting.</summary>
    public static string Root { get; } = FindRoot();

    public static string Dir => Path.Combine(Root, "fsc");
    public static string Dll => Path.Combine(Dir, "FsCopilot.dll");
    public static string Exe => Path.Combine(Dir, "FsCopilot.exe");
    public static string RelayExe => Path.Combine(Root, "relay", "p2p_serv.exe");

    private static AssemblyDependencyResolver? _resolver;

    /// <summary>
    /// Installed before Main, and before anything can touch a type from the synced build.
    /// A module initializer rather than a call at the top of Main: the JIT resolves a
    /// method's types when it prepares that method, so a Main that mentioned one would have
    /// needed the resolver a moment before its first line ran.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (!File.Exists(Dll)) return null;
            _resolver ??= new AssemblyDependencyResolver(Dll);

            // The declared graph first, read from FsCopilot.deps.json.
            var path = _resolver.ResolveAssemblyToPath(name);
            // Then the folder itself, for anything the graph does not name - a dependency
            // brought in by a runtime pack, or one the app drops in beside itself.
            if (path == null && name.Name != null)
            {
                var beside = Path.Combine(Dir, name.Name + ".dll");
                if (File.Exists(beside)) path = beside;
            }
            return path == null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        };
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FsCopilot.Exerciser.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
