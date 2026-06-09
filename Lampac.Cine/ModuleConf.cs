using Shared.Models.Base;
using Shared.Services;
using System;

namespace Cine;

public class ModuleConf : BaseSettings, ICloneable
{
    public ModuleConf(string plugin, string host, bool enable = true)
    {
        this.enable = enable;
        this.plugin = plugin;

        if (host != null)
            this.host = host.StartsWith("http") ? host : Decrypt(host);

        streamproxy = true;

        headers = HeadersModel.Init(Http.defaultFullHeaders,
            ("Origin", this.host)
        ).ToDictionary();

        headers_stream = HeadersModel.Init(Http.defaultFullHeaders,
            ("Origin", this.host),
            ("sec-fetch-dest", "empty"),
            ("sec-fetch-mode", "cors"),
            ("sec-fetch-site", "cross-site"),
            ("accept", "*/*")
        ).ToDictionary();
    }


    public ModuleConf Clone() => (ModuleConf)MemberwiseClone();
    object ICloneable.Clone() => MemberwiseClone();
}
