namespace GameShift.Core.Detection;

/// <summary>
/// GPU generation classification for feature support detection.
/// Used by HAGS toggle redesign to determine Frame Generation capability
/// and HAGS compatibility.
/// </summary>
public enum GpuGeneration
{
    Unknown,
    NvidiaGtx10,
    NvidiaGtx16,
    NvidiaRtx20,
    NvidiaRtx30,
    NvidiaRtx40,
    NvidiaRtx50,
    AmdRdna1,
    AmdRdna2,
    AmdRdna3,
    AmdRdna4,
    IntelArcAlchemist,
    IntelArcBattlemage,
    Other
}
