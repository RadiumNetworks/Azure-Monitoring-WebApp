using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

public sealed class ApplicationAuthenticationOptionsTests
{
    [Theory]
    [InlineData("open", true, false)]
    [InlineData("OPEN", true, false)]
    [InlineData("sql", false, true)]
    [InlineData("SQL", false, true)]
    public void AcceptsSupportedTypes(string type, bool isOpen, bool isSql)
    {
        var options = new ApplicationAuthenticationOptions { Type = type };

        Assert.Empty(options.Validate());
        Assert.Equal(isOpen, options.IsOpen);
        Assert.Equal(isSql, options.IsSql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("entra")]
    [InlineData("anonymous")]
    [InlineData(null)]
    public void RejectsUnsupportedTypes(string? type)
    {
        var options = new ApplicationAuthenticationOptions { Type = type! };

        Assert.NotEmpty(options.Validate());
    }
}

public sealed class SqlPasswordHasherTests
{
    private readonly SqlPasswordHasher hasher = new();

    [Fact]
    public void HashesAndVerifiesPassword()
    {
        const string password = "correct horse battery staple";

        var hash = hasher.Hash(password);

        Assert.StartsWith("PBKDF2-SHA256$600000$", hash);
        Assert.DoesNotContain(password, hash, StringComparison.Ordinal);
        Assert.True(hasher.Verify(password, hash));
        Assert.False(hasher.Verify("wrong password", hash));
    }

    [Fact]
    public void UsesRandomSaltForEachHash()
    {
        var first = hasher.Hash("same password");
        var second = hasher.Hash("same password");

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify("same password", first));
        Assert.True(hasher.Verify("same password", second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2-SHA256$600000$invalid$invalid")]
    [InlineData("PBKDF2-SHA1$600000$MDEyMzQ1Njc4OUFCQ0RFRg==$xKPYhItL4ZykbLDQKl7QVpmF5O5oYJJq5P2OMfrI8JQ=")]
    [InlineData("PBKDF2-SHA256$1$MDEyMzQ1Njc4OUFCQ0RFRg==$xKPYhItL4ZykbLDQKl7QVpmF5O5oYJJq5P2OMfrI8JQ=")]
    public void RejectsMalformedOrUnsupportedHashes(string hash)
    {
        Assert.False(hasher.Verify("password", hash));
    }
}

public sealed class SqlAuthenticationUserModelTests
{
    [Fact]
    public void UsesExactTwoColumnAuthenticationTable()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(SqlAuthenticationUser)));
        var propertyNames = entity.GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal("AuthenticationUsers", entity.GetTableName());
        Assert.Equal([nameof(SqlAuthenticationUser.PasswordHash), nameof(SqlAuthenticationUser.Username)], propertyNames);
        Assert.Equal([nameof(SqlAuthenticationUser.Username)], entity.FindPrimaryKey()?.Properties.Select(property => property.Name));
        Assert.False(entity.FindProperty(nameof(SqlAuthenticationUser.Username))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(SqlAuthenticationUser.PasswordHash))!.IsNullable);
    }
}
