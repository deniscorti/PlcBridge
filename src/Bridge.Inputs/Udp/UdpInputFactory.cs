using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs.Udp;

public sealed class UdpInputConfig : IDataInputConfig
{
    public string Type => "udp";
    public int ListenPort { get; init; } = 9100;
    public string Protocol { get; init; } = "custom-v1";
    public bool AutoDiscovery { get; init; }
}

public sealed class UdpInputFactory : IDataInputFactory
{
    public string Protocol => "udp";

    public IDataInput Create(DataSource source, IDataInputConfig config)
    {
        var udpCfg = config as UdpInputConfig ?? new UdpInputConfig();
        return new UdpInput(source, udpCfg.ListenPort, udpCfg.Protocol, udpCfg.AutoDiscovery);
    }
}
