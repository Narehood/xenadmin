using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XenAPI
{
    public partial class JsonRpcClient
    {
        // Keep hand-written async adapters outside the generated SDK. The same
        // converter as host_get_servertime preserves XAPI's compact date format.
        public System.Threading.Tasks.Task<DateTime> HostGetServerTimeAsync(
            string session, string host, CancellationToken cancellationToken)
            => RpcAsync<DateTime>("host.get_servertime", new JArray(session, host ?? ""),
                CreateSerializer(new List<JsonConverter> { new XenDateTimeConverter() }), cancellationToken);
    }
}
