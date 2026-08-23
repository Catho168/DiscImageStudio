using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

DiscModuleCatalog catalog = new(
[
    new CdDiscModule(),
    new DvdDiscModule(),
    new FutureBluRayModule(),
]);

Equal(3, catalog.Modules.Count, "module count");
Equal(OpticalDiscFamily.CompactDisc, catalog.Resolve("cd-generate").Descriptor.Family, "CD routing");
Equal(OpticalDiscFamily.Dvd, catalog.Resolve("solve").Descriptor.Family, "DVD routing");
Equal(OpticalDiscFamily.BluRay, catalog.Resolve("bd-generate").Descriptor.Family, "future Blu-ray routing");
True(
    catalog.Resolve("cd-generate").Descriptor.SupportsCancellation,
    "CD module cancellation metadata");
True(
    !catalog.Resolve("solve").Descriptor.SupportsCancellation,
    "DVD module cancellation metadata");

DiscJobResult bluRayResult = await catalog.ExecuteAsync(
    new DiscJobRequest("bd-generate", ["--output", "future.iso"]));
True(bluRayResult.Succeeded, "future module execution");

Throws<ArgumentException>(
    () => new DiscModuleCatalog([new FutureBluRayModule(), new ConflictingModule()]),
    "duplicate command rejection");
Throws<ArgumentException>(() => catalog.Resolve("unknown-command"), "unknown command rejection");

Console.WriteLine("architecture-selftest: all checks passed");
return;

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{name}: expected {expected}, actual {actual}.");
    }
}

static void True(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}: condition was false.");
    }
}

static void Throws<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}.");
}

sealed class FutureBluRayModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "bluray-future-test",
        "Future Blu-ray module",
        OpticalDiscFamily.BluRay,
        "Proves that a Blu-ray implementation can join the catalog without changing CD or DVD.",
        SupportsCancellation: true,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Generate Blu-ray image",
                "Test-only future command.",
                DiscModuleCapabilities.DataImageGeneration,
                [new("output", "Output image", DiscOptionValueType.OutputFile, Required: true)]),
        ]);

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DiscJobResult(
            Descriptor.Id,
            request.Command,
            0,
            "Future Blu-ray module test completed.",
            "future.iso"));
    }
}

sealed class ConflictingModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "conflict-test",
        "Conflicting module",
        OpticalDiscFamily.BluRay,
        "Test-only duplicate command.",
        SupportsCancellation: false,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Conflict",
                "Duplicate command.",
                DiscModuleCapabilities.None,
                []),
        ]);

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
