namespace Claudette.App.Mascot;

/// <summary>What she wears, by the user's local date and time (DESIGN.md §5, "Claudette on the composer").</summary>
public static class MascotCalendar
{
    /// <summary>A witch's hat in Halloween's last week, Santa's from December 1st to Boxing Day, a nightcap late at night.</summary>
    public static string? HatAt(DateTimeOffset local) => local switch
    {
        { Month: 10, Day: >= 24 } => "witch",
        { Month: 12, Day: <= 26 } => "santa",
        _ when IsNight(local) => "nightcap",
        _ => null,
    };

    /// <summary>From 11 at night to 5 in the morning: she's sleepier, and yawns more.</summary>
    public static bool IsNight(DateTimeOffset local) => local.Hour is >= 23 or < 5;
}
