using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Migrations;

[DbContext(typeof(AlertDbContext))]
[Migration("20261013102000_AddAlertGraphSiteLocations")]
public sealed class AddAlertGraphSiteLocations : Migration
{
    private const string DefaultLocations =
        """[{"DisplayName":"New York","Latitude":40.7128,"Longitude":-74.006,"LabelOffsetX":-30,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["New York","NewYork","NYC"]},{"DisplayName":"Singapore","Latitude":1.3521,"Longitude":103.8198,"LabelOffsetX":30,"LabelOffsetY":-31,"TextAnchor":"start","Aliases":["Singapore","Singapure"]},{"DisplayName":"Frankfurt","Latitude":50.1109,"Longitude":8.6821,"LabelOffsetX":35,"LabelOffsetY":38,"TextAnchor":"start","Aliases":["Frankfurt"]},{"DisplayName":"London","Latitude":51.5074,"Longitude":-0.1278,"LabelOffsetX":-35,"LabelOffsetY":-31,"TextAnchor":"end","Aliases":["London"]}]""";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            $"""
            UPDATE [dbo].[Settings]
            SET [JsonValue] = JSON_MODIFY(
                [JsonValue],
                '$.SiteLocations',
                JSON_QUERY(N'{DefaultLocations}'))
            WHERE [Name] = N'AlertGraph'
              AND ISJSON([JsonValue]) = 1
              AND JSON_QUERY([JsonValue], '$.SiteLocations') IS NULL;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE [dbo].[Settings]
            SET [JsonValue] = JSON_MODIFY([JsonValue], '$.SiteLocations', NULL)
            WHERE [Name] = N'AlertGraph'
              AND ISJSON([JsonValue]) = 1
              AND JSON_QUERY([JsonValue], '$.SiteLocations') IS NOT NULL;
            """);
}
