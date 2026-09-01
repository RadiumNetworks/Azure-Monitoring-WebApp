using Microsoft.EntityFrameworkCore;
using EFCoreLearning.Components;
using EFCoreLearning.Data;
using EFCoreLearning.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LearningDatabase")
    ?? "Data Source=efcore-learning.db";
builder.Services.AddDbContextFactory<LearningDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddScoped<LearningRepository>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LearningDbContext>();
    await db.Database.MigrateAsync();
    await DemoDataSeeder.SeedAsync(db);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
