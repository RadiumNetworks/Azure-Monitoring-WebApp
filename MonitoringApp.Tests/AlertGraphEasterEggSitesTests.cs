namespace MonitoringApp.Tests;

public sealed class AlertGraphEasterEggSitesTests
{
    public static TheoryData<int, int, string, string[]> CalendarCases => new()
    {
        { 4, 1, "Phantom sites", ["Atlantis", "El Dorado", "Bielefeld", "Area 51"] },
        { 4, 4, "Unreachable sites", ["Null Island", "localhost", "/dev/null", "Site Not Found"] },
        { 5, 4, "Off-world sites", ["Tatooine", "Naboo", "Coruscant", "Alderaan"] },
        { 7, 20, "Lunar sites", ["Tranquility Base", "Fra Mauro", "Taurus-Littrow", "Shackleton Crater"] },
        { 8, 8, "Feline sites", ["Aoshima", "Tashirojima", "Kuching", "Cat Island"] },
        { 9, 19, "Pirate sites", ["Tortuga", "Port Royal", "Nassau", "Ile Sainte-Marie"] },
        { 10, 31, "Haunted sites", ["Transylvania", "Salem", "Sleepy Hollow", "Whitby"] },
        { 12, 31, "New Year sites", ["Kiritimati", "Auckland", "Sydney", "Honolulu"] }
    };

    [Theory]
    [MemberData(nameof(CalendarCases))]
    public void ResolvesExpectedSites(
        int month,
        int day,
        string heading,
        string[] expectedSites)
    {
        var easterEgg = AlertGraphEasterEggSites.Resolve(
            new DateTimeOffset(2025, month, day, 12, 0, 0, TimeSpan.Zero));

        Assert.NotNull(easterEgg);
        Assert.Equal(heading, easterEgg.Heading);
        Assert.Equal(expectedSites, easterEgg.Sites.Select(site => site.Name));
    }

    [Fact]
    public void ActivatesOnExactlyEightDaysInAStandardYear()
    {
        var firstDay = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var activeDays = Enumerable.Range(0, 365)
            .Select(offset => firstDay.AddDays(offset))
            .Where(day => AlertGraphEasterEggSites.Resolve(day) is not null)
            .ToArray();

        Assert.Equal(8, activeDays.Length);
    }

    [Fact]
    public void MarksOnlyAlderaanAndSiteNotFoundAsDanger()
    {
        var dangerousSites = Enumerable.Range(0, 365)
            .Select(offset => new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(offset))
            .Select(AlertGraphEasterEggSites.Resolve)
            .Where(easterEgg => easterEgg is not null)
            .SelectMany(easterEgg => easterEgg!.Sites)
            .Where(site => site.IsDanger)
            .Select(site => site.Name)
            .ToArray();

        Assert.Equal(["Site Not Found", "Alderaan"], dangerousSites);
    }
}
