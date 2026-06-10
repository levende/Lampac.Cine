using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;

namespace Cine;

public class CineController : BaseOnlineController<ModuleConf>
{
    public CineController() : base(ModInit.conf)
    {
    }

    [HttpGet, Staticache(manually: true)]
    [Route("lite/cine")]
    public async Task<ActionResult> Index(long kinopoisk_id, string title, string original_title, int serial, string t, short s = -1, bool rjson = false)
    {
        if (kinopoisk_id == 0)
            return OnError("KP id not provided");

        if (serial == 0)
        {
            return await FindMovieAsync(kinopoisk_id, title, original_title);
        }
        else
        {
            return await FindSeriesAsync(kinopoisk_id, title, original_title, s, t, rjson);
        }
    }

    private async Task<ActionResult> FindMovieAsync(long kpId, string title, string original_title)
    {
        var cineService = new CineService(proxyManager, ModInit.conf);

        var cache = await InvokeCacheResult<MovieModel>($"cine:movie:{kpId}", 180, async e =>
        {
            var movie = await cineService.FindMovieAsync(kpId);
            return e.Success(movie);
        });

        var proxy = proxyManager?.Get();
        var cookies = await cineService.EnsureAuthorizedAsync(null);
        proxyManager?.Refresh();

        return ContentTpl(cache, () =>
        {
            var mtpl = new MovieTpl(title, original_title, cache.Value.voices.Count);

            var cookie = BuildCookieHeader(cookies, new Uri(init.host));
            var headers = new List<HeadersModel>(1) { new("cookie", cookie) };

            foreach (var voice in cache.Value.voices)
            {
                mtpl.Append(
                    voice.title,
                    HostStreamProxy($"{init.host}{voice.url}#.m3u8", headers),
                    vast: init.vast,
                    quality: voice.quality
                );
            }

            return mtpl;
        });
    }

    private async Task<ActionResult> FindSeriesAsync(long kpId, string title, string original_title, int s, string t, bool rjson)
    {
        var cineService = new CineService(proxyManager, ModInit.conf);

        var cache = await InvokeCacheResult<SeriesModel>($"cine:tv:{kpId}", 180, async e =>
        {
            var series = await cineService.FindSeriesAsync(kpId);
            return e.Success(series);
        });

        string enc_title = HttpUtility.UrlEncode(title);
        string enc_original_title = HttpUtility.UrlEncode(original_title);

        var proxy = proxyManager?.Get();
        var cookies = await cineService.EnsureAuthorizedAsync(proxy);
        proxyManager?.Refresh();

        return ContentTpl(cache, () =>
        {
            if (s == -1)
            {
                var tpl = new SeasonTpl(cache.Value.seasons.Count);

                foreach (var season in cache.Value.seasons)
                {
                    tpl.Append(
                        $"{season.season} сезон",
                        $"{host}/lite/cine?rjson={rjson}&serial=1&kinopoisk_id={kpId}&title={enc_title}&original_title={enc_original_title}&s={season.season}",
                        season.season
                    );
                }

                return tpl;
            }

            var selectedSeason = cache.Value.seasons.FirstOrDefault(season => season.season == s);
            if (selectedSeason == default)
            {
                throw new InvalidOperationException($"Season {s} not found for kinopoisk_id {kpId}");
            }

            var vtpl = new VoiceTpl();

            string selectedVoice = string.IsNullOrEmpty(t)
                ? selectedSeason.voices.First().Key.GetHashCode().ToString()
                : t;

            var voices = new Dictionary<string, List<EpisodeModel>>(selectedSeason.voices.Count);

            foreach (var voice in selectedSeason.voices)
            {
                var voiceId = voice.Key.GetHashCode().ToString();
                var link = $"{host}/lite/cine?rjson={rjson}&serial=1&kinopoisk_id={kpId}&title={enc_title}&original_title={enc_original_title}&s={s}&t={voiceId}";

                vtpl.Append(
                    voice.Key,
                    voiceId == selectedVoice,
                    link
                );

                voices[voiceId] = voice.Value;
            }

            var etpl = new EpisodeTpl(vtpl);

            var cookie = BuildCookieHeader(cookies, new Uri(init.host));
            var headers = new List<HeadersModel>(1) { new("cookie", cookie) };

            foreach (var episode in voices[selectedVoice])
            {
                etpl.Append(
                    name: $"{episode.episode} серия",
                    title: title ?? original_title,
                    s: s.ToString(),
                    e: episode.episode.ToString(),
                    link: HostStreamProxy($"{init.host}{episode.url}#.m3u8", headers)
                );
            }

            return etpl;
        });
    }


    static string BuildCookieHeader(CookieContainer cookieContainer, Uri uri)
    {
        if (cookieContainer == null || uri == null)
        {
            return null;
        }

        var cookies = cookieContainer.GetCookies(uri);
        if (cookies.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder((int)cookies.Count * 32);
        bool first = true;

        foreach (System.Net.Cookie cookie in cookies)
        {
            if (!first)
                sb.Append("; ");
            first = false;
            sb.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return sb.ToString();
    }
}
