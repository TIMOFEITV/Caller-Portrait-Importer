using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using UnityEngine;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace ScamWYF.TwitchControl
{
    // Twitch OAuth 2.0 Device Code Grant, public client, chat:read only.
    // Requires the MOD AUTHOR to register a Twitch Public application and set its Client ID.
    // Streamers never enter client secrets, access tokens, or their login names.
    internal sealed class TwitchDeviceAuth : IDisposable
    {
        // Twitch responses are deserialized with Unity's built-in JsonUtility, which the
        // game already uses. Unity's serializer binds *fields* by exact JSON key name,
        // so these snake_case identifiers are intentional.
        // Suppress CS0649: Unity populates these fields without C# assignments.
#pragma warning disable 0649
        [Serializable]
        private sealed class DeviceResponse
        {
            public string device_code;
            public string user_code;
            public string verification_uri;
            public int expires_in;
            public int interval;
            public string DeviceCode { get { return device_code; } }
            public string UserCode { get { return user_code; } }
            public string VerificationUri { get { return verification_uri; } }
            public int ExpiresIn { get { return expires_in; } }
            public int Interval { get { return interval; } }
        }
        [Serializable]
        private sealed class TokenResponse
        {
            public string access_token;
            public string refresh_token;
            public int expires_in;
            public string AccessToken { get { return access_token; } }
            public string RefreshToken { get { return refresh_token; } }
            public int ExpiresIn { get { return expires_in; } }
        }
        [Serializable]
        private sealed class ValidateResponse
        {
            public string login;
            public string client_id;
            public int expires_in;
            public string[] scopes;
            public string Login { get { return login; } }
            public string ClientId { get { return client_id; } }
            public int ExpiresIn { get { return expires_in; } }
            public string[] Scopes { get { return scopes; } }
        }
        [Serializable]
        private sealed class ErrorResponse
        {
            public string message;
            public string error;
            public int status;
            public string Message { get { return message; } }
            public string Error { get { return error; } }
            public int Status { get { return status; } }
        }
#pragma warning restore 0649
        internal sealed class Session
        {
            public string Login;
            public string Token;
        }

        private readonly object gate = new object();
        private readonly string clientId;
        private readonly string cachePath;
        private CancellationTokenSource cancel;
        private Session pendingSession;
        private string openBrowserUrl;
        private string token;
        private string refreshToken;
        private DateTimeOffset expiresAt;
        private volatile string status = "Not connected";
        private volatile string activationCode = "";
        private bool started;
        private bool refreshing;
        private bool disposed;
        private DateTimeOffset nextRefreshTry;
        private string storageStatus = "";

        public string Status { get { return status; } }
        public string ActivationCode { get { return activationCode; } }
        public string StorageStatus { get { return storageStatus; } }
        public bool Configured { get { return IsValidClientId(clientId); } }

        public TwitchDeviceAuth(string publicClientId)
        {
            clientId = (publicClientId ?? "").Trim();
            cachePath = Path.Combine(Paths.ConfigPath, "ScamWYF.TwitchControl.twitch-auth.dat");
        }

        private static bool IsValidClientId(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length < 10 || id.Length > 80) return false;
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z'))) return false;
            return true;
        }

        // Unity 6 players can ship with a stripped System.Net.Http.dll where HttpClient
        // does not exist. Use .NET Framework's classic WebRequest API instead.
        // All network I/O happens on the thread pool, never the Unity render thread.
        private static Task<KeyValuePair<int, string>> Post(string url, IDictionary<string, string> args, CancellationToken ct)
        {
            return RequestAsync(url, "POST", args, null, ct);
        }

        private static Task<KeyValuePair<int, string>> RequestAsync(string url, string method,
            IDictionary<string, string> form, string oauthToken, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = 18000;
                request.ReadWriteTimeout = 18000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (!String.IsNullOrEmpty(oauthToken))
                    request.Headers[HttpRequestHeader.Authorization] = "OAuth " + oauthToken;

                using (ct.Register(() => { try { request.Abort(); } catch { } }))
                {
                    if (form != null)
                    {
                        var parts = new List<string>();
                        foreach (var entry in form)
                            parts.Add(Uri.EscapeDataString(entry.Key) + "=" + Uri.EscapeDataString(entry.Value ?? ""));
                        byte[] payload = Encoding.UTF8.GetBytes(String.Join("&", parts.ToArray()));
                        request.ContentType = "application/x-www-form-urlencoded";
                        request.ContentLength = payload.Length;
                        try
                        {
                            using (var body = request.GetRequestStream()) body.Write(payload, 0, payload.Length);
                        }
                        catch (WebException)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            throw;
                        }
                    }

                    try
                    {
                        using (var response = (HttpWebResponse)request.GetResponse())
                            return ReadResponse(response);
                    }
                    catch (WebException ex)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        using (var response = ex.Response as HttpWebResponse)
                        {
                            if (response != null) return ReadResponse(response);
                        }
                        throw;
                    }
                }
            }, ct);
        }

        private static KeyValuePair<int, string> ReadResponse(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return new KeyValuePair<int, string>((int)response.StatusCode, reader.ReadToEnd());
        }

        private static T Parse<T>(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("Twitch returned an empty JSON response");
            // Unity documents that JsonUtility.FromJson supports background threads.
            // No System.Runtime.Serialization or System.Net.Http type is needed.
            return JsonUtility.FromJson<T>(json);
        }

        public void Resume()
        {
            if (!Configured) { status = "Mod author must configure the Twitch public Client ID"; return; }
            if (disposed) return;
            if (!TryLoad()) return;
            status = "Restoring Twitch session...";
            var ct = ReplaceCancellation();
            Task.Run(() => ResumeAsync(ct));
        }

        public void BeginLogin()
        {
            if (!Configured) { status = "Missing Twitch app Client ID (developer setup required)"; return; }
            if (disposed) return;
            status = "Requesting Twitch authorization...";
            activationCode = "";
            var ct = ReplaceCancellation();
            Task.Run(() => LoginAsync(ct));
        }

        private CancellationToken ReplaceCancellation()
        {
            lock (gate)
            {
                if (cancel != null) { cancel.Cancel(); cancel.Dispose(); }
                cancel = new CancellationTokenSource();
                openBrowserUrl = null;
                pendingSession = null;
                started = true;
                return cancel.Token;
            }
        }

        private async Task LoginAsync(CancellationToken ct)
        {
            try
            {
                var result = await Post("https://id.twitch.tv/oauth2/device",
                    new Dictionary<string, string> { { "client_id", clientId }, { "scopes", "chat:read" } }, ct).ConfigureAwait(false);
                if (result.Key != 200) throw new InvalidOperationException("Twitch device authorization request failed (HTTP " + result.Key + ")");
                DeviceResponse device = Parse<DeviceResponse>(result.Value);
                if (device == null || String.IsNullOrWhiteSpace(device.DeviceCode) || String.IsNullOrWhiteSpace(device.UserCode))
                    throw new InvalidOperationException("Twitch returned an invalid authorization response");
                // Prefer the exact activation URL supplied by Twitch; it may include
                // parameters specific to public device clients. Never follow a foreign host.
                string url = device.VerificationUri;
                Uri activation;
                if (!Uri.TryCreate(url, UriKind.Absolute, out activation) ||
                    activation.Scheme != Uri.UriSchemeHttps ||
                    !(String.Equals(activation.Host, "www.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
                      String.Equals(activation.Host, "twitch.tv", StringComparison.OrdinalIgnoreCase)))
                    url = "https://www.twitch.tv/activate?public=true&device-code=" + Uri.EscapeDataString(device.UserCode ?? "");
                lock (gate) { if (ct.IsCancellationRequested) return; openBrowserUrl = url; }
                activationCode = device.UserCode ?? "";
                status = "Approve access in the browser (code: " + activationCode + ")";
                var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 600);
                int intervalSeconds = Math.Max(5, device.Interval);
                while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(intervalSeconds * 1000, ct).ConfigureAwait(false);
                    var post = await Post("https://id.twitch.tv/oauth2/token",
                        new Dictionary<string, string> {
                            {"client_id", clientId}, {"scopes", "chat:read"},
                            {"device_code", device.DeviceCode}, {"grant_type", "urn:ietf:params:oauth:grant-type:device_code"}
                        }, ct).ConfigureAwait(false);
                    if (post.Key == 200)
                    {
                        var received = Parse<TokenResponse>(post.Value);
                        if (received == null || String.IsNullOrEmpty(received.AccessToken))
                            throw new InvalidOperationException("Twitch returned an invalid access token response");
                        await AcceptAsync(received, ct).ConfigureAwait(false);
                        return;
                    }
                    if (post.Key == 400)
                    {
                        var error = Parse<ErrorResponse>(post.Value);
                        string reason = error == null ? "" : (error.Message ?? error.Error ?? "");
                        if (reason.IndexOf("authorization_pending", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            reason.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (reason.IndexOf("slow_down", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            intervalSeconds = Math.Min(30, intervalSeconds + 5);
                            continue;
                        }
                        if (reason.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidOperationException("Approval expired; click Sign in again");
                        if (reason.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidOperationException("Twitch authorization was declined");
                        throw new InvalidOperationException("Twitch device authorization failed");
                    }
                    throw new InvalidOperationException("Twitch sign-in failed (HTTP " + post.Key + ")");
                }
                if (!ct.IsCancellationRequested) status = "Twitch authorization expired; click Sign in to retry";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!ct.IsCancellationRequested) status = "Twitch sign-in failed: " + ex.GetBaseException().Message; }
        }

        private async Task ResumeAsync(CancellationToken ct)
        {
            try
            {
                bool valid = false;
                ValidateResponse details = null;
                if (!String.IsNullOrEmpty(token))
                {
                    try { details = await ValidateAsync(token, ct).ConfigureAwait(false); valid = details != null; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                if (valid)
                {
                    expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, details.ExpiresIn));
                    SetSession(details.Login, token, ct);
                    return;
                }
                TokenResponse replacement = await RefreshAsync(ct).ConfigureAwait(false);
                await AcceptAsync(replacement, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { if (!ct.IsCancellationRequested) status = "Session expired; click Sign in with Twitch"; }
        }

        private async Task<TokenResponse> RefreshAsync(CancellationToken ct)
        {
            if (String.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("No refresh token");
            var r = await Post("https://id.twitch.tv/oauth2/token",
                new Dictionary<string, string> {
                    {"client_id", clientId}, {"grant_type", "refresh_token"}, {"refresh_token", refreshToken}
                }, ct).ConfigureAwait(false);
            if (r.Key != 200) throw new InvalidOperationException("Twitch refresh failed");
            var fresh = Parse<TokenResponse>(r.Value);
            if (fresh == null || String.IsNullOrEmpty(fresh.AccessToken)) throw new InvalidOperationException("Twitch refresh returned invalid token");
            return fresh;
        }

        private async Task<ValidateResponse> ValidateAsync(string accessToken, CancellationToken ct)
        {
            var result = await RequestAsync("https://id.twitch.tv/oauth2/validate", "GET", null, accessToken, ct)
                .ConfigureAwait(false);
            if (result.Key != 200) throw new InvalidOperationException("Twitch token validation failed (HTTP " + result.Key + ")");
            var data = Parse<ValidateResponse>(result.Value);
            if (data == null || String.IsNullOrEmpty(data.Login) || data.ClientId != clientId ||
                data.Scopes == null || Array.IndexOf(data.Scopes, "chat:read") < 0)
                throw new InvalidOperationException("Twitch authorization did not grant chat:read");
            return data;
        }

        private async Task AcceptAsync(TokenResponse received, CancellationToken ct)
        {
            var info = await ValidateAsync(received.AccessToken, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            token = received.AccessToken;
            if (!String.IsNullOrEmpty(received.RefreshToken)) refreshToken = received.RefreshToken;
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, received.ExpiresIn));
            TrySave();
            SetSession(info.Login, token, ct);
        }

        private void SetSession(string login, string accessToken, CancellationToken ct)
        {
            lock (gate)
            {
                if (ct.IsCancellationRequested || disposed) return;
                pendingSession = new Session { Login = login, Token = accessToken };
                activationCode = "";
                status = "Twitch authorized as " + login;
            }
        }

        public Session TakeSession()
        {
            lock (gate) { Session s = pendingSession; pendingSession = null; return s; }
        }
        public string TakeBrowserUrl()
        {
            lock (gate) { string s = openBrowserUrl; openBrowserUrl = null; return s; }
        }

        public void Tick()
        {
            if (disposed || !started || String.IsNullOrEmpty(token) || String.IsNullOrEmpty(refreshToken) || refreshing) return;
            if (DateTimeOffset.UtcNow < expiresAt.AddMinutes(-5) || DateTimeOffset.UtcNow < nextRefreshTry) return;
            refreshing = true;
            var ct = cancel == null ? CancellationToken.None : cancel.Token;
            Task.Run(async () =>
            {
                try { await AcceptAsync(await RefreshAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { if (!ct.IsCancellationRequested) status = "Twitch session refresh failed; sign in again if disconnected"; }
                finally { nextRefreshTry = DateTimeOffset.UtcNow.AddMinutes(1); refreshing = false; }
            });
        }

        public void SignOut()
        {
            lock (gate)
            {
                if (cancel != null) { cancel.Cancel(); cancel.Dispose(); cancel = null; }
                token = null;
                refreshToken = null;
                pendingSession = null;
                openBrowserUrl = null;
                activationCode = "";
                status = "Signed out";
                started = false;
            }
            try { if (File.Exists(cachePath)) File.Delete(cachePath); }
            catch { }
        }

        private void TrySave()
        {
            try
            {
                // Windows DPAPI: encrypted for the current Windows user, never stored in plaintext.
                // On unsupported Mono runtimes fail closed: user can still authorize for this session.
                byte[] raw = Encoding.UTF8.GetBytes(token + "\n" + refreshToken);
                byte[] protectedBytes = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllBytes(cachePath, protectedBytes);
                storageStatus = "Sign-in securely remembered for this Windows user";
            }
            catch { storageStatus = "Sign-in works this session; secure local storage unavailable"; }
        }

        private bool TryLoad()
        {
            try
            {
                if (!File.Exists(cachePath)) return false;
                string saved = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(cachePath), null, DataProtectionScope.CurrentUser));
                string[] values = saved.Split(new[] {'\n'}, 2);
                if (values.Length != 2 || String.IsNullOrEmpty(values[0]) || String.IsNullOrEmpty(values[1])) return false;
                token = values[0]; refreshToken = values[1];
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            disposed = true;
            lock (gate) { if (cancel != null) { cancel.Cancel(); cancel.Dispose(); cancel = null; } }
        }
    }
}
