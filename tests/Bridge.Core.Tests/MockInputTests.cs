using Bridge.Core.Model;
using Bridge.Inputs.Mock;

namespace Bridge.Core.Tests;

public class MockInputTests
{
    [Fact]
    public async Task MockInput_ProducesValues()
    {
        var ds = new DataSource { Id = "test" };
        ds.RegisterTag(new Tag { Name = "temperature", Kind = DataKind.Telemetry, PollMs = 100 });

        await using var input = new MockInput(ds);
        var values = new List<TagValue>();
        input.OnValue += v => values.Add(v);

        await input.StartAsync();
        await Task.Delay(350);
        await input.StopAsync();

        Assert.NotEmpty(values);
        Assert.All(values, v =>
        {
            Assert.Equal("test", v.Source);
            Assert.Equal("temperature", v.Tag);
            Assert.Equal(DataKind.Telemetry, v.Kind);
        });
    }

    [Fact]
    public async Task MockInput_ReadTag_ReturnsValue()
    {
        var ds = new DataSource { Id = "test" };
        ds.RegisterTag(new Tag { Name = "temp", Kind = DataKind.Telemetry });

        await using var input = new MockInput(ds);
        var value = await input.ReadTagAsync("temp");
        Assert.IsType<double>(value);
    }

    [Fact]
    public async Task MockInput_ReadTag_UnknownTag_Throws()
    {
        var ds = new DataSource { Id = "test" };
        await using var input = new MockInput(ds);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => input.ReadTagAsync("nonexistent"));
    }
}
