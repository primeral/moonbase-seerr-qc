using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

public static class ControllerExtensions
{
    private static readonly Type? UserManagerType =
        Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

    private static readonly MethodInfo? GetUserByIdMethod =
        UserManagerType?.GetMethod("GetUserById", [typeof(Guid)]);

    public static Guid? GetUserIdFromClaims(this ControllerBase controller)
    {
        var userIdClaim = controller.User.FindFirst("Jellyfin-UserId")?.Value
            ?? controller.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    /// <summary>
    /// Resolves the username from Jellyfin's server-side user record.
    /// The client cannot select or override this identity.
    /// </summary>
    public static string? GetUsernameForUserId(
        this ControllerBase controller,
        Guid userId)
    {
        if (UserManagerType == null || GetUserByIdMethod == null)
        {
            return null;
        }

        var userManager = controller.HttpContext.RequestServices
            .GetService(UserManagerType);

        var user = userManager == null
            ? null
            : GetUserByIdMethod.Invoke(userManager, [userId]);

        var userType = user?.GetType();
        return userType?.GetProperty("Username")?.GetValue(user) as string
            ?? userType?.GetProperty("Name")?.GetValue(user) as string;
    }
}
