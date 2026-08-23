namespace DiscImageStudio.Core;

public sealed class DiscModuleCatalog
{
    private readonly IReadOnlyList<IOpticalDiscModule> _modules;
    private readonly Dictionary<string, IOpticalDiscModule> _commands;

    public DiscModuleCatalog(IEnumerable<IOpticalDiscModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        IOpticalDiscModule[] materialized = modules.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one optical-disc module is required.", nameof(modules));
        }

        Dictionary<string, IOpticalDiscModule> commands = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> moduleIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (IOpticalDiscModule module in materialized)
        {
            ArgumentNullException.ThrowIfNull(module);
            if (!moduleIds.Add(module.Descriptor.Id))
            {
                throw new ArgumentException(
                    $"Duplicate optical-disc module id '{module.Descriptor.Id}'.",
                    nameof(modules));
            }

            foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
            {
                if (!commands.TryAdd(command.Name, module))
                {
                    throw new ArgumentException(
                        $"Disc command '{command.Name}' is registered by more than one module.",
                        nameof(modules));
                }
            }
        }

        _modules = materialized;
        _commands = commands;
    }

    public IReadOnlyList<IOpticalDiscModule> Modules => _modules;

    public bool TryResolve(string command, out IOpticalDiscModule? module)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            module = null;
            return false;
        }

        return _commands.TryGetValue(command, out module);
    }

    public IOpticalDiscModule Resolve(string command)
    {
        if (TryResolve(command, out IOpticalDiscModule? module) && module is not null)
        {
            return module;
        }

        throw new ArgumentException($"Unknown optical-disc command '{command}'.", nameof(command));
    }

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.Command).ExecuteAsync(request, progress, cancellationToken);
    }
}
