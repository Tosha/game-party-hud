namespace GamePartyHud.Capture;

public sealed record HpCalibration(
    CaptureRegion Region,
    Hsv FullColor,
    HsvTolerance Tolerance,
    FillDirection Direction);
