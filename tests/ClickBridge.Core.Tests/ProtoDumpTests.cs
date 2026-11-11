using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Tests;

public class ProtoDumpTests
{
    [Fact]
    public void LogData_BatteryVoltage_IsReadable()
    {
        // Body of a real LOG_DATA frame from a Click v2 (opcode 0x2A stripped).
        byte[] body = [0x08, 0x02, 0x12, 0x15, 0x22, 0x13, .. "Batt. Volt. 2910 mV"u8];

        Assert.Equal("{1=2, 2={4=\"Batt. Volt. 2910 mV\"}}", ProtoDump.Format(body));
    }

    [Fact]
    public void LogData_Nested_IsDecoded()
    {
        byte[] body = [0x08, 0x03, 0x12, 0x13, 0x12, 0x11, 0x08, 0x06, 0x10, 0x00, 0x18, 0xD8, 0x04,
            0x22, 0x05, 0xFF, 0xFF, 0xFF, 0xFF, 0x1F, 0x28, 0xFB, 0x01];

        Assert.Equal("{1=3, 2={2={1=6, 2=0, 3=600, 4=[5B FF FF FF FF 1F], 5=251}}}", ProtoDump.Format(body));
    }

    [Fact]
    public void BinaryBlob_FallsBackToHex()
    {
        // Field 1 holding a 3-byte value that starts with an invalid tag (field 0).
        byte[] body = [0x0A, 0x03, 0x02, 0xF5, 0xAF];

        Assert.Equal("{1=[3B 02 F5 AF]}", ProtoDump.Format(body));
    }

    [Fact]
    public void NotProtobuf_IsHex()
    {
        Assert.Equal("03 00", ProtoDump.Format([0x03, 0x00]));
    }
}
