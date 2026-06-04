using Bridge.Core.Model;

namespace Bridge.Core.Tests;

public class DataSourceTests
{
    [Fact]
    public void RegisterTag_AssignsTagId()
    {
        var ds = new DataSource { Id = "test" };
        ds.RegisterTag(new Tag { Name = "temperature", Kind = DataKind.Telemetry });

        var tag = ds.GetTag("temperature");
        Assert.NotNull(tag);
        Assert.Equal(Crc32.Compute("temperature"), tag.TagId);
    }

    [Fact]
    public void RegisterTag_DuplicateName_Throws()
    {
        var ds = new DataSource { Id = "test" };
        ds.RegisterTag(new Tag { Name = "temp", Kind = DataKind.Telemetry });
        Assert.Throws<InvalidOperationException>(() =>
            ds.RegisterTag(new Tag { Name = "temp", Kind = DataKind.Event }));
    }

    [Fact]
    public void MsgId_Increments()
    {
        var ds = new DataSource { Id = "test" };
        var id1 = ds.NextMsgId();
        var id2 = ds.NextMsgId();
        Assert.Equal(1u, id1);
        Assert.Equal(2u, id2);
    }

    [Fact]
    public void SetLastValue_GetLastValue_Roundtrips()
    {
        var ds = new DataSource { Id = "src" };
        ds.RegisterTag(new Tag { Name = "temp", Kind = DataKind.Telemetry });

        var tv = new TagValue
        {
            Source = "src", Tag = "temp", Kind = DataKind.Telemetry,
            Value = 23.5, Timestamp = DateTimeOffset.UtcNow, MsgId = 1
        };
        ds.SetLastValue(tv);

        var last = ds.GetLastValue("temp");
        Assert.NotNull(last);
        Assert.Equal(23.5, last.Value);
    }
}
