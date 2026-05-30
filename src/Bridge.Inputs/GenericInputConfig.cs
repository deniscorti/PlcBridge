using Bridge.Core.Abstractions;

namespace Bridge.Inputs;

/// <summary>
/// Generic config for inputs that don't need protocol-specific settings (e.g. Mock).
/// </summary>
public sealed class GenericInputConfig(string type) : IDataInputConfig
{
    public string Type { get; } = type;
}
