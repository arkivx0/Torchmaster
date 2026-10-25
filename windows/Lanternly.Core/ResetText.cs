namespace Lanternly.Core;

/// <summary>Factory reset steps for Cync / C by GE bulbs (from GE's support article), shown in the app.</summary>
public static class ResetText
{
    public const string Intro =
        "Bulbs already paired with the Cync app must be factory reset once before Lanternly can add them. A reset removes the bulb from its old account and mesh, and it also loses its current color and brightness; set them again in Lanternly right after adding the bulb. " +
        "Careful: switching a bulb off and on quickly several times can reset it by accident. " +
        "Use a plain on/off switch or a switched plug/power strip (not a dimmer or 3-way lamp). A reset worked when the bulb blinks (usually 3 times).";

    public static readonly (string Title, string Steps)[] Methods =
    [
        ("Current firmware (try first)", "Start with the bulb OFF. Turn ON 1 s, OFF 1 s. Do this 5 times. The bulb flashes when reset."),
        ("Older bulbs, method A", "OFF 5 s. Then ON 8 s, OFF 2 s; repeat 5 times. Turn ON: it flashes 3 times."),
        ("Older bulbs, method B (firmware 2.7 or earlier)", "OFF 5 s. ON 8 s. Then OFF/ON at 2 s intervals 3 times. Then OFF 2 s, ON 8 s; twice. Then OFF 2 s and ON: it flashes 3 times."),
    ];
}
