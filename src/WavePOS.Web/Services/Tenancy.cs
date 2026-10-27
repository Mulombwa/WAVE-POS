using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using WavePOS.Core.Security;
using WavePOS.Data;

namespace WavePOS.Web.Services;

/// <summary>
/// The business the signed-in person belongs to, for this request or this open page. Every
/// repository is built on <see cref="Database"/>, so pages only ever read and write that business's
/// own database. With nobody signed in there is no business, and asking for its database fails
/// rather than falling back to anyone else's.
/// </summary>
public sealed class CurrentBusiness(BusinessRegistry registry, IHttpContextAccessor http, AuthenticationStateProvider auth)
{
    private long? _id;
    private bool _resolved;

    public Business? Business
    {
        get
        {
            if (!_resolved)
            {
                _resolved = true;
                _id = BusinessClaims.BusinessId(Principal());
            }
            return _id is { } id ? registry.Get(id) is { IsActive: true } b ? b : null : null;
        }
    }

    public PosDatabase Database =>
        Business is { } b ? registry.Database(b) : throw new InvalidOperationException("Nobody is signed in to a business.");

    /// <summary>Always the latest, so switching a module on or off applies straight away.</summary>
    public ModuleSet Modules => Business?.Modules ?? ModuleSet.ForSize(BusinessSize.Informal);

    private ClaimsPrincipal? Principal()
    {
        // Pages (static and interactive) get the signed-in person from the authentication state;
        // plain endpoints such as /logout only have the HTTP request.
        try
        {
            var state = auth.GetAuthenticationStateAsync();
            if (state.IsCompletedSuccessfully && state.Result.User.Identity?.IsAuthenticated == true) return state.Result.User;
        }
        catch (InvalidOperationException) { /* not set for this request */ }
        return http.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;
    }
}

public static class BusinessClaims
{
    public const string BusinessClaim = "wavepos:business";

    public static long? BusinessId(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.FindFirstValue(BusinessClaim), out var id) ? id : null;

    /// <summary>The business's database for a signed-in principal, or null (e.g. in plain endpoints).</summary>
    public static PosDatabase? DatabaseFor(this BusinessRegistry registry, ClaimsPrincipal principal) =>
        BusinessId(principal) is { } id && registry.Get(id) is { IsActive: true } b ? registry.Database(b) : null;

    public static ModuleSet ModulesFor(this BusinessRegistry registry, ClaimsPrincipal principal) =>
        BusinessId(principal) is { } id && registry.Get(id) is { } b ? b.Modules : ModuleSet.ForSize(BusinessSize.Informal);
}

/// <summary>A page or action needs this permission, within the modules the business has switched on.</summary>
public sealed record PermissionRequirement(Permission Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler(BusinessRegistry registry) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.ToUser().Role.Can(requirement.Permission, registry.ModulesFor(context.User)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
