using ClickBridge.Core.Obc;

namespace ClickBridge.Core.Tests;

public class ObcProtocolTests
{
    [Fact]
    public void ButtonState_EncodesPairs()
    {
        var message = ObcMessages.ButtonState([ObcButtonState.Press(ObcButton.ShiftUp), ObcButtonState.Release(ObcButton.ShiftDown)]);

        Assert.Equal([0x01, 0x01, 0x01, 0x02, 0x00], message);
    }

    [Fact]
    public void ButtonState_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => ObcMessages.ButtonState([]));
    }

    [Theory]
    [InlineData(85, true, new byte[] { 0x02, 0x55, 0x01 })]
    [InlineData(-1, true, new byte[] { 0x02, 0xFF, 0x01 })]
    [InlineData(150, false, new byte[] { 0x02, 0xFF, 0x00 })]
    public void DeviceStatus_Encodes(int battery, bool connected, byte[] expected)
    {
        Assert.Equal(expected, ObcMessages.DeviceStatus(battery, connected));
    }

    [Fact]
    public void AppInfo_MatchesSpecExample()
    {
        // Example from the OpenBikeControl mDNS spec.
        byte[] expected = [0x04, 0x01, 0x05, .. "zwift"u8, 0x06, .. "1.52.0"u8, 0x04, 0x01, 0x02, 0x10, 0x14];

        Assert.Equal(expected, ObcMessages.AppInfo("zwift", "1.52.0", [0x01, 0x02, 0x10, 0x14]));
    }

    [Fact]
    public void Parser_ReadsAppInfo_EvenWhenFragmented()
    {
        var parser = new ObcIncomingParser();
        var data = ObcMessages.AppInfo("mywhoosh", "1.0", [0x01, 0x02]);

        Assert.Empty(parser.Append(data.AsSpan(0, 5)));
        Assert.Empty(parser.Append(data.AsSpan(5, 6)));
        var result = Assert.Single(parser.Append(data.AsSpan(11)));

        var appInfo = Assert.IsType<ObcAppInfo>(result);
        Assert.Equal("mywhoosh", appInfo.AppId);
        Assert.Equal("1.0", appInfo.AppVersion);
        Assert.Equal([0x01, 0x02], appInfo.SupportedButtons);
        Assert.Equal("app='mywhoosh' version='1.0' buttons=ShiftUp,ShiftDown", appInfo.ToString());
    }

    [Fact]
    public void Parser_ReadsBackToBackMessages()
    {
        var parser = new ObcIncomingParser();
        byte[] data = [.. ObcMessages.AppInfo("a", "", []), 0x03, 0x02, 0x14, 0x80];

        var results = parser.Append(data);

        Assert.Equal(2, results.Count);
        Assert.Empty(Assert.IsType<ObcAppInfo>(results[0]).SupportedButtons);
        Assert.Equal(new ObcHaptic(0x02, 0x14, 0x80), results[1]);
    }

    [Fact]
    public void Parser_ReadsMyWhooshHeartbeat()
    {
        var parser = new ObcIncomingParser();

        var results = parser.Append([0x00, 0x00]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.IsType<ObcHeartbeat>(r));
    }

    [Fact]
    public void Parser_ReadsRealMyWhooshAppInfo()
    {
        // Captured from MyWhoosh on Windows (2026-11-11).
        byte[] data =
        [
            0x04, 0x01, 0x08, 0x6D, 0x79, 0x77, 0x68, 0x6F, 0x6F, 0x73, 0x68, 0x05, 0x35, 0x2E, 0x37, 0x2E, 0x30,
            0x0E, 0x01, 0x02, 0x03, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x40,
        ];

        var appInfo = Assert.IsType<ObcAppInfo>(Assert.Single(new ObcIncomingParser().Append(data)));

        Assert.Equal("mywhoosh", appInfo.AppId);
        Assert.Equal("5.7.0", appInfo.AppVersion);
        Assert.Equal(14, appInfo.SupportedButtons.Count);
    }

    [Fact]
    public void Parser_ReportsUnknownBytes()
    {
        var parser = new ObcIncomingParser();

        var result = Assert.Single(parser.Append([0x7F, 0x01]));

        Assert.Equal("7F 01", Assert.IsType<ObcUnknown>(result).ToString());
    }

    [Theory]
    [InlineData("ShiftUp", 0x01)]
    [InlineData("shiftdown", 0x02)]
    [InlineData("0x40", 0x40)]
    [InlineData("20", 20)]
    public void ButtonNames_Parse(string text, byte expected)
    {
        Assert.True(ObcButton.TryParse(text, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("0x00")]
    [InlineData("0xZZ")]
    public void ButtonNames_RejectInvalid(string text)
    {
        Assert.False(ObcButton.TryParse(text, out _));
    }
}
