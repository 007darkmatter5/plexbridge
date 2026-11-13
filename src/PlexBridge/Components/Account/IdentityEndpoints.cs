using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using PlexBridge.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityEndpoints
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/Account");
        account.MapPost("/Logout", async (HttpContext context, SignInManager<ApplicationUser> signInManager) =>
        {
            // Antiforgery runs first and marks the request. Read the form only after that succeeds,
            // or a missing token becomes a 500 when the form is touched.
            if (context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                return Results.BadRequest();

            var returnUrl = "";
            if (context.Request.HasFormContentType)
                returnUrl = (await context.Request.ReadFormAsync())["returnUrl"].ToString();

            await signInManager.SignOutAsync();
            if (string.IsNullOrEmpty(returnUrl)
                || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                || returnUrl.StartsWith("//", StringComparison.Ordinal)
                || returnUrl.Contains('\\')
                || returnUrl.Contains("://", StringComparison.Ordinal))
                return Results.LocalRedirect("~/");
            return Results.LocalRedirect("~/" + returnUrl.TrimStart('/'));
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());
        return account;
    }
}
