using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AlertGraphEasterEggSitesTests
{
    public static TheoryData<int, int, string, string[]> CalendarCases => new()
    {
        { 1, 2, "Science fiction sites", ["Robot Outpost", "Orbital Station", "Deep Space Relay", "Unknown Planet"] },
        { 1, 29, "Duke Nukem sites", ["Hollywood", "Los Angeles", "Lunar Base", "Alien Mothership"] },
        { 3, 25, "Middle-earth sites", ["Auenland", "Bruchtal", "Minas Tirith", "Mordor"] },
        { 4, 1, "Phantom sites", ["Atlantis", "El Dorado", "Bielefeld", "Area 51"] },
        { 4, 4, "Unreachable sites", ["Null Island", "localhost", "/dev/null", "Site Not Found"] },
        { 4, 12, "Human spaceflight", ["Baikonur", "Vostok 1", "Low Earth Orbit", "Yuri Gagarin"] },
        { 4, 26, "Alien sites", ["LV-426", "Nostromo", "Hadley's Hope", "Sulaco"] },
        { 5, 4, "Off-world sites", ["Tatooine", "Naboo", "Coruscant", "Alderaan"] },
        { 5, 25, "Hitchhiker sites", ["Earth", "Magrathea", "Milliways", "Heart of Gold"] },
        { 6, 22, "Quake sites", ["Slipgate Complex", "The Necropolis", "Shub-Niggurath's Pit", "Rune Chamber"] },
        { 7, 4, "Independence Day sites", ["Washington, D.C.", "New York", "Los Angeles", "Area 51"] },
        { 7, 20, "Lunar sites", ["Tranquility Base", "Fra Mauro", "Taurus-Littrow", "Shackleton Crater"] },
        { 8, 8, "Feline sites", ["Aoshima", "Tashirojima", "Kuching", "Cat Island"] },
        { 9, 8, "Star Trek sites", ["Vulcan", "Qo'noS", "Risa", "Deep Space Nine"] },
        { 9, 19, "Pirate sites", ["Tortuga", "Port Royal", "Nassau", "Ile Sainte-Marie"] },
        { 10, 5, "Warhammer sites", ["Terra", "Mars", "Cadia", "Macragge"] },
        { 10, 31, "Haunted sites", ["Transylvania", "Salem", "Sleepy Hollow", "Whitby"] },
        { 11, 19, "Half-Life sites", ["Black Mesa", "Xen", "Lambda Complex", "Resonance Cascade"] },
        { 12, 10, "DOOM sites", ["Mars", "Phobos", "Deimos", "Hell Portal"] },
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

    [Theory]
    [InlineData(2025)]
    [InlineData(2028)]
    public void ActivatesOnExactlyTwentyDays(int year)
    {
        var firstDay = new DateTimeOffset(year, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var activeDays = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365)
            .Select(offset => firstDay.AddDays(offset))
            .Where(day => AlertGraphEasterEggSites.Resolve(day) is not null)
            .ToArray();

        Assert.Equal(20, activeDays.Length);
        Assert.Equal(activeDays.Select(date => (date.Month, date.Day)),
            AlertGraphEasterEggSites.Previews.Select(preview => (preview.Month, preview.Day)));
        Assert.Equal(20, AlertGraphEasterEggSites.Previews.Select(preview => preview.Key).Distinct().Count());
    }

    [Fact]
    public void MarksOnlyConfiguredFictionalDangerSites()
    {
        var dangerousSites = Enumerable.Range(0, 365)
            .Select(offset => new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(offset))
            .Select(AlertGraphEasterEggSites.Resolve)
            .Where(easterEgg => easterEgg is not null)
            .SelectMany(easterEgg => easterEgg!.Sites)
            .Where(site => site.IsDanger)
            .Select(site => site.Name)
            .ToArray();

        Assert.Equal(["Site Not Found", "LV-426", "Alderaan", "Cadia"], dangerousSites);
    }

    [Theory]
    [InlineData("science-fiction-day", 1, 2)]
    [InlineData("duke-nukem-day", 1, 29)]
    [InlineData("tolkien-reading-day", 3, 25)]
    [InlineData("april-fools", 4, 1)]
    [InlineData("404-day", 4, 4)]
    [InlineData("human-spaceflight-day", 4, 12)]
    [InlineData("alien-day", 4, 26)]
    [InlineData("star-wars-day", 5, 4)]
    [InlineData("towel-day", 5, 25)]
    [InlineData("quake-day", 6, 22)]
    [InlineData("independence-day", 7, 4)]
    [InlineData("moon-landing", 7, 20)]
    [InlineData("cat-day", 8, 8)]
    [InlineData("star-trek-day", 9, 8)]
    [InlineData("pirates-day", 9, 19)]
    [InlineData("warhammer-day", 10, 5)]
    [InlineData("halloween", 10, 31)]
    [InlineData("half-life-day", 11, 19)]
    [InlineData("doom-day", 12, 10)]
    [InlineData("new-year", 12, 31)]
    public void LocalPreviewsUseExistingCalendarAndAreDisabledOutsideDevelopment(string key, int month, int day)
    {
        Assert.Same(AlertGraphEasterEggSites.Resolve(new(2026, month, day, 12, 0, 0, TimeSpan.Zero)),
            AlertGraphEasterEggSites.ResolvePreview(key, true));
        Assert.Null(AlertGraphEasterEggSites.ResolvePreview(key, false));
    }

    [Fact]
    public void UnknownPreviewHasExplicitErrorLocallyButCannotActivateInProduction()
    {
        Assert.Throws<ArgumentException>(() => AlertGraphEasterEggSites.ResolvePreview("unknown", true));
        Assert.Null(AlertGraphEasterEggSites.ResolvePreview("unknown", false));
        Assert.Null(AlertGraphEasterEggSites.ResolvePreview(null, true));
        Assert.Null(AlertGraphEasterEggSites.ResolvePreview("", true));
    }

    [Theory]
    [MemberData(nameof(CalendarCases))]
    public async Task RendersSymbolsOnCorrectBodyWithoutChangingRealSiteMarkers(
        int month, int day, string heading, string[] expectedSites)
    {
        var egg = AlertGraphEasterEggSites.Resolve(new(2026, month, day, 12, 0, 0, TimeSpan.Zero))!;
        var html = await RenderMapAsync(egg);
        var baseline = await RenderMapAsync(null);
        Assert.Contains(heading, html);
        Assert.Equal(egg.Note is not null, html.Contains("class=\"graph-offworld-note\"", StringComparison.Ordinal));
        if (egg.Note is { } note)
        {
            Assert.Contains(HtmlEncoder.Default.Encode(note), html);
        }
        Assert.All(expectedSites, name => Assert.Contains(HtmlEncoder.Default.Encode(name), html));
        Assert.Equal(egg.Sites.Count(site => site.EarthLocation is not null),
            Count(html, "class=\"graph-earth-easter-site "));
        Assert.Equal(egg.Sites.Count(site => site.MoonPosition is not null),
            Count(html, "class=\"graph-moon-site "));
        Assert.Equal(egg.Sites.Count(site => site.MarsPosition is not null),
            Count(html, "class=\"graph-mars-site "));
        Assert.DoesNotContain("decorative", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approximate", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offset", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("calibrated", html, StringComparison.OrdinalIgnoreCase);
        Assert.All(expectedSites, name => Assert.Contains($"title=\"{HtmlEncoder.Default.Encode(name)}", html));
        Assert.All(egg.Sites.Where(site => site.EarthLocation is not null || site.MoonPosition is not null || site.MarsPosition is not null),
            site => Assert.Contains($"<title>{HtmlEncoder.Default.Encode(site.Name)}", html));
        Assert.Equal(Count(baseline, "class=\"graph-site-marker "),
            Count(html, "class=\"graph-site-marker "));
        Assert.Equal(Count(baseline, "class=\"graph-site-marker graph-site-marker-alert\""),
            Count(html, "class=\"graph-site-marker graph-site-marker-alert\""));
        Assert.Equal(Count(baseline, "class=\"graph-site-marker graph-site-marker-healthy\""),
            Count(html, "class=\"graph-site-marker graph-site-marker-healthy\""));
        Assert.Contains("New York, 1 open alert", html);
        Assert.Contains("Frankfurt, No open alerts", html);
        var hasMoon = egg.Sites.Any(site => site.MoonPosition is not null);
        Assert.Equal(hasMoon, html.Contains("href=\"/moon-easter-egg.png\"", StringComparison.Ordinal));
        if (hasMoon)
        {
            Assert.Contains("has-moon", html);
            Assert.Contains("graph-moon-site-legend", html);
        }
        var hasMars = egg.Sites.Any(site => site.MarsPosition is not null);
        Assert.Equal(egg.Theme == "warhammer", hasMars);
        Assert.Equal(hasMars, html.Contains("href=\"/mars-easter-egg.png\"", StringComparison.Ordinal));
        Assert.Equal(hasMars, html.Contains("class=\"graph-mars-property\"", StringComparison.Ordinal));
        if (hasMars)
        {
            Assert.Contains("has-mars", html);
            Assert.Contains("graph-mars-site-legend", html);
            Assert.False(hasMoon);
        }
        Assert.Equal(egg.Sites.Count(site => site.IsOrbiting), Count(html, "class=\"graph-easter-orbit\""));
        Assert.Equal(egg.Sites.Count(site => site.Tone == "ufo" && site.EarthLocation is not null),
            Count(html, "class=\"graph-easter-hovering\""));
        Assert.Equal(egg.Sites.Count + egg.Sites.Count(site => site.EarthLocation is not null || site.MoonPosition is not null || site.MarsPosition is not null)
            + egg.Sites.Count(site => site.IsOrbiting), Count(html, "class=\"easter-egg-icon\""));
        foreach (var site in egg.Sites)
        {
            if (site.EarthLocation is { } location)
            {
                Assert.InRange(location.Latitude, -90, 90);
                Assert.InRange(location.Longitude, -180, 180);
                var marker = Assert.Single(AlertGraphSiteMap.Build([],
                    [new() { DisplayName = site.Name, Latitude = location.Latitude, Longitude = location.Longitude }],
                    DateTimeOffset.UtcNow));
                Assert.InRange(marker.X + location.OffsetX, 43, AlertGraphSiteMap.Width - 43);
                Assert.InRange(marker.Y + location.OffsetY, 43, AlertGraphSiteMap.Height - 43);
            }
            if (site.MoonPosition is { } position)
            {
                Assert.InRange(position.X, .05, .95);
                Assert.InRange(position.Y, .05, .95);
                Assert.Null(site.EarthLocation);
            }
            if (site.MarsPosition is { } marsPosition)
            {
                Assert.InRange(marsPosition.X, .05, .95);
                Assert.InRange(marsPosition.Y, .05, .95);
                Assert.Null(site.EarthLocation);
                Assert.Null(site.MoonPosition);
                Assert.False(site.IsOrbiting);
            }
        }
    }

    [Fact]
    public async Task AlienDayShowsLv426AsCriticalWithoutMakingItARealSite()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("alien-day", true)!;
        var site = Assert.Single(egg.Sites, site => site.Name == "LV-426");
        Assert.True(site.IsDanger);
        Assert.Equal("Critical", site.Status);
        Assert.Null(site.EarthLocation);
        Assert.Null(site.MoonPosition);
        Assert.False(site.IsOrbiting);
        var html = await RenderMapAsync(egg);
        Assert.Contains("graph-offworld-site-planet graph-offworld-site-danger", html);
        Assert.Contains($"title=\"{HtmlEncoder.Default.Encode("LV-426 · Critical")}\"", html);
    }

    [Theory]
    [InlineData("science-fiction-day", "Orbital Station", "station")]
    [InlineData("duke-nukem-day", "Alien Mothership", "ufo")]
    [InlineData("human-spaceflight-day", "Vostok 1", "capsule")]
    [InlineData("towel-day", "Heart of Gold", "starship")]
    public async Task RendersTheRequestedOrbitingSite(string key, string name, string tone)
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview(key, true)!;
        var site = Assert.Single(egg.Sites, site => site.IsOrbiting);
        Assert.Equal(name, site.Name);
        Assert.Equal(tone, site.Tone);
        Assert.Null(site.EarthLocation);
        Assert.Null(site.MoonPosition);
        var html = await RenderMapAsync(egg);
        Assert.Contains($"aria-label=\"{name} Earth orbit\"", html);
        Assert.Contains($"class=\"graph-orbiting-site graph-easter-marker-{tone}\"", html);
        Assert.Contains("cx=\"1378\" cy=\"699\" rx=\"1250\" ry=\"610\"", html);
        Assert.Contains($"and {egg.Heading}", html);
    }

    [Fact]
    public async Task WarhammerHasAnAdjustableDateNoteAndDecorativeLostCadia()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("warhammer-day", true)!;
        Assert.Same(egg, AlertGraphEasterEggSites.Resolve(new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("5 October is provisional and can be adjusted.", egg.Note);
        var cadia = Assert.Single(egg.Sites, site => site.Name == "Cadia");
        Assert.Equal("Signal lost", cadia.Status);
        Assert.True(cadia.IsDanger);
        Assert.Null(cadia.EarthLocation);
        var html = await RenderMapAsync(egg);
        Assert.Contains("class=\"graph-offworld-note\"", html);
        Assert.Contains("graph-offworld-site-missing graph-offworld-site-danger", html);
    }

    [Fact]
    public void TolkienSitesUseNewZealandFilmingLocationsWithSeparatedSymbols()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("tolkien-reading-day", true)!;
        Assert.Equal(["Hobbiton / Matamata", "Kaitoke Regional Park", "Dry Creek Quarry / Wellington", "Tongariro / Ngauruhoe"],
            egg.Sites.Select(site => site.Status));
        var symbols = egg.Sites.Select(site =>
        {
            var location = Assert.IsType<AlertGraphEasterEggEarthLocation>(site.EarthLocation);
            Assert.InRange(location.Latitude, -42, -37);
            Assert.InRange(location.Longitude, 174, 176);
            var marker = Assert.Single(AlertGraphSiteMap.Build([],
                [new() { DisplayName = site.Name, Latitude = location.Latitude, Longitude = location.Longitude }],
                DateTimeOffset.UtcNow));
            return (X: marker.X + location.OffsetX, Y: marker.Y + location.OffsetY);
        }).ToArray();
        for (var i = 0; i < symbols.Length; i++)
        {
            for (var j = i + 1; j < symbols.Length; j++)
            {
                var dx = symbols[i].X - symbols[j].X;
                var dy = symbols[i].Y - symbols[j].Y;
                Assert.True(dx * dx + dy * dy >= 100 * 100);
            }
        }
    }

    [Theory]
    [InlineData("april-fools", "El Dorado", 4.98, -73.78)]
    [InlineData("duke-nukem-day", "Hollywood", 34.10, -118.33)]
    [InlineData("duke-nukem-day", "Los Angeles", 34.05, -118.24)]
    [InlineData("human-spaceflight-day", "Baikonur", 45.92, 63.34)]
    [InlineData("human-spaceflight-day", "Yuri Gagarin", 55.67, 35.05)]
    [InlineData("towel-day", "Earth", 51.54, -.10)]
    [InlineData("warhammer-day", "Terra", 28.00, 86.90)]
    [InlineData("half-life-day", "Black Mesa", 34.50, -106.00)]
    [InlineData("independence-day", "Washington, D.C.", 38.91, -77.04)]
    [InlineData("independence-day", "New York", 40.71, -74.01)]
    [InlineData("independence-day", "Los Angeles", 34.05, -118.24)]
    [InlineData("independence-day", "Area 51", 37.24, -115.79)]
    public void EarthEventsUseTheRequestedRealLocations(string key, string name, double latitude, double longitude)
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview(key, true)!;
        var site = Assert.Single(egg.Sites, site => site.Name == name);
        var location = Assert.IsType<AlertGraphEasterEggEarthLocation>(site.EarthLocation);
        Assert.Equal(latitude, location.Latitude);
        Assert.Equal(longitude, location.Longitude);
    }

    [Fact]
    public async Task EverySiteToneHasAnImplementedIconRatherThanTheFallbackPin()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> RenderIconAsync(string tone) =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<EasterEggIcon>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EasterEggIcon.Tone)] = tone }));
                return component.ToHtmlString();
            });

        var fallback = await RenderIconAsync("unknown");
        var icons = new Dictionary<string, string>();
        foreach (var preview in AlertGraphEasterEggSites.Previews)
        {
            foreach (var site in AlertGraphEasterEggSites.ResolvePreview(preview.Key, true)!.Sites)
            {
                var html = await RenderIconAsync(site.Tone);
                Assert.NotEqual(fallback, html);
                icons[site.Tone] = html;
            }
        }
        Assert.Equal(4, new[] { "hobbit-door", "leaf", "white-city", "volcano" }
            .Select(tone => icons[tone]).Distinct().Count());
        Assert.Equal(6, new[] { "sunglasses", "portal", "rune", "lab", "helmet", "gear" }
            .Select(tone => icons[tone]).Distinct().Count());
    }

    [Fact]
    public void AtlantisHasEightDistinctOpenOceanChoicesWithoutChangingFixedSites()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("april-fools", true)!;
        var atlantis = Assert.Single(egg.Sites, site => site.EarthLocationOptions is not null);
        Assert.Equal("Atlantis", atlantis.Name);
        Assert.Null(atlantis.EarthLocation);
        Assert.Null(atlantis.MoonPosition);
        Assert.Null(atlantis.MarsPosition);
        Assert.False(atlantis.IsOrbiting);
        var options = atlantis.EarthLocationOptions!;
        Assert.Equal(new AlertGraphEasterEggEarthLocation[]
        {
            new(35, -40), new(-25, -20), new(15, -145), new(-25, -125),
            new(-25, 75), new(-40, 95), new(-50, -110), new(5, -30)
        }, options);
        Assert.Equal(8, options.Distinct().Count());
        Assert.Equal(3, egg.Sites.Count(site => site.EarthLocation is not null));
        foreach (var location in options)
        {
            var marker = Assert.Single(AlertGraphSiteMap.Build([],
                [new() { DisplayName = "Atlantis", Latitude = location.Latitude, Longitude = location.Longitude }],
                DateTimeOffset.UtcNow));
            Assert.InRange(marker.X, 43, AlertGraphSiteMap.Width - 43);
            Assert.InRange(marker.Y, 43, AlertGraphSiteMap.Height - 43);
            Assert.Equal(0, location.OffsetX);
            Assert.Equal(0, location.OffsetY);
        }
    }

    [Fact]
    public async Task StaticPrerenderDefersAtlantisSelectionUntilTheMapIsInteractive()
    {
        var html = await RenderMapAsync(AlertGraphEasterEggSites.ResolvePreview("april-fools", true));
        Assert.Contains("Atlantis", html);
        Assert.Equal(3, Count(html, "class=\"graph-earth-easter-site "));
        Assert.DoesNotContain("aria-label=\"Atlantis", html);
        Assert.Contains("New York, 1 open alert", html);
    }

    [Fact]
    public async Task DukeLunarBaseUsesTheMoonRenderWithAStationSymbol()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("duke-nukem-day", true)!;
        var site = Assert.Single(egg.Sites, site => site.MoonPosition is not null);
        Assert.Equal("Lunar Base", site.Name);
        Assert.Equal("station", site.Tone);
        Assert.Equal(new AlertGraphEasterEggMoonPosition(.60, .55), site.MoonPosition);
        Assert.Null(site.EarthLocation);
        Assert.Null(site.MarsPosition);
        Assert.False(site.IsOrbiting);
        var html = await RenderMapAsync(egg);
        Assert.Contains("href=\"/moon-easter-egg.png\"", html);
        Assert.Contains("class=\"graph-moon-site graph-easter-marker-station\"", html);
        Assert.Contains("transform=\"translate(478.8 422.4)\"", html);
        Assert.DoesNotContain("graph-mars-panel", html);
        Assert.Contains("Alien Mothership Earth orbit", html);
    }

    [Theory]
    [InlineData("duke-nukem-day", "Moon", 1)]
    [InlineData("moon-landing", "Moon landing anniversary", 4)]
    public async Task MoonPanelHeadingAndAccessibleCountMatchTheSelectedEvent(string key, string heading, int markers)
    {
        var html = await RenderMapAsync(AlertGraphEasterEggSites.ResolvePreview(key, true)!);
        Assert.Contains($"<h3 id=\"graph-moon-heading\">{heading}</h3>", html);
        Assert.Contains($"aria-label=\"Moon with {markers} lunar site {(markers == 1 ? "marker" : "markers")}\"", html);
        Assert.Equal(markers, Count(html, "class=\"graph-moon-site "));
        Assert.Contains("graph-moon-site-legend", html);
    }

    [Fact]
    public async Task WarhammerMarsPlacesTheGearOnOlympusMonsWithTheRequestedPropertyOverlay()
    {
        var egg = AlertGraphEasterEggSites.ResolvePreview("warhammer-day", true)!;
        var site = Assert.Single(egg.Sites, site => site.MarsPosition is not null);
        Assert.Equal("Mars", site.Name);
        Assert.Equal("gear", site.Tone);
        Assert.Equal("Olympus Mons", site.Status);
        Assert.Equal(new AlertGraphEasterEggMarsPosition(.334, .452), site.MarsPosition);
        var html = await RenderMapAsync(egg);
        Assert.Contains("viewBox=\"0 0 530 513\"", html);
        Assert.Contains("href=\"/mars-easter-egg.png\" width=\"530\" height=\"513\"", html);
        Assert.Contains("class=\"graph-mars-site graph-easter-marker-gear\"", html);
        Assert.Contains("transform=\"translate(177.02 231.876)\"", html);
        Assert.Contains("Property of Fabricator-General of Mars, Adeptus Mechanicus", html);
        Assert.DoesNotContain("graph-moon-panel", html);
    }

    [Fact]
    public async Task OrdinaryDayHasNoDecorativeSitesOrAdditionalPlanets()
    {
        var html = await RenderMapAsync(null);
        Assert.DoesNotContain("graph-earth-easter-site", html);
        Assert.DoesNotContain("graph-moon-panel", html);
        Assert.DoesNotContain("graph-mars-panel", html);
        Assert.DoesNotContain("mars-easter-egg.png", html);
        Assert.DoesNotContain("Fabricator-General", html);
        Assert.DoesNotContain("graph-offworld-sites", html);
        Assert.Contains("World map showing alert status for 4 configured sites", html);
    }

    private static int Count(string html, string value) =>
        html.Split(value, StringSplitOptions.None).Length - 1;

    private static async Task<string> RenderMapAsync(AlertGraphEasterEgg? egg)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<GraphSiteMap>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(GraphSiteMap.Records)] = new AlertGraphRecord[]
                    {
                        new(Guid.NewGuid(), now, "real-alert", "Fired", "", "Real outage", "sub",
                            "rg", "DC01", "New York", "example.org", "Domain Controller")
                    },
                    [nameof(GraphSiteMap.Locations)] = AlertGraphSiteLocationOptions.CreateDefaults(),
                    [nameof(GraphSiteMap.TimeUtc)] = now,
                    [nameof(GraphSiteMap.EasterEgg)] = egg
                }));
            return component.ToHtmlString();
        });
    }
}
