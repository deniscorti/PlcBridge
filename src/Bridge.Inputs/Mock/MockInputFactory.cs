using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs.Mock;

public sealed class MockInputFactory : IDataInputFactory
{
    public string Protocol => "mock";

    public IDataInput Create(DataSource source, IDataInputConfig config)
    {
        return new MockInput(source);
    }
}
