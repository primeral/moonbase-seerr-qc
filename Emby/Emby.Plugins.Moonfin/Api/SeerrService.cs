using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class SeerrService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;
        private static readonly object BootstrapLock = new object();
        private static readonly Dictionary<Guid, long> BootstrapRetryAfter =
            new Dictionary<Guid, long>();
        private const long BootstrapRetryDelayMs = 30000;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private SeerrSessionService Session => Plugin.Instance?.SeerrService
            ?? throw new InvalidOperationException("SeerrSessionService not initialized");

        public SeerrService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private static bool ReserveBootstrapAttempt(Guid userId)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (BootstrapLock)
            {
                if (BootstrapRetryAfter.TryGetValue(userId, out var retryAfter)
                    && retryAfter > now)
                    return false;

                BootstrapRetryAfter[userId] = now + BootstrapRetryDelayMs;
                return true;
            }
        }

        private static void ClearBootstrapCooldown(Guid userId)
        {
            lock (BootstrapLock)
                BootstrapRetryAfter.Remove(userId);
        }

        /// <summary>
        /// Creates a Seerr session from the already-authenticated Jellyfin identity.
        /// No Jellyfin credentials or client-selected identity are accepted.
        /// </summary>
        public async Task<object?> Post(SeerrBootstrapRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            {
                Request.Response.StatusCode = 503;
                return new { error = "Seerr integration is not enabled", success = false };
            }

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null || user.Id == Guid.Empty)
            {
                Request.Response.StatusCode = 401;
                return new { error = "User not authenticated", success = false };
            }

            if (string.IsNullOrWhiteSpace(user.Name))
            {
                Request.Response.StatusCode = 500;
                return new { error = "Unable to resolve Jellyfin username", success = false };
            }

            var result = await Session
                .BootstrapWithSeerrQcAsync(user.Id, user.Name)
                .ConfigureAwait(false);

            if (result == null || !result.Success)
            {
                Request.Response.StatusCode = 401;
                return new { error = result?.Error ?? "Seerr+QC bootstrap failed", success = false };
            }

            return new { success = true, seerrUserId = result.SeerrUserId, jellyseerrUserId = result.SeerrUserId, displayName = result.DisplayName, avatar = result.Avatar, permissions = result.Permissions };
        }

        public async Task<object?> Post(SeerrLoginRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            { Request.Response.StatusCode = 503; return new { error = "Seerr integration is not enabled" }; }

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }
            if (string.IsNullOrEmpty(request.Username)) { Request.Response.StatusCode = 400; return new { error = "Username is required" }; }

            var result = await Session.AuthenticateAsync(userId.Value, request.Username, request.Password, request.AuthType).ConfigureAwait(false);

            if (result == null || !result.Success)
            {
                Request.Response.StatusCode = 401;
                return new { error = result?.Error ?? "Authentication failed", success = false };
            }

            // Only an admin or the owner session can provision the webhook, so trigger it for
            // those logins instead of every successful login. Best-effort, throttled internally.
            const int adminBit = 2;
            const int ownerSeerrUserId = 1;
            var provisioning = Plugin.Instance?.SeerrProvisioning;
            if (provisioning != null
                && (result.SeerrUserId == ownerSeerrUserId || (result.Permissions & adminBit) != 0))
            {
                _ = Task.Run(async () =>
                {
                    try { await provisioning.EnsureWebhookAsync(System.Threading.CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                });
            }

            return new { success = true, seerrUserId = result.SeerrUserId, jellyseerrUserId = result.SeerrUserId, displayName = result.DisplayName, avatar = result.Avatar, permissions = result.Permissions };
        }

        public async Task<object?> Get(GetSeerrStatusRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
                return new { enabled = false, authenticated = false, url = (string?)null };

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null || user.Id == Guid.Empty)
                return new { enabled = true, authenticated = false, url = config.SeerrUrl };

            var sess = await Session.GetSessionAsync(user.Id, validate: false).ConfigureAwait(false);

            // Existing Moonfin clients already request Status before opening Seerr.
            // Use that request as an idempotent bootstrap trigger when Seerr+QC is configured.
            if (sess == null
                && !string.IsNullOrWhiteSpace(config.SeerrApiKey)
                && !string.IsNullOrWhiteSpace(user.Name)
                && ReserveBootstrapAttempt(user.Id))
            {
                var result = await Session
                    .BootstrapWithSeerrQcAsync(user.Id, user.Name)
                    .ConfigureAwait(false);

                if (result?.Success == true)
                {
                    ClearBootstrapCooldown(user.Id);
                    sess = await Session
                        .GetSessionAsync(user.Id, validate: false)
                        .ConfigureAwait(false);
                }
            }
            return new
            {
                enabled = true,
                authenticated = sess != null,
                url = config.SeerrUrl,
                seerrUserId = sess?.SeerrUserId,
                jellyseerrUserId = sess?.SeerrUserId, // alias kept for older clients
                displayName = sess?.DisplayName,
                avatar = sess?.Avatar,
                permissions = sess?.Permissions ?? 0,
                sessionCreated = sess?.CreatedAt,
                lastValidated = sess?.LastValidated
            };
        }

        public async Task<object?> Get(ValidateSeerrSessionRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return new { valid = false, error = "Not authenticated" };
            var sess = await Session.GetSessionAsync(userId.Value, validate: true).ConfigureAwait(false);
            return new { valid = sess != null, lastValidated = sess?.LastValidated };
        }

        public async Task<object?> Delete(SeerrLogoutRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }

            await Session.ProxyRequestAsync(userId.Value, HttpMethod.Post, "auth/logout").ConfigureAwait(false);
            await Session.ClearSessionAsync(userId.Value).ConfigureAwait(false);
            return new { success = true, message = "Logged out from Seerr" };
        }

        public async Task<object?> Get(SeerrProxyGetRequest request) => await ProxyApiRequest(HttpMethod.Get, request.Path, null).ConfigureAwait(false);
        public async Task<object?> Post(SeerrProxyPostRequest request) => await ProxyApiRequest(HttpMethod.Post, request.Path, request.RequestStream).ConfigureAwait(false);
        public async Task<object?> Put(SeerrProxyPutRequest request) => await ProxyApiRequest(HttpMethod.Put, request.Path, request.RequestStream).ConfigureAwait(false);
        public async Task<object?> Delete(SeerrProxyDeleteRequest request) => await ProxyApiRequest(HttpMethod.Delete, request.Path, null).ConfigureAwait(false);

        private async Task<object?> ProxyApiRequest(HttpMethod method, string? path, System.IO.Stream? bodyStream)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            { Request.Response.StatusCode = 503; return new { error = "Seerr integration is not enabled" }; }

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }

            byte[]? body = null;
            string? contentType = null;
            if (bodyStream != null && (method == HttpMethod.Post || method == HttpMethod.Put))
            {
                using var ms = new MemoryStream();
                await bodyStream.CopyToAsync(ms).ConfigureAwait(false);
                body = ms.ToArray();
                contentType = Request.ContentType;
            }

            var result = await Session.ProxyRequestAsync(userId.Value, method, path ?? string.Empty, Http.QueryString(Request), body, contentType).ConfigureAwait(false);

            Request.Response.StatusCode = result.StatusCode;
            if (result.Body == null) return null;

            var responseContentType = string.IsNullOrWhiteSpace(result.ContentType) ? "application/octet-stream" : result.ContentType;
            return ResultFactory.GetResult(Request, new MemoryStream(result.Body), responseContentType, null);
        }
    }
}
