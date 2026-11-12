using ClickBridge.Ble;
using ClickBridge.Core.Logging;
using ClickBridge.Core.Zwift;
using System.Globalization;

namespace ClickBridge.Diagnostics;

/// <summary>Logs every frame of one controller and keeps statistics for the periodic summary.</summary>
internal sealed class DiagMonitor
{
    private const int EmptyFramesWarning = 3;
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromSeconds(2);

    private readonly IBridgeLog _log;
    private readonly ButtonStateTracker _tracker = new();
    private readonly Lock _lock = new();

    private DateTime? _readySince;
    private DateTime? _lastFrameAt;
    private DateTime? _lastButtonAt;
    private int _frames;
    private int _idleFrames;
    private int _buttonEvents;
    private int _emptySinceLastButton;
    private int _reconnects;
    private bool _wasReady;

    public DiagMonitor(ZwiftController controller, IBridgeLog log)
    {
        Controller = controller;
        _log = log;
        controller.FrameReceived += OnFrame;
        controller.ReadyChanged += OnReadyChanged;
    }

    public ZwiftController Controller { get; }

    public string Summary()
    {
        var c = Controller;
        var state = c.IsReady ? "READY" : c.ConnectionStatus?.ToString() ?? "not connected";
        lock (_lock)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{state,-12} fw={c.Firmware ?? "?"} battery={(c.Battery < 0 ? "?" : c.Battery + "%")} " +
                $"readyFor={Age(_readySince)} frames={_frames} (idle {_idleFrames}) buttonEvents={_buttonEvents} " +
                $"lastFrame={Age(_lastFrameAt)} ago lastButton={Age(_lastButtonAt)} ago emptySinceButton={_emptySinceLastButton} " +
                $"reconnects={_reconnects} rideOnAnswered={c.HandshakeAnswered} serviceHidden={c.CustomServiceHidden?.ToString() ?? "?"}");
        }
    }

    private void OnReadyChanged(ZwiftController controller, bool ready)
    {
        lock (_lock)
        {
            if (ready)
            {
                if (_wasReady)
                {
                    _reconnects++;
                }

                _wasReady = true;
                _readySince = DateTime.Now;
                _tracker.Reset();
                _log.Event(controller.Tag, "Press buttons on this controller.");
            }
            else
            {
                _log.Warn(controller.Tag, $"Not ready. Last frame {Age(_lastFrameAt)} ago, last button {Age(_lastButtonAt)} ago.");
            }
        }
    }

    private void OnFrame(ZwiftController controller, string channel, ZwiftMessage message)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            _lastFrameAt = now;
            _frames++;

            var line = $"{channel,-6} {Hex.Format(message.Raw)} | {message.Describe()}{DecodeBody(message)}";
            switch (message)
            {
                case IdleMessage:
                    _idleFrames++;
                    _log.Verbose(controller.Tag, line);
                    break;
                case ControllerNotification notification:
                    var (pressed, released) = _tracker.Update(notification.Pressed);
                    if (pressed == ZwiftButtons.None && released == ZwiftButtons.None)
                    {
                        // After every press the controller repeats the "released" frame for ~1 s, so only empty
                        // frames arriving well after the last real press count as possibly masked presses.
                        if (notification.Pressed == ZwiftButtons.None
                            && now - (_lastButtonAt ?? DateTime.MinValue) > ReleaseTail
                            && ++_emptySinceLastButton == EmptyFramesWarning)
                        {
                            _log.Warn(controller.Tag, $"Empty controller frames {ReleaseTail.TotalSeconds:0}+ s after the last real button event -> presses are probably being masked (locked?).");
                        }

                        _log.Verbose(controller.Tag, line);
                        break;
                    }

                    _buttonEvents++;
                    _emptySinceLastButton = 0;
                    _lastButtonAt = now;
                    _log.Event(controller.Tag, $"{line}  >> down: {pressed.Describe()}  up: {released.Describe()}");
                    break;
                case VendorMessage { LooksLikeStopped: true }:
                case MalformedMessage:
                    _log.Warn(controller.Tag, line);
                    break;
                default:
                    _log.Info(controller.Tag, line);
                    break;
            }
        }
    }

    /// <summary>Schema-less decode of messages we do not understand yet (LOG_DATA, vendor messages, ...).</summary>
    private static string DecodeBody(ZwiftMessage message) => message switch
    {
        // Vendor messages carry a 2-byte header (e.g. "03 00") before the protobuf payload.
        UnlockChallenge challenge => " " + ProtoDump.Format(challenge.Payload.Span),
        VendorMessage { Raw.Length: > 3 } v => $" hdr={Hex.Format(v.Raw.AsSpan(1, 2))} {ProtoDump.Format(v.Raw.AsSpan(3))}",
        OtherMessage { Raw.Length: > 1 } o => " " + ProtoDump.Format(o.Raw.AsSpan(1)),
        _ => string.Empty,
    };

    private static string Age(DateTime? since) => since is { } t
        ? (DateTime.Now - t).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
        : "never";
}
