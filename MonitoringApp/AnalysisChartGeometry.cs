using System.Globalization;

namespace MonitoringApp;

public sealed record AnalysisChartTick(string Position, string Label, string? DateLabel = null);

public sealed record AnalysisChartGeometry(
    double Maximum,
    IReadOnlyList<string> Lines,
    IReadOnlyList<AnalysisChartTick> ValueTicks,
    IReadOnlyList<AnalysisChartTick> TimeTicks)
{
    public static AnalysisChartGeometry Create(AnalysisQueryResponse response)
    {
        var maximumValue = response.Series.SelectMany(series => series.Points)
            .Select(point => point.Value).DefaultIfEmpty(0).Max();
        var maximum = maximumValue > 0 ? maximumValue * 1.1 : 1;
        var duration = Math.Max((response.To - response.From).TotalSeconds, 1);
        var lines = response.Series.Select(series => string.Join(" ",
            series.Points.OrderBy(point => point.Timestamp).Select(point =>
            {
                var x = 100 + (point.Timestamp - response.From).TotalSeconds / duration * 875;
                var y = 305 - point.Value / maximum * 270;
                return FormattableString.Invariant($"{x:0.##},{y:0.##}");
            }))).ToArray();
        var values = Enumerable.Range(0, 6).Select(index => new AnalysisChartTick(
            (305 - index * 54).ToString(CultureInfo.InvariantCulture),
            (maximum * index / 5).ToString("G4", CultureInfo.InvariantCulture))).ToArray();
        var times = Enumerable.Range(0, 6).Select(index =>
        {
            var timestamp = response.From.AddTicks((response.To - response.From).Ticks / 5 * index)
                .ToUniversalTime();
            return new AnalysisChartTick(
                (100 + index * 175).ToString(CultureInfo.InvariantCulture),
                timestamp.ToString("HH:mm", CultureInfo.InvariantCulture),
                timestamp.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        }).ToArray();
        return new(maximum, lines, values, times);
    }
}
