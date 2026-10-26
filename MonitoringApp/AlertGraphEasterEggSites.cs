namespace MonitoringApp;

public static class AlertGraphEasterEggSites
{
    private static readonly IReadOnlyDictionary<(int Month, int Day), AlertGraphEasterEgg> Calendar =
        new Dictionary<(int Month, int Day), AlertGraphEasterEgg>
        {
            [(1, 2)] = new(
                "Science fiction sites",
                "National Science Fiction Day sites",
                "science-fiction",
                [
                    new("Robot Outpost", "robot"),
                    new("Orbital Station", "station", IsOrbiting: true),
                    new("Deep Space Relay", "relay"),
                    new("Unknown Planet", "planet")
                ]),
            [(1, 29)] = new(
                "Duke Nukem sites",
                "Duke Nukem 3D release anniversary sites",
                "duke",
                [
                    new("Hollywood", "sunglasses", EarthLocation: new(34.10, -118.33, -100, -100)),
                    new("Los Angeles", "ufo", EarthLocation: new(34.05, -118.24, 80, 80)),
                    new("Lunar Base", "station", MoonPosition: new(.60, .55)),
                    new("Alien Mothership", "ufo", IsOrbiting: true)
                ]),
            [(3, 25)] = new(
                "Middle-earth sites",
                "Tolkien Reading Day sites",
                "tolkien",
                [
                    new("Auenland", "hobbit-door", "Hobbiton / Matamata", EarthLocation: new(-37.86, 175.68, -160, -110)),
                    new("Bruchtal", "leaf", "Kaitoke Regional Park", EarthLocation: new(-41.07, 175.20, -160, 80)),
                    new("Minas Tirith", "white-city", "Dry Creek Quarry / Wellington", EarthLocation: new(-41.16, 174.97, -30, 130)),
                    new("Mordor", "volcano", "Tongariro / Ngauruhoe", EarthLocation: new(-39.16, 175.63, -30, -80))
                ]),
            [(4, 1)] = new(
                "Phantom sites",
                "April Fools phantom sites",
                "phantom",
                [
                    new("Atlantis", "water", "Existence unconfirmed", EarthLocationOptions:
                    [
                        new(35, -40),
                        new(-25, -20),
                        new(15, -145),
                        new(-25, -125),
                        new(-25, 75),
                        new(-40, 95),
                        new(-50, -110),
                        new(5, -30)
                    ]),
                    new("El Dorado", "gold", "Guatavita / Colombia", EarthLocation: new(4.98, -73.78)),
                    new("Bielefeld", "phantom", "Existence disputed", EarthLocation: new(52.03, 8.53)),
                    new("Area 51", "classified", "Access classified", EarthLocation: new(37.24, -115.79))
                ]),
            [(4, 4)] = new(
                "Unreachable sites",
                "HTTP 404 unreachable sites",
                "unreachable",
                [
                    new("Null Island", "water", "404 - Site not found", EarthLocation: new(0, 0)),
                    new("localhost", "city", "Connection refused"),
                    new("/dev/null", "missing", "Successfully discarded"),
                    new("Site Not Found", "danger", "No route to host", IsDanger: true)
                ]),
            [(4, 12)] = new(
                "Human spaceflight",
                "International Day of Human Space Flight",
                "spaceflight",
                [
                    new("Baikonur", "rocket", "12 April 1961", EarthLocation: new(45.92, 63.34)),
                    new("Vostok 1", "capsule", IsOrbiting: true),
                    new("Low Earth Orbit", "orbit"),
                    new("Yuri Gagarin", "astronaut", "Birthplace: Klushino", EarthLocation: new(55.67, 35.05))
                ]),
            [(4, 26)] = new(
                "Alien sites",
                "Alien Day LV-426 sites",
                "alien",
                [
                    new("LV-426", "planet", "Critical", IsDanger: true),
                    new("Nostromo", "starship"),
                    new("Hadley's Hope", "alien"),
                    new("Sulaco", "starship")
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
            [(5, 25)] = new(
                "Hitchhiker sites",
                "Towel Day sites",
                "towel",
                [
                    new("Earth", "towel", "Islington / London", EarthLocation: new(51.54, -.10, 130, -130)),
                    new("Magrathea", "answer"),
                    new("Milliways", "planet"),
                    new("Heart of Gold", "starship", IsOrbiting: true)
                ]),
            [(6, 22)] = new(
                "Quake sites",
                "Quake release anniversary sites",
                "quake",
                [
                    new("Slipgate Complex", "portal"),
                    new("The Necropolis", "city"),
                    new("Shub-Niggurath's Pit", "portal"),
                    new("Rune Chamber", "rune")
                ]),
            [(7, 4)] = new(
                "Independence Day sites",
                "Independence Day alien visitors",
                "independence",
                [
                    new("Washington, D.C.", "ufo", EarthLocation: new(38.91, -77.04, -120, 60)),
                    new("New York", "ufo", EarthLocation: new(40.71, -74.01, 45, -160)),
                    new("Los Angeles", "ufo", EarthLocation: new(34.05, -118.24, -55, 85)),
                    new("Area 51", "classified", EarthLocation: new(37.24, -115.79, 65, -95))
                ]),
            [(7, 20)] = new(
                "Lunar sites",
                "Moon landing anniversary lunar sites",
                "lunar",
                [
                    new("Tranquility Base", "historic", "First crewed lunar landing", MoonPosition: new(.685, .48)),
                    new("Fra Mauro", "moon", MoonPosition: new(.375, .525)),
                    new("Taurus-Littrow", "moon", MoonPosition: new(.725, .325)),
                    new("Shackleton Crater", "moon", MoonPosition: new(.51, .945))
                ]),
            [(8, 8)] = new(
                "Feline sites",
                "International Cat Day sites",
                "feline",
                [
                    new("Aoshima", "cat", EarthLocation: new(33.74, 132.48, -50, 70)),
                    new("Tashirojima", "cat", EarthLocation: new(38.30, 141.42, 50, -70)),
                    new("Kuching", "cat", EarthLocation: new(1.55, 110.36)),
                    new("Cat Island", "cat", "Bahamas", EarthLocation: new(24.30, -75.42))
                ]),
            [(9, 8)] = new(
                "Star Trek sites",
                "Star Trek Day sites",
                "star-trek",
                [
                    new("Vulcan", "planet"),
                    new("Qo'noS", "relay"),
                    new("Risa", "sun"),
                    new("Deep Space Nine", "station")
                ]),
            [(9, 19)] = new(
                "Pirate sites",
                "Talk Like a Pirate Day sites",
                "pirate",
                [
                    new("Tortuga", "pirate", EarthLocation: new(20.05, -72.79, 110, -20)),
                    new("Port Royal", "pirate", EarthLocation: new(17.94, -76.84, -50, 115)),
                    new("Nassau", "pirate", EarthLocation: new(25.06, -77.34, -80, -95)),
                    new("Ile Sainte-Marie", "gold", "Treasure reported", EarthLocation: new(-16.99, 49.85))
                ]),
            // Provisional Warhammer Day; adjust the date, preview metadata and note when needed.
            [(10, 5)] = new(
                "Warhammer sites",
                "Warhammer Day sites",
                "warhammer",
                [
                    new("Terra", "helmet", "Himalaya", EarthLocation: new(28.00, 86.90)),
                    new("Mars", "gear", "Olympus Mons", MarsPosition: new(.334, .452)),
                    new("Cadia", "missing", "Signal lost", IsDanger: true),
                    new("Macragge", "station")
                ],
                Note: "5 October is provisional and can be adjusted."),
            [(10, 31)] = new(
                "Haunted sites",
                "Halloween haunted sites",
                "haunted",
                [
                    new("Transylvania", "spooky", "Bran area", EarthLocation: new(45.52, 25.37)),
                    new("Salem", "spooky", EarthLocation: new(42.52, -70.90, -70, -110)),
                    new("Sleepy Hollow", "pumpkin", EarthLocation: new(41.09, -73.86, -110, 55)),
                    new("Whitby", "spooky", EarthLocation: new(54.49, -.61))
                ]),
            [(11, 19)] = new(
                "Half-Life sites",
                "Half-Life release anniversary sites",
                "half-life",
                [
                    new("Black Mesa", "lab", "New Mexico setting", EarthLocation: new(34.50, -106.00)),
                    new("Xen", "portal"),
                    new("Lambda Complex", "lab"),
                    new("Resonance Cascade", "relay")
                ]),
            [(12, 10)] = new(
                "DOOM sites",
                "DOOM release anniversary sites",
                "doom",
                [
                    new("Mars", "planet"),
                    new("Phobos", "station", "UAC station"),
                    new("Deimos", "planet"),
                    new("Hell Portal", "portal")
                ]),
            [(12, 31)] = new(
                "New Year sites",
                "New Year time zone sites",
                "newyear",
                [
                    new("Kiritimati", "gold", "First into the new year", EarthLocation: new(1.87, -157.43)),
                    new("Auckland", "celebration", EarthLocation: new(-36.85, 174.76)),
                    new("Sydney", "celebration", EarthLocation: new(-33.87, 151.21)),
                    new("Honolulu", "water", "Still counting down", EarthLocation: new(21.31, -157.86))
                ])
        };

    public static readonly IReadOnlyList<AlertGraphEasterEggPreview> Previews =
    [
        new("science-fiction-day", "Science Fiction Day", 1, 2),
        new("duke-nukem-day", "Duke Nukem Day", 1, 29),
        new("tolkien-reading-day", "Tolkien Reading Day", 3, 25),
        new("april-fools", "April Fools", 4, 1),
        new("404-day", "404 Day", 4, 4),
        new("human-spaceflight-day", "Human Spaceflight", 4, 12),
        new("alien-day", "Alien Day", 4, 26),
        new("star-wars-day", "May the Fourth", 5, 4),
        new("towel-day", "Towel Day", 5, 25),
        new("quake-day", "Quake Day", 6, 22),
        new("independence-day", "Independence Day", 7, 4),
        new("moon-landing", "Moon landing", 7, 20),
        new("cat-day", "Cat Day", 8, 8),
        new("star-trek-day", "Star Trek Day", 9, 8),
        new("pirates-day", "Pirates' Day", 9, 19),
        new("warhammer-day", "Warhammer Day", 10, 5),
        new("halloween", "Halloween", 10, 31),
        new("half-life-day", "Half-Life Day", 11, 19),
        new("doom-day", "DOOM Day", 12, 10),
        new("new-year", "New Year's Eve", 12, 31)
    ];

    public static AlertGraphEasterEgg? Resolve(DateTimeOffset time) =>
        Calendar.GetValueOrDefault((time.Month, time.Day));

    public static AlertGraphEasterEgg? ResolvePreview(string? name, bool isDevelopment)
    {
        if (!isDevelopment || string.IsNullOrEmpty(name))
            return null;
        var preview = Previews.SingleOrDefault(preview => preview.Key == name)
            ?? throw new ArgumentException("Unknown Easter-egg preview. Choose one of the preview links.");
        return Calendar[(preview.Month, preview.Day)];
    }
}

public sealed record AlertGraphEasterEggPreview(string Key, string Label, int Month, int Day);

public sealed record AlertGraphEasterEgg(
    string Heading,
    string AriaLabel,
    string Theme,
    IReadOnlyList<AlertGraphEasterEggSite> Sites,
    string? Note = null);

public sealed record AlertGraphEasterEggSite(
    string Name,
    string Tone,
    string? Status = null,
    bool IsDanger = false,
    AlertGraphEasterEggEarthLocation? EarthLocation = null,
    AlertGraphEasterEggMoonPosition? MoonPosition = null,
    bool IsOrbiting = false,
    AlertGraphEasterEggMarsPosition? MarsPosition = null,
    IReadOnlyList<AlertGraphEasterEggEarthLocation>? EarthLocationOptions = null);

public sealed record AlertGraphEasterEggEarthLocation(
    double Latitude, double Longitude, double OffsetX = 0, double OffsetY = 0);

// Relative image positions are decorative estimates for the supplied Moon render.
public sealed record AlertGraphEasterEggMoonPosition(double X, double Y);

public sealed record AlertGraphEasterEggMarsPosition(double X, double Y);
