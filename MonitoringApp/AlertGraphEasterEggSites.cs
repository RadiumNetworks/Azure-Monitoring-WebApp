namespace MonitoringApp;

public static class AlertGraphEasterEggSites
{
    private static readonly IReadOnlyDictionary<(int Month, int Day), AlertGraphEasterEgg> Calendar =
        new Dictionary<(int Month, int Day), AlertGraphEasterEgg>
        {
            [(4, 1)] = new(
                "Phantom sites",
                "April Fools phantom sites",
                "phantom",
                [
                    new("Atlantis", "water", "Existence unconfirmed"),
                    new("El Dorado", "gold", "Location unverified"),
                    new("Bielefeld", "phantom", "Existence disputed"),
                    new("Area 51", "classified", "Access classified")
                ]),
            [(4, 4)] = new(
                "Unreachable sites",
                "HTTP 404 unreachable sites",
                "unreachable",
                [
                    new("Null Island", "water", "404 - Site not found"),
                    new("localhost", "city", "Connection refused"),
                    new("/dev/null", "missing", "Successfully discarded"),
                    new("Site Not Found", "danger", "No route to host", IsDanger: true)
                ]),
            [(5, 4)] = new(
                "Off-world sites",
                "May the Fourth off-world sites",
                "offworld",
                [
                    new("Tatooine", "desert"),
                    new("Naboo", "water"),
                    new("Coruscant", "city"),
                    new("Alderaan", "danger", "Critical", IsDanger: true)
                ]),
            [(7, 20)] = new(
                "Lunar sites",
                "Moon landing anniversary lunar sites",
                "lunar",
                [
                    new("Tranquility Base", "historic", "First crewed lunar landing"),
                    new("Fra Mauro", "moon"),
                    new("Taurus-Littrow", "moon"),
                    new("Shackleton Crater", "moon")
                ]),
            [(8, 8)] = new(
                "Feline sites",
                "International Cat Day sites",
                "feline",
                [
                    new("Aoshima", "cat"),
                    new("Tashirojima", "cat"),
                    new("Kuching", "cat"),
                    new("Cat Island", "cat")
                ]),
            [(9, 19)] = new(
                "Pirate sites",
                "Talk Like a Pirate Day sites",
                "pirate",
                [
                    new("Tortuga", "pirate"),
                    new("Port Royal", "pirate"),
                    new("Nassau", "pirate"),
                    new("Ile Sainte-Marie", "gold", "Treasure reported")
                ]),
            [(10, 31)] = new(
                "Haunted sites",
                "Halloween haunted sites",
                "haunted",
                [
                    new("Transylvania", "spooky"),
                    new("Salem", "spooky"),
                    new("Sleepy Hollow", "pumpkin"),
                    new("Whitby", "spooky")
                ]),
            [(12, 31)] = new(
                "New Year sites",
                "New Year time zone sites",
                "newyear",
                [
                    new("Kiritimati", "gold", "First into the new year"),
                    new("Auckland", "celebration"),
                    new("Sydney", "celebration"),
                    new("Honolulu", "water", "Still counting down")
                ])
        };

    public static AlertGraphEasterEgg? Resolve(DateTimeOffset time) =>
        Calendar.GetValueOrDefault((time.Month, time.Day));
}

public sealed record AlertGraphEasterEgg(
    string Heading,
    string AriaLabel,
    string Theme,
    IReadOnlyList<AlertGraphEasterEggSite> Sites);

public sealed record AlertGraphEasterEggSite(
    string Name,
    string Tone,
    string? Status = null,
    bool IsDanger = false);
