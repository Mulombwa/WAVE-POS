using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WavePOS.Core.Security;
using WavePOS.Data;
using WavePOS.Web.Components;
using WavePOS.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Sign-in: an HTTP-only cookie, renewed while the till is in use.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "WavePOS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;

        // On every request, check the signed-in account still exists, is active and hasn't changed.
        // Otherwise a deleted/disabled user (or a cookie from another database) would stay signed in.
        options.Events.OnValidatePrincipal = async context =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
            if (!UserClaims.StillValid(context.Principal, users))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
// One policy per permission (e.g. "ProcessReturns"), allowing the roles defined in Core's Permissions.
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Enum.GetValues<Permission>())
        options.AddPolicy(permission.ToString(), policy =>
            policy.RequireRole(Permissions.RolesFor(permission).Select(r => r.ToString())));
});
builder.Services.AddCascadingAuthenticationState();

// Database: same SQLite schema as the desktop app.
// Set "WavePOS:Database" in appsettings.json or the WAVEPOS_DB environment variable to choose the file.
var dbPath = builder.Configuration["WavePOS:Database"];
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Environment.GetEnvironmentVariable("WAVEPOS_DB");
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "wavepos.db");

var db = new PosDatabase(dbPath);
db.Initialize();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<SaleRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<DraftInvoiceRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<InventoryRepository>();
builder.Services.AddSingleton<CustomerRepository>();
builder.Services.AddSingleton<ReturnRepository>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// The business's logo (or the Wave logo). Public, because the sign-in page shows it.
app.MapGet("/branding/logo", (SettingsRepository settings) =>
{
    var profile = settings.GetBusinessProfile();
    if (profile.HasCustomLogo && File.Exists(profile.LogoPath))
        return Results.File(profile.LogoPath!, LogoUpload.ContentTypeFor(profile.LogoPath!));
    return Results.Redirect("/img/wave-logo.svg");
});

app.MapPost("/logout", async (HttpContext context, AuditRepository audit) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var user = context.User.ToUser();
        audit.Write(context.User.ToActor(), WavePOS.Core.Audit.AuditActions.Logout, "user", user.Id.ToString(), "Signed out");
    }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
