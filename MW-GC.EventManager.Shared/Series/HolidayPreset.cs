namespace MW_GC.EventManager.Shared.Series;

/// <summary>
/// The yearly dates a Series can repeat on by name (#204). The dates come from the in-house table in
/// <see cref="HolidayPresets"/>, never from an external service. Not to be confused with a Holiday, which
/// is a tag on Activities and has no date.
/// </summary>
public enum HolidayPreset
{
    NewYearsDay,
    MartinLutherKingJrDay,
    ValentinesDay,
    PresidentsDay,
    StPatricksDay,
    EarthDay,
    EasterSunday,
    MothersDay,
    MemorialDay,
    FathersDay,
    IndependenceDay,
    LaborDay,
    ColumbusDay,
    Halloween,
    Thanksgiving,
    ChristmasEve,
    Christmas,
    NewYearsEve,
}
