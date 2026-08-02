using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class LogDestinationFactory(IEnumerable<ILogDestination> destinations) : ILogDestinationFactory
{
    private readonly IReadOnlyDictionary<DestinationType, ILogDestination> _destinations = destinations.ToDictionary(item => item.Type);
    public ILogDestination Get(DestinationType type) => _destinations.TryGetValue(type, out var value) ? value : throw new NotSupportedException($"Destination type {type} is not registered.");
}
