using System.Reflection;
using System.Runtime.Loader;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BetterHardwareStats.Core.Sensors;

namespace BetterHardwareStats.Windows.Lhm;
internal sealed class BundledLhm : IDisposable
{
    private readonly object _sync = new();
    private LocalContext? _context;
    private Assembly? _bridge;
    private readonly string? _rootDirectory;

    internal BundledLhm(string? directory = null) => _rootDirectory = directory;

    // VRCOSC loads module assemblies from streams, leaving Assembly.Location empty.
    // Match the bridge's build identity without loading another assembly into the host context.
    internal static string ModuleDirectory()
    {
        var assembly = typeof(BundledLhm).Assembly;
        if (!string.IsNullOrEmpty(assembly.Location)) return Path.GetDirectoryName(assembly.Location)!;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCOSC", "packages");
        var candidates = new List<string> { Path.Combine(packages, "local"), AppContext.BaseDirectory };
        var remote = Path.Combine(packages, "remote");
        if (Directory.Exists(remote)) candidates.AddRange(Directory.GetDirectories(remote));
        foreach (var directory in candidates)
        {
            try
            {
                using var file = File.OpenRead(Path.Combine(directory, "BetterHardwareStats.Windows.dll"));
                using var pe = new PEReader(file);
                var metadata = pe.GetMetadataReader();
                if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == assembly.ManifestModule.ModuleVersionId) return directory;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        }
        throw new FileNotFoundException("The module's local sensor bundle directory could not be found.");
    }

    internal static bool IsPresent()
    {
        try { var path = Path.Combine(ModuleDirectory(), "lhm", "LibreHardwareMonitorLib.dll"); return AssemblyName.GetAssemblyName(path).Version == new Version(0, 9, 6, 0); }
        catch { return false; }
    }

    internal ILhmGpuBackend CreateGpu() => (ILhmGpuBackend)Create("BetterHardwareStats.Windows.Lhm.LhmGpuBackend");
    internal ILhmCpuSensors CreateCpu() => (ILhmCpuSensors)Create("BetterHardwareStats.Windows.Lhm.LhmCpuSensors");

    private object Create(string type)
    {
        lock (_sync)
        {
            if (_bridge is null)
            {
                var root = _rootDirectory ?? ModuleDirectory();
                var directory = Path.Combine(root, "lhm");
                var library = Path.Combine(directory, "LibreHardwareMonitorLib.dll");
                if (!File.Exists(library)) throw new FileNotFoundException("The local LHM bundle is missing; host fallback is disabled.", library);
                if (AssemblyName.GetAssemblyName(library).Version != new Version(0, 9, 6, 0))
                    throw new FileLoadException("The local LibreHardwareMonitorLib bundle must be version 0.9.6.", library);
                var context = new LocalContext(directory);
                try
                {
                    context.LoadFromAssemblyPath(library);
                    var bridge = context.LoadFromAssemblyPath(Path.Combine(root, "BetterHardwareStats.Windows.dll"));
                    _context = context;
                    _bridge = bridge;
                }
                catch { context.Unload(); throw; }
            }
            try { return Activator.CreateInstance(_bridge.GetType(type, throwOnError: true)!)!; }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_sync) { _bridge = null; _context?.Unload(); _context = null; }
    }

    private sealed class LocalContext(string directory) : AssemblyLoadContext("Nanashi bundled LHM 0.9.6", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ILhmGpuBackend).Assembly.GetName().Name) return typeof(ILhmGpuBackend).Assembly;
            // Framework types must retain their identities across the boundary.
            if (name.Name is "mscorlib" or "netstandard" or "System.Private.CoreLib" || name.Name?.StartsWith("System.", StringComparison.Ordinal) == true && name.Name is not "System.Management" and not "System.IO.Ports")
                return null;
            var path = Path.Combine(directory, name.Name + ".dll");
            if (!File.Exists(path)) throw new FileNotFoundException("Required local LHM dependency is missing; host fallback is disabled.", path);
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = Path.Combine(directory, Path.GetFileName(name));
            if (File.Exists(path)) return LoadUnmanagedDllFromPath(path);
            // Windows/vendor system libraries remain supplied by the installed OS/driver.
            return nint.Zero;
        }
    }
}
