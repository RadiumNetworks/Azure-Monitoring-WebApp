using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class LogbookEntryTests
{
    [Fact]
    public void DefaultsTextFieldsToEmptyStrings()
    {
        var entry = new LogbookEntry();

        Assert.Equal(string.Empty, entry.User);
        Assert.Equal(string.Empty, entry.Comment);
    }

    [Fact]
    public void ConfiguresRequiredAuditColumnsAndCreatedAtIndex()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(LogbookEntry)));

        Assert.Equal("LogbookEntries", entity.GetTableName());
        Assert.Equal(256, entity.FindProperty(nameof(LogbookEntry.User))?.GetMaxLength());
        Assert.Equal("nvarchar(max)", entity.FindProperty(nameof(LogbookEntry.Comment))?.GetColumnType());
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(LogbookEntry.CreatedAt)]));
    }
}
