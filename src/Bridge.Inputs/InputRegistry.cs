using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs;

/// <summary>
/// Registry for data input factories. Provides a single point to resolve
/// any input protocol by name. New protocols (Modbus, OPC-UA, etc.) register
/// their factory here.
/// </summary>
public sealed class InputRegistry
{
    private readonly Dictionary<string, IDataInputFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a factory for a protocol.</summary>
    public void Register(IDataInputFactory factory)
    {
        _factories[factory.Protocol] = factory;
    }

    /// <summary>Get all registered protocol names.</summary>
    public IReadOnlyCollection<string> RegisteredProtocols => _factories.Keys.ToList();

    /// <summary>Create an input instance by protocol name.</summary>
    public IDataInput Create(string protocol, DataSource source, IDataInputConfig config)
    {
        if (!_factories.TryGetValue(protocol, out var factory))
            throw new InvalidOperationException(
                $"No input factory registered for protocol '{protocol}'. " +
                $"Available: [{string.Join(", ", _factories.Keys)}]");

        return factory.Create(source, config);
    }

    /// <summary>Check if a protocol is registered.</summary>
    public bool HasProtocol(string protocol) => _factories.ContainsKey(protocol);
}
