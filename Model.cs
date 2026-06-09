using System.Collections.Generic;

namespace Cine;

public record VoiceModel(string title, string url, string quality);
public record MovieModel(List<VoiceModel> voices);

public record EpisodeModel(int episode, string url);
public record SeasonModel(int season, Dictionary<string, List<EpisodeModel>> voices);
public record SeriesModel(string type, List<SeasonModel> seasons);