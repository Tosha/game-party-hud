namespace GamePartyHud.Capture;

public sealed record HpCalibration(
    HpRegion Region,
    Hsv FullColor,
    HsvTolerance Tolerance,
    FillDirection Direction);
