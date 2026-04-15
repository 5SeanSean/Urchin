using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OllamaSharp;
using Urchin.Data;
using Urchin.Models;
using Urchin.Services;
using Serilog;
using Microsoft.AspNetCore.HttpOverrides;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database context factory (Supabase PostgreSQL)
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";
});

// Application services
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<OllamaApiClient>(sp =>
    new OllamaApiClient(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/urchin-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

var app = builder.Build();

var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);
app.UseStaticFiles();
app.UseAntiforgery();
// Apply migrations and test connections
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();

    var ollama = scope.ServiceProvider.GetRequiredService<OllamaApiClient>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var models = await ollama.ListLocalModelsAsync();
        logger.LogInformation("Ollama connected. Available models: {Models}",
            string.Join(", ", models.Select(m => m.Name)));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Ollama is not reachable at {Url}. AI features will not work.",
            builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    }
}

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ─── Authentication API Endpoints ────────────────────────────────────────────
app.MapPost("/api/auth/register", async (
    HttpContext ctx,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ILogger<Program> logger) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var email    = form["email"].ToString().Trim();
    var password = form["password"].ToString();

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect("/register?error=" + Uri.EscapeDataString("All fields are required."));

    var user   = new ApplicationUser { UserName = username, Email = email };
    var result = await userManager.CreateAsync(user, password);

    if (!result.Succeeded)
    {
        var error = string.Join("; ", result.Errors.Select(e => e.Description));
        return Results.Redirect("/register?error=" + Uri.EscapeDataString(error));
    }

    await signInManager.SignInAsync(user, isPersistent: false);
    logger.LogInformation("User {Username} registered and signed in.", username);
    return Results.Redirect("/");
});

app.MapPost("/api/auth/login", async (
    HttpContext ctx,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ILogger<Program> logger) =>
{
    var form       = await ctx.Request.ReadFormAsync();
    var username   = form["username"].ToString().Trim();
    var password   = form["password"].ToString();
    var rememberMe = form["rememberMe"].ToString() == "true";

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("Username and password are required."));

    var user = await userManager.FindByNameAsync(username);
    if (user == null)
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("Invalid login attempt."));

    var result = await signInManager.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: false);
    if (result.Succeeded)
    {
        logger.LogInformation("User {Username} logged in.", username);
        return Results.Redirect("/");
    }

    return Results.Redirect("/login?error=" + Uri.EscapeDataString("Invalid login attempt."));
});

app.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
}).RequireAuthorization();

// ─── Conversation & Account Deletion Endpoints ────────────────────────────────
app.MapDelete("/api/conversations/{id}", async (
    int id,
    HttpContext ctx,
    UserManager<ApplicationUser> userManager,
    AppDbContext db) =>
{
    var user = await userManager.GetUserAsync(ctx.User);
    if (user is null) return Results.Unauthorized();

    var conv = await db.Conversations
        .Include(c => c.Messages)
        .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id);

    if (conv is null) return Results.NotFound();

    db.Conversations.Remove(conv);
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireAuthorization();

app.MapDelete("/api/account", async (
    HttpContext ctx,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    AppDbContext db) =>
{
    var user = await userManager.GetUserAsync(ctx.User);
    if (user is null) return Results.Unauthorized();

    var convs = await db.Conversations.Where(c => c.UserId == user.Id).ToListAsync();
    db.Conversations.RemoveRange(convs);
    await db.SaveChangesAsync();

    var result = await userManager.DeleteAsync(user);
    if (!result.Succeeded) return Results.BadRequest(result.Errors);

    await signInManager.SignOutAsync();
    return Results.Ok();
}).RequireAuthorization();

app.MapRazorComponents<Urchin.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
