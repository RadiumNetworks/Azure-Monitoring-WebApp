using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class AnalysisTablePagingTests
{
    [Fact]
    public async Task RendersOnly50RowsAndNavigatesWithoutLosingStatusFormattingOrAllocatingColumnsPerCell()
    {
        var columns = new CountingColumns(
        [
            new("Computer", "string"), new("State", "string"), new("State__Status", "string")
        ]);
        var table = new AnalysisQueryTable("Production", columns,
            Enumerable.Range(1, 1000).Select(index => (IReadOnlyList<string?>)
                new string?[] { $"ROW_{index:0000}_END", "Stopped", "Error" }).ToArray());
        var activator = new TableActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Tables"] = new AnalysisQueryTable[] { table },
            ["Name"] = "Netlogon errors"
        });
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AnalysisTableView>(parameters));
        var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Rows 1-50 of 1000", html);
        Assert.Contains("ROW_0001_END", html);
        Assert.Contains("ROW_0050_END", html);
        Assert.DoesNotContain("ROW_0051_END", html);
        Assert.Equal(50, html.Split("analysis-status-error", StringSplitOptions.None).Length - 1);
        Assert.InRange(columns.Enumerations, 1, 2);

        await ChangePageAsync(1);
        html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Rows 51-100 of 1000", html);
        Assert.Contains("ROW_0051_END", html);
        Assert.DoesNotContain("ROW_0001_END", html);
        await ChangePageAsync(1000);
        html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Rows 951-1000 of 1000", html);
        Assert.Contains("ROW_1000_END", html);
        Assert.DoesNotContain("ROW_0950_END", html);
        Assert.Equal(50, html.Split("analysis-status-error", StringSplitOptions.None).Length - 1);

        await renderer.Dispatcher.InvokeAsync(() => activator.Table!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Tables"] = new AnalysisQueryTable[] { table with { Rows = table.Rows.Take(51).ToArray() } }
            })));
        html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Rows 1-50 of 51", html);
        await ChangePageAsync(1);
        html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        Assert.Contains("Rows 51-51 of 51", html);
        Assert.Equal(1, html.Split("analysis-status-error", StringSplitOptions.None).Length - 1);
        await ChangePageAsync(-1000);
        Assert.Contains("Rows 1-50 of 51", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Equal(1000, table.Rows.Count);

        Task ChangePageAsync(int direction) => renderer.Dispatcher.InvokeAsync(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AnalysisTableView).GetMethod("ChangePage", flags)!.Invoke(activator.Table, [0, direction]);
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(activator.Table, []);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task SmallTablesNeedNoPagination(int count)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AnalysisTableView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Tables"] = new AnalysisQueryTable[]
                {
                    new("Production", [new("Value", "string")],
                        Enumerable.Range(0, count).Select(index => (IReadOnlyList<string?>)new[] { index.ToString() }).ToArray())
                }
            }))).ToHtmlString());
        Assert.DoesNotContain("analysis-table-pagination", html);
    }

    private sealed class TableActivator : IComponentActivator
    {
        public AnalysisTableView? Table { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AnalysisTableView table)
                Table = table;
            return component;
        }
    }

    private sealed class CountingColumns(AnalysisQueryColumn[] columns) : IReadOnlyList<AnalysisQueryColumn>
    {
        public int Enumerations { get; private set; }
        public int Count => columns.Length;
        public AnalysisQueryColumn this[int index] => columns[index];
        public IEnumerator<AnalysisQueryColumn> GetEnumerator()
        {
            Enumerations++;
            return ((IEnumerable<AnalysisQueryColumn>)columns).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
