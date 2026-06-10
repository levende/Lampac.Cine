using Shared.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace Cine;

public class CineService
{
    const string SessionTokenName = "__Secure-next-auth.session-token";

    private static readonly SemaphoreSlim LoginSemaphore = new(1, 1);
    private static readonly Lock CacheSaveLock = new();
    private static readonly object StateLock = new();
    private static readonly Regex HostRegex = new(@"https?://([^/]+)", RegexOptions.Compiled);

    private static readonly string CacheFilepath = Path.Combine("cache", "cine.json");
    private static CineData _cacheData = null!;

    private const int SaveCacheIntervalHours = 4;
    private static DateTime? _cacheLastSaveTime;

    private static CookieContainer? _cookieContainer;

    private readonly ModuleConf _config;
    private readonly ProxyManager _proxyManager;


    static CineService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilepath)!);
        _cacheData = LoadData();
        _cacheLastSaveTime = _cacheData.lastUpdated;
    }

    public CineService(ProxyManager proxyManager, ModuleConf conf)
    {
        _proxyManager = proxyManager;
        _config = conf;
    }

    public async Task<MovieModel> FindMovieAsync(long kpId)
    {
        var proxy = _proxyManager?.Get();
        var cookieContainer = await EnsureAuthorizedAsync(proxy);

        var movie = await Http.Get<MovieModel>(
            $"{_config.host}/api/cinetorrent-playlist?media_type=movie&kp_id={kpId}",
            useDefaultHeaders: true,
            cookieContainer: cookieContainer,
            proxy: proxy);

        _proxyManager?.Refresh();

        return movie;
    }

    public async Task<SeriesModel> FindSeriesAsync(long kpId)
    {
        var proxy = _proxyManager?.Get();
        var cookieContainer = await EnsureAuthorizedAsync(proxy);

        var series = await Http.Get<SeriesModel>(
            $"{_config.host}/api/cinetorrent-playlist?media_type=tv&kp_id={kpId}",
            useDefaultHeaders: true,
            cookieContainer: cookieContainer);

        _proxyManager?.Refresh();

        return series;
    }

    public async Task<CookieContainer> EnsureAuthorizedAsync(WebProxy proxy)
    {
        var needLogin = false;

        lock (StateLock)
        {
            if (string.IsNullOrEmpty(_cacheData.token) || _cacheData.tokenExpires is null)
            {
                needLogin = true;
            }
            else if (_cacheData.tokenExpires.Value <= DateTime.UtcNow)
            {
                needLogin = true;
            }
            else if (_cookieContainer is null)
            {
                InitializeCookieContainerLocked();
            }
        }

        if (needLogin)
        {
            await LoginAsync(proxy);
            return _cookieContainer;
        }

        var sessionResult = await CheckSessionAsync();

        if (!sessionResult.Success)
        {
            await LoginAsync(proxy);
        }
        else
        {
            lock (StateLock)
            {
                if (_cookieContainer is null)
                {
                    InitializeCookieContainerLocked();
                }
            }
        }

        return _cookieContainer;
    }

    private void InitializeCookieContainerLocked()
    {
        string domain = HostRegex.Match(_config.host).Groups[1].Value;
        _cookieContainer = new CookieContainer();
        _cookieContainer.Add(new Cookie()
        {
            Path = "/",
            Expires = _cacheData.tokenExpires!.Value,
            Domain = $".{domain}",
            Name = SessionTokenName,
            Value = _cacheData.token,
            HttpOnly = true,
        });
    }

    private async Task LoginAsync(WebProxy proxy)
    {
        await LoginSemaphore.WaitAsync();
        try
        {
            lock (StateLock)
            {
                if (!string.IsNullOrEmpty(_cacheData.token) && _cookieContainer is not null)
                {
                    return;
                }
            }

            var uri = new Uri(_config.host);
            string domain = Regex.Match(_config.host, "https?://([^/]+)").Groups[1].Value;
            var cookieContainer = new CookieContainer();

            var csrf = await Http.BaseGetAsync<CsrfResponse>($"{uri.Scheme}://{uri.Authority}/api/auth/csrf", proxy: proxy, cookieContainer: cookieContainer);

            if (string.IsNullOrEmpty(csrf.content?.csrfToken)
                || !csrf.response.Headers.TryGetValues("Set-Cookie", out var csrfCookieHeaders))
            {
                throw new InvalidOperationException("CSRF");
            }

            foreach (var cookie in csrfCookieHeaders)
            {
                cookieContainer.SetCookies(
                    csrf.response.RequestMessage!.RequestUri!,
                    cookie
                );
            }

            var postContentStr = $"email={Uri.EscapeDataString(_config.login)}" +
                $"&password={Uri.EscapeDataString(_config.passwd)}" +
                $"&csrfToken={Uri.EscapeDataString(csrf.content.csrfToken)}" +
                $"&callbackUrl={Uri.EscapeDataString($"{uri.Scheme}://{uri.Authority}/login")}" +
                $"&redirect=false" +
                $"&json=true";

            using var postContent = new StringContent(postContentStr, Encoding.UTF8, "application/x-www-form-urlencoded");
            var auth = await Http.BasePost($"{uri.Scheme}://{uri.Authority}/api/auth/callback/credentials",
                postContent,
                cookieContainer: cookieContainer,
                proxy: proxy
            );

            if (string.IsNullOrEmpty(csrf.content?.csrfToken)
               || !csrf.response.Headers.TryGetValues("Set-Cookie", out var authCookieHeaders))
            {
                throw new InvalidOperationException("CSRF");
            }

            foreach (var cookie in authCookieHeaders)
            {
                cookieContainer.SetCookies(
                    csrf.response.RequestMessage!.RequestUri!,
                    cookie
                );
            }

            lock (StateLock)
            {
                var sessionCookie = cookieContainer.GetCookies(uri).First(x => x.Name == SessionTokenName);

                _cacheData.token = sessionCookie.Value;
                _cacheData.tokenExpires = sessionCookie.Expires;
                _cacheData.lastUpdated = DateTime.UtcNow;
                _cookieContainer = cookieContainer;
            }

            SaveData(force: true);
        }
        finally
        {
            LoginSemaphore.Release();
        }
    }

    private async Task<SessionCheckResult> CheckSessionAsync()
    {
        string? token;
        CookieContainer? cookieContainer;

        lock (StateLock)
        {
            token = _cacheData.token;
            cookieContainer = _cookieContainer;
        }

        if (string.IsNullOrEmpty(token))
        {
            return new SessionCheckResult(false, "No token", false);
        }

        try
        {
            var sessionResponse = await Http.Get<AuthResponse>(
                $"{_config.host}/api/auth/session",
                useDefaultHeaders: true,
                cookieContainer: cookieContainer);

            if (string.IsNullOrEmpty(sessionResponse?.expires) || sessionResponse.user is null)
            {
                return new SessionCheckResult(false, "Invalid session", false);
            }

            var expires = DateTime.Parse(sessionResponse.expires);

            lock (StateLock)
            {
                _cacheData.tokenExpires = expires;
            }

            return new SessionCheckResult(true, null, sessionResponse.user.isBanned);
        }
        catch (Exception ex)
        {
            return new SessionCheckResult(false, ex.Message, false);
        }
    }

    private void SaveData(bool force = false)
    {
        lock (CacheSaveLock)
        {
            if (!force && _cacheLastSaveTime.HasValue && DateTime.UtcNow - _cacheLastSaveTime.Value < TimeSpan.FromHours(SaveCacheIntervalHours))
            {
                return;
            }

            _cacheData.lastUpdated = DateTime.UtcNow;
            _cacheLastSaveTime = _cacheData.lastUpdated;

            var json = JsonSerializer.Serialize(_cacheData, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            File.WriteAllText(CacheFilepath, json);
        }
    }

    private static CineData LoadData()
    {
        if (File.Exists(CacheFilepath))
        {
            var json = File.ReadAllText(CacheFilepath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<CineData>(json, options) ?? new CineData();
        }

        return new CineData();
    }

    private record CineData
    {
        public string? token { get; set; }
        public DateTime? tokenExpires { get; set; }
        public DateTime? lastUpdated { get; set; }
    }

    private record CsrfResponse(string csrfToken);
    private record AuthResponse(UserInfo? user, string expires);
    private record UserInfo(string? name, string? email, int id, bool isAdmin, bool isBanned);
    private record SessionCheckResult(bool Success, string? Reason, bool IsBanned);
}