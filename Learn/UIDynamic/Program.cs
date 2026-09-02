using Microsoft.EntityFrameworkCore;
using UIDynamic.Components;
using UIDynamic.Data;
using UIDynamic.Services;

var builder = WebApplication.CreateBuilder(args);

var databaseDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(databaseDirectory);
var connectionString = builder.Configuration.GetConnectionString("LayoutDatabase")
    ?? $"Data Source={Path.Combine(databaseDirectory, "ui-dynamic.db")}";

// Add services to the container.
builder.Services.AddDbContextFactory<LayoutDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<DashboardDesignerState>();
builder.Services.AddScoped<LayoutRepository>();
builder.Services.AddScoped<DashboardContentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LayoutDbContext>>();
    await using var db = await contextFactory.CreateDbContextAsync();
    await DashboardDatabaseInitializer.InitializeAsync(db, builder.Environment.ContentRootPath);
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
