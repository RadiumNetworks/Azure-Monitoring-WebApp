using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SQLAuth.Authentication;
using SQLAuth.Components;
using SQLAuth.Data;

var builder = WebApplication.CreateBuilder(args);
const string CookieScheme = "SqlCookie";

var connectionString = builder.Configuration.GetConnectionString("SqlAuth")
    ?? throw new InvalidOperationException("Connection string 'SqlAuth' is missing.");

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<SqlUserService>();

builder.Services
    .AddAuthentication(CookieScheme)
    .AddCookie(CookieScheme, options =>
    {
        options.Cookie.Name = ".SQLAuth.Learning";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(CookieScheme)
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("Login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.MapPost("/auth/setup", async (
    [FromForm] SetupRequest request,
    SqlUserService users,
    CancellationToken cancellationToken) =>
{
    try
    {
        await users.CreateInitialAdminAsync(request.Username, request.Password, cancellationToken);
        return Results.Redirect("/login?setup=complete");
    }
    catch (InvalidOperationException exception)
    {
        var error = Uri.EscapeDataString(exception.Message);
        return Results.Redirect($"/setup?error={error}");
    }
}).AllowAnonymous();

app.MapPost("/auth/login", async (
    [FromForm] LoginRequest request,
    HttpContext httpContext,
    SqlUserService users,
    CancellationToken cancellationToken) =>
{
    var user = await users.ValidateCredentialsAsync(request.Username, request.Password, cancellationToken);
    if (user is null)
    {
        return Results.Redirect("/login?error=invalid");
    }

    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Username),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        ],
        CookieScheme);
    await httpContext.SignInAsync(
        CookieScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false });
    return Results.Redirect("/");
}).AllowAnonymous().RequireRateLimiting("Login");

app.MapPost("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieScheme);
    return Results.Redirect("/login");
});

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
