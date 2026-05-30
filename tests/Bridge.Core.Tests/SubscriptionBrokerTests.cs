using Bridge.Core.Model;
using Bridge.Core.Services;

namespace Bridge.Core.Tests;

public class SubscriptionBrokerTests
{
    [Fact]
    public void Subscribe_ExactTag_MatchesCorrectValue()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", "src1", ["temp"]);

        var value = MakeValue("src1", "temp");
        var subs = broker.GetSubscribers(value);
        Assert.Single(subs);
        Assert.Equal("c1", subs[0]);
    }

    [Fact]
    public void Subscribe_ALL_MatchesAnyTag()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", "src1", ["ALL"]);

        Assert.Single(broker.GetSubscribers(MakeValue("src1", "temp")));
        Assert.Single(broker.GetSubscribers(MakeValue("src1", "pressure")));
        Assert.Empty(broker.GetSubscribers(MakeValue("src2", "temp")));
    }

    [Fact]
    public void Subscribe_NoSource_MatchesAllSources()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", null, ["ALL"]);

        Assert.Single(broker.GetSubscribers(MakeValue("src1", "temp")));
        Assert.Single(broker.GetSubscribers(MakeValue("src2", "pressure")));
    }

    [Fact]
    public void Subscribe_ByKind_MatchesCorrectKind()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", "src1", [], [DataKind.Alarm]);

        Assert.Single(broker.GetSubscribers(MakeValue("src1", "overtemp", DataKind.Alarm)));
        Assert.Empty(broker.GetSubscribers(MakeValue("src1", "temp", DataKind.Telemetry)));
    }

    [Fact]
    public void Unsubscribe_RemovesSubscription()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", "src1", ["temp", "pressure"]);
        broker.Unsubscribe("c1", "src1", ["temp"]);

        Assert.Empty(broker.GetSubscribers(MakeValue("src1", "temp")));
        Assert.Single(broker.GetSubscribers(MakeValue("src1", "pressure")));
    }

    [Fact]
    public void UnsubscribeAll_ClearsEverything()
    {
        var broker = new SubscriptionBroker();
        broker.Subscribe("c1", "src1", ["ALL"]);
        broker.UnsubscribeAll("c1");

        Assert.Empty(broker.GetSubscribers(MakeValue("src1", "temp")));
    }

    private static TagValue MakeValue(string source, string tag, DataKind kind = DataKind.Telemetry)
        => new() { Source = source, Tag = tag, Kind = kind, Value = 1.0, Timestamp = DateTimeOffset.UtcNow, MsgId = 1 };
}
