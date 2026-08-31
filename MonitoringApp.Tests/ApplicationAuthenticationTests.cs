using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

internal static class AuthenticationTestData
{
    public static readonly AuthenticationTestCases Cases =
        TestCaseLoader.Load<AuthenticationTestCases>("application-authentication.json");

    public static IEnumerable<object[]> SupportedTypes =>
        Cases.SupportedTypes.Select(testCase => new object[] { testCase });

    public static IEnumerable<object?[]> UnsupportedTypes =>
        Cases.UnsupportedTypes.Select(type => new object?[] { type });

    public static IEnumerable<object[]> MalformedHashes =>
        Cases.MalformedHashes.Select(hash => new object[] { hash });

    public static IEnumerable<object[]> SupportedRoles =>
        Cases.SupportedRoles.Select(testCase => new object[] { testCase });

    public static IEnumerable<object[]> UnsupportedRoles =>
        Cases.UnsupportedRoles.Select(role => new object[] { role });
}

public sealed class ApplicationAuthenticationOptionsTests
{
    [Theory]
    [MemberData(nameof(AuthenticationTestData.SupportedTypes), MemberType = typeof(AuthenticationTestData))]
    public void AcceptsSupportedTypes(AuthenticationTypeCase testCase)
    {
        var options = new ApplicationAuthenticationOptions { Type = testCase.Type };

        Assert.Empty(options.Validate());
        Assert.Equal(testCase.IsOpen, options.IsOpen);
        Assert.Equal(testCase.IsSql, options.IsSql);
    }

    [Theory]
    [MemberData(nameof(AuthenticationTestData.UnsupportedTypes), MemberType = typeof(AuthenticationTestData))]
    public void RejectsUnsupportedTypes(string? type)
    {
        var options = new ApplicationAuthenticationOptions { Type = type! };

        Assert.NotEmpty(options.Validate());
    }
}

public sealed class ApplicationAuthenticationTests
{
    [Fact]
    public void DefaultsToSqlAuthentication()
    {
        var options = new ApplicationAuthenticationOptions();

        Assert.True(options.IsSql);
        Assert.False(options.IsOpen);
        Assert.Empty(options.Validate());
    }
}

public sealed class SqlPasswordHasherTests
{
    private readonly SqlPasswordHasher hasher = new();

    [Fact]
    public void HashesAndVerifiesPassword()
    {
        var password = AuthenticationTestData.Cases.Password;

        var hash = hasher.Hash(password);

        Assert.StartsWith("PBKDF2-SHA256$600000$", hash);
        Assert.DoesNotContain(password, hash, StringComparison.Ordinal);
        Assert.True(hasher.Verify(password, hash));
        Assert.False(hasher.Verify(AuthenticationTestData.Cases.WrongPassword, hash));
    }

    [Fact]
    public void UsesRandomSaltForEachHash()
    {
        var password = AuthenticationTestData.Cases.Password;
        var first = hasher.Hash(password);
        var second = hasher.Hash(password);

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify(password, first));
        Assert.True(hasher.Verify(password, second));
    }

    [Theory]
    [MemberData(nameof(AuthenticationTestData.MalformedHashes), MemberType = typeof(AuthenticationTestData))]
    public void RejectsMalformedOrUnsupportedHashes(string hash)
    {
        Assert.False(hasher.Verify(AuthenticationTestData.Cases.Password, hash));
    }
}

public sealed class SqlAuthenticationUserModelTests
{
    [Fact]
    public void UsesAuthenticationTableWithRoleColumn()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
            .Options;
        using var context = new AlertDbContext(options);

        var entity = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(SqlAuthenticationUser)));
        var propertyNames = entity.GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal("AuthenticationUsers", entity.GetTableName());
        Assert.Equal(
            [nameof(SqlAuthenticationUser.PasswordHash), nameof(SqlAuthenticationUser.Role), nameof(SqlAuthenticationUser.Username)],
            propertyNames);
        Assert.Equal([nameof(SqlAuthenticationUser.Username)], entity.FindPrimaryKey()?.Properties.Select(property => property.Name));
        Assert.False(entity.FindProperty(nameof(SqlAuthenticationUser.Username))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(SqlAuthenticationUser.PasswordHash))!.IsNullable);
        var roleProperty = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IProperty>(
            entity.FindProperty(nameof(SqlAuthenticationUser.Role)));
        Assert.False(roleProperty.IsNullable);
        Assert.Equal(16, roleProperty.GetMaxLength());
    }
}

public sealed class SqlAuthenticationRoleTests
{
    [Theory]
    [MemberData(nameof(AuthenticationTestData.SupportedRoles), MemberType = typeof(AuthenticationTestData))]
    public void NormalizesSupportedRoles(AuthenticationRoleCase testCase)
    {
        Assert.Equal(testCase.Expected, SqlAuthenticationRoles.Normalize(testCase.Input));
    }

    [Theory]
    [MemberData(nameof(AuthenticationTestData.UnsupportedRoles), MemberType = typeof(AuthenticationTestData))]
    public void RejectsUnsupportedRoles(string input)
    {
        Assert.Throws<InvalidOperationException>(() => SqlAuthenticationRoles.Normalize(input));
    }
}
