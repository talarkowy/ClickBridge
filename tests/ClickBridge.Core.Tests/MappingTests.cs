using ClickBridge.Core.Config;
using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Tests;

public class MappingTests
{
    [Fact]
    public void Default_ShiftsWithPlusAndMinus()
    {
        var mapping = ButtonMapping.Default(BridgeMode.Auto);

        Assert.Equal([ObcButton.ShiftUp], mapping.Map(ZwiftButtons.ShiftUpRight));
        Assert.Equal([ObcButton.ShiftDown], mapping.Map(ZwiftButtons.ShiftUpLeft));
        Assert.Equal([ObcButton.Back], mapping.Map(ZwiftButtons.B));
    }

    [Fact]
    public void RightOnly_UsesBForShiftDown()
    {
        var mapping = ButtonMapping.Default(BridgeMode.RightOnly);

        Assert.Equal([ObcButton.ShiftDown], mapping.Map(ZwiftButtons.B));
    }

    [Fact]
    public void Tracker_ReportsOnlyChanges()
    {
        var tracker = new ObcStateTracker(ButtonMapping.Default(BridgeMode.Auto));

        Assert.Equal([ObcButtonState.Press(ObcButton.ShiftUp)], tracker.Update("L", ZwiftButtons.ShiftUpRight));
        Assert.Empty(tracker.Update("L", ZwiftButtons.ShiftUpRight));
        Assert.Equal(
            [ObcButtonState.Release(ObcButton.ShiftUp), ObcButtonState.Press(ObcButton.Select)],
            tracker.Update("L", ZwiftButtons.A));
        Assert.Equal([ObcButtonState.Release(ObcButton.Select)], tracker.Update("L", ZwiftButtons.None));
    }

    [Fact]
    public void Tracker_IgnoresUnmappedButtons()
    {
        var tracker = new ObcStateTracker(ButtonMapping.Default(BridgeMode.Auto));

        Assert.Empty(tracker.Update("L", ZwiftButtons.PowerUpLeft));
    }

    [Fact]
    public void Tracker_MergesControllers_AndReleasesOnRemove()
    {
        var tracker = new ObcStateTracker(ButtonMapping.Default(BridgeMode.Auto));

        tracker.Update("L", ZwiftButtons.ShiftUpRight);
        Assert.Empty(tracker.Update("R", ZwiftButtons.ShiftUpRight)); // already pressed via L
        Assert.Empty(tracker.Update("L", ZwiftButtons.None)); // still held via R
        Assert.Equal([ObcButtonState.Release(ObcButton.ShiftUp)], tracker.Remove("R"));
        Assert.Empty(tracker.Remove("unknown"));
    }

    [Fact]
    public void Config_ParsesAndAppliesOverrides()
    {
        const string json = """
            {
              // comments are allowed
              "deviceName": "Wife's bike",
              "port": 40000,
              "mode": "RightOnly",
              "mapping": { "Left": "SteerLeft", "right": "0x19", "Z": "None" },
            }
            """;

        var config = BridgeConfig.Parse(json);
        var mapping = config.BuildMapping();

        Assert.Equal("Wife's bike", config.DeviceName);
        Assert.Equal(40000, config.Port);
        Assert.Equal(BridgeMode.RightOnly, config.Mode);
        Assert.True(config.Mdns);
        Assert.Equal([ObcButton.SteerLeft], mapping.Map(ZwiftButtons.Left));
        Assert.Equal([ObcButton.SteerRight], mapping.Map(ZwiftButtons.Right));
        Assert.Empty(mapping.Map(ZwiftButtons.Z));
        Assert.Equal([ObcButton.ShiftDown], mapping.Map(ZwiftButtons.B));
    }

    [Theory]
    [InlineData("""{ "mapping": { "Nope": "ShiftUp" } }""")]
    [InlineData("""{ "mapping": { "A": "Nope" } }""")]
    public void Config_RejectsUnknownNames(string json)
    {
        Assert.Throws<FormatException>(() => BridgeConfig.Parse(json).BuildMapping());
    }
}
