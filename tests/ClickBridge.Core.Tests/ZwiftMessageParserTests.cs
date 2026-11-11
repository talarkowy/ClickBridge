using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Tests;

public class ZwiftMessageParserTests
{
    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (value != 0);
        return [.. bytes];
    }

    private static byte[] ControllerFrame(uint buttonMap, params byte[][] extraFields) =>
        [ZwiftOpcodes.ControllerNotification, 0x08, .. Varint(buttonMap), .. extraFields.SelectMany(f => f)];

    [Fact]
    public void AllBitsSet_MeansNothingPressed()
    {
        // The "released" frame BikeControl uses as a neutral keep-alive.
        var msg = ZwiftMessageParser.Parse([0x23, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);

        var controller = Assert.IsType<ControllerNotification>(msg);
        Assert.True(controller.HasButtonMap);
        Assert.Equal(0xFFFFFFFFu, controller.ButtonMap);
        Assert.Equal(ZwiftButtons.None, controller.Pressed);
    }

    [Theory]
    [InlineData(ZwiftButtons.A)]
    [InlineData(ZwiftButtons.ShiftUpLeft)]
    [InlineData(ZwiftButtons.ShiftUpRight)]
    [InlineData(ZwiftButtons.Up | ZwiftButtons.Left)]
    public void ClearedBits_AreReportedAsPressed(ZwiftButtons buttons)
    {
        var msg = ZwiftMessageParser.Parse(ControllerFrame(~(uint)buttons));

        var controller = Assert.IsType<ControllerNotification>(msg);
        Assert.Equal(buttons, controller.Pressed);
    }

    [Fact]
    public void AnalogPaddles_AreDecodedWithZigZag()
    {
        // field 3 (len-delimited): { location = 1, value = sint32(-100) }
        byte[] paddle = [0x08, 0x01, 0x10, .. Varint(199)]; // zigzag(-100) = 199
        byte[] field3 = [0x1A, (byte)paddle.Length, .. paddle];

        var msg = ZwiftMessageParser.Parse(ControllerFrame(0xFFFFFFFF, field3));

        var controller = Assert.IsType<ControllerNotification>(msg);
        var p = Assert.Single(controller.Paddles);
        Assert.Equal(new AnalogPaddle(1, -100), p);
    }

    [Fact]
    public void UnknownFields_AreSkipped()
    {
        byte[] unknownVarintField = [0x10, 0x05]; // field 2, varint
        byte[] unknownFixed32Field = [0x25, 1, 2, 3, 4]; // field 4, fixed32

        var msg = ZwiftMessageParser.Parse(ControllerFrame(~(uint)ZwiftButtons.B, unknownVarintField, unknownFixed32Field));

        Assert.Equal(ZwiftButtons.B, Assert.IsType<ControllerNotification>(msg).Pressed);
    }

    [Fact]
    public void MissingButtonMap_MeansNothingPressed()
    {
        var msg = ZwiftMessageParser.Parse([ZwiftOpcodes.ControllerNotification]);

        var controller = Assert.IsType<ControllerNotification>(msg);
        Assert.False(controller.HasButtonMap);
        Assert.Equal(ZwiftButtons.None, controller.Pressed);
    }

    [Fact]
    public void TruncatedVarint_IsMalformed()
    {
        var msg = ZwiftMessageParser.Parse([0x23, 0x08, 0xFF, 0xFF]);

        Assert.IsType<MalformedMessage>(msg);
    }

    [Fact]
    public void BatteryNotification_ReadsPercentFromField2()
    {
        var msg = ZwiftMessageParser.Parse([0x19, 0x08, 0x00, 0x10, 0x55]);

        Assert.Equal(85, Assert.IsType<BatteryMessage>(msg).Percent);
    }

    [Fact]
    public void RideOnResponse_SplitsHeaderAndPayload()
    {
        byte[] frame = [.. "RideOn"u8, 0x02, 0x03, 0xAA, 0xBB];

        var msg = ZwiftMessageParser.Parse(frame);

        var rideOn = Assert.IsType<RideOnResponse>(msg);
        Assert.Equal([0x02, 0x03], rideOn.Header);
        Assert.Equal([0xAA, 0xBB], rideOn.Payload);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0x05, 0x00, 0xEA, 0x05 }, true)]
    [InlineData(new byte[] { 0xFF, 0x05, 0x00, 0xFA, 0x05 }, true)]
    [InlineData(new byte[] { 0xFF, 0x01, 0x02 }, false)]
    public void VendorMessage_DetectsStoppedPattern(byte[] frame, bool stopped)
    {
        Assert.Equal(stopped, Assert.IsType<VendorMessage>(ZwiftMessageParser.Parse(frame)).LooksLikeStopped);
    }

    [Fact]
    public void IdleAndUnknownOpcodes()
    {
        Assert.IsType<IdleMessage>(ZwiftMessageParser.Parse([0x15]));
        var other = Assert.IsType<OtherMessage>(ZwiftMessageParser.Parse([0x3C, 0x01]));
        Assert.Equal("GET_RESPONSE", other.Describe());
    }

    [Fact]
    public void EmptyFrame_IsMalformed()
    {
        Assert.IsType<MalformedMessage>(ZwiftMessageParser.Parse([]));
    }
}
