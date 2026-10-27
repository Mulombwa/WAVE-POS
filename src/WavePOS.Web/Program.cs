using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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

        // Signed in but not allowed on a page: go to the person's home page (the till for shop
        // staff, the books for accountants…) rather than one they may not be allowed on either.
        options.Events.OnRedirectToAccessDenied = context =>
        {
            var registry = context.HttpContext.RequestServices.GetRequiredService<BusinessRegistry>();
            var home = context.HttpContext.User.ToUser().Role.HomePage(registry.ModulesFor(context.HttpContext.User));
            context.Response.Redirect(context.Request.Path == home ? "/login" : home);
            return Task.CompletedTask;
        };

        // On every request, check the business is active and the account still exists, is active and
        // hasn't changed. Otherwise a deleted/disabled user or a suspended business would stay signed in.
        options.Events.OnValidatePrincipal = async context =>
        {
            var registry = context.HttpContext.RequestServices.GetRequiredService<BusinessRegistry>();
            if (!UserClaims.StillValid(context.Principal, registry))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

// One policy per permission (e.g. "ProcessReturns"): the role must allow it AND its module must be on.
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Enum.GetValues<Permission>())
        options.AddPolicy(permission.ToString(), policy => policy.AddRequirements(new PermissionRequirement(permission)));
});
builder.Services.AddCascadingAuthenticationState();

// Sign-up and sign-in are open to the internet: limit attempts per address.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value?.ToLowerInvariant();
        if (!HttpMethods.IsPost(context.Request.Method) || path is not ("/login" or "/welcome"))
            return RateLimitPartition.GetNoLimiter("other");
        var key = $"{path}:{context.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = path == "/welcome" ? 5 : 20,
            Window = TimeSpan.FromMinutes(10),
        });
    });
});

// Data: a registry of businesses, each with its own SQLite database in its own folder.
// "WavePOS:Database" (or WAVEPOS_DB) points at the single-business database from earlier versions;
// the registry lives in the same folder and adopts that database as the first business.
var dbPath = builder.Configuration["WavePOS:Database"];
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Environment.GetEnvironmentVariable("WAVEPOS_DB");
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "wavepos.db");

var registry = new BusinessRegistry(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
registry.AdoptExisting(dbPath);
builder.Services.AddSingleton(registry);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentBusiness>();
builder.Services.AddScoped(sp => sp.GetRequiredService<CurrentBusiness>().Database);
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<SaleRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SettingsRepository>();
builder.Services.AddScoped<DraftInvoiceRepository>();
builder.Services.AddScoped<AuditRepository>();
builder.Services.AddScoped<InventoryRepository>();
builder.Services.AddScoped<CustomerRepository>();
builder.Services.AddScoped<ReturnRepository>();
builder.Services.AddScoped<LedgerRepository>();
builder.Services.AddScoped<BankRepository>();
builder.Services.AddScoped<PurchasingRepository>();
builder.Services.AddScoped<SalesOrderRepository>();
builder.Services.AddScoped<HrRepository>();
builder.Services.AddScoped<PayrollRepository>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// The signed-in business's logo, or the Wave logo (e.g. on the sign-in page, before we know the business).
app.MapGet("/branding/logo", (HttpContext context, BusinessRegistry businesses) =>
{
    if (businesses.DatabaseFor(context.User) is { } db
        && new SettingsRepository(db).GetBusinessProfile() is { HasCustomLogo: true } profile && File.Exists(profile.LogoPath))
        return Results.File(profile.LogoPath!, LogoUpload.ContentTypeFor(profile.LogoPath!));
    return Results.Redirect("/img/wave-logo.svg");
});

// Product pictures: only for people signed in to the business that owns the product.
app.MapGet("/media/products/{id:long}", (long id, HttpContext context, BusinessRegistry businesses) =>
    businesses.DatabaseFor(context.User) is { } db && new ProductRepository(db).ImagePath(id) is { } path
        ? Results.File(path, LogoUpload.ContentTypeFor(path), enableRangeProcessing: false)
        : Results.NotFound())
    .RequireAuthorization();

app.MapPost("/logout", async (HttpContext context, BusinessRegistry businesses) =>
{
    if (context.User.Identity?.IsAuthenticated == true && businesses.DatabaseFor(context.User) is { } db)
    {
        var user = context.User.ToUser();
        new AuditRepository(db).Write(context.User.ToActor(), WavePOS.Core.Audit.AuditActions.Logout, "user", user.Id.ToString(), "Signed out");
    }
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
