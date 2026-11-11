using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Tests;

public class ZwiftDeviceAndButtonsTests
{
    [Theory]
    [InlineData(new byte[] { 0x0A, 0x01 }, ZwiftDeviceKind.ClickV2Right)]
    [InlineData(new byte[] { 0x0B }, ZwiftDeviceKind.ClickV2Left)]
    [InlineData(new byte[] { 0x09 }, ZwiftDeviceKind.ClickV1)]
    [InlineData(new byte[] { 0x42 }, ZwiftDeviceKind.Unknown)]
    [InlineData(new byte[0], ZwiftDeviceKind.Unknown)]
    public void Classify_UsesFirstManufacturerByte(byte[] payload, ZwiftDeviceKind expected)
    {
        Assert.Equal(expected, ZwiftAdvertisement.Classify(payload));
    }

    [Fact]
    public void ClickV2StartCommand_IsRideOnPlusHeader()
    {
        Assert.Equal("RideOn\u0002\u0003"u8.ToArray(), ZwiftConstants.ClickV2StartCommand);
    }

    [Fact]
    public void Tracker_ReportsEdges()
    {
        var tracker = new ButtonStateTracker();

        Assert.Equal((ZwiftButtons.A, ZwiftButtons.None), tracker.Update(ZwiftButtons.A));
        Assert.Equal((ZwiftButtons.None, ZwiftButtons.None), tracker.Update(ZwiftButtons.A));
        Assert.Equal((ZwiftButtons.B, ZwiftButtons.None), tracker.Update(ZwiftButtons.A | ZwiftButtons.B));
        Assert.Equal((ZwiftButtons.None, ZwiftButtons.A | ZwiftButtons.B), tracker.Update(ZwiftButtons.None));
    }

    [Fact]
    public void Describe_ListsButtons()
    {
        Assert.Equal("-", ZwiftButtons.None.Describe());
        Assert.Equal("A+ShiftUpRight", (ZwiftButtons.A | ZwiftButtons.ShiftUpRight).Describe());
    }

    [Fact]
    public void HexFormat()
    {
        Assert.Equal("23 08 FF", Hex.Format([0x23, 0x08, 0xFF]));
        Assert.Equal(string.Empty, Hex.Format([]));
    }
}
