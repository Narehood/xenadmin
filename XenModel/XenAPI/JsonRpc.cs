/*
 * Copyright (c) Cloud Software Group, Inc.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 *   1) Redistributions of source code must retain the above copyright
 *      notice, this list of conditions and the following disclaimer.
 *
 *   2) Redistributions in binary form must reproduce the above
 *      copyright notice, this list of conditions and the following
 *      disclaimer in the documentation and/or other materials
 *      provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace XenAPI
{
    public enum JsonRpcVersion
    {
        v1,
        v2
    }

    internal abstract class JsonRequest
    {
        protected JsonRequest(int id, string method, JToken parameters)
        {
            this.Id = id;
            this.Method = method;
            this.Parameters = parameters;
        }

        public static JsonRequest Create(JsonRpcVersion jsonRpcVersion, int id, string method, JToken parameters)
        {
            switch (jsonRpcVersion)
            {
                case JsonRpcVersion.v2:
                    return new JsonRequestV2(id, method, parameters);
                default:
                    return new JsonRequestV1(id, method, parameters);
            }
        }

        /// <summary>
        /// Unique call id. Can be null in JSON_RPC v2.0, but xapi disallows it.
        /// </summary>
        [JsonProperty("id", Required = Required.Always)]
        public int Id { get; private set; }

        /// <summary>
        /// The API function to call.
        /// </summary>
        [JsonProperty("method", Required = Required.Always)]
        public string Method { get; private set; }

        /// <summary>
        /// Any parameters, optional in JSON-RPC v2.0.
        /// </summary>
        [JsonProperty("params", Required = Required.Always)]
        public JToken Parameters { get; private set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }

    internal class JsonRequestV1 : JsonRequest
    {
        public JsonRequestV1(int id, string method, JToken parameters)
            : base(id, method, parameters)
        {
        }
    }

    internal class JsonRequestV2 : JsonRequest
    {
        public JsonRequestV2(int id, string method, JToken parameters)
            : base(id, method, parameters)
        {
        }

        [JsonProperty("jsonrpc", Required = Required.Always)]
        public string JsonRPC
        {
            get { return "2.0"; }
        }
    }


    internal abstract class JsonResponse<T>
    {
        [JsonProperty("id", Required = Required.AllowNull)] public int Id = 0;

        [JsonProperty("result", Required = Required.Default)] public T Result = default(T);

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }

    internal class JsonResponseV1<T> : JsonResponse<T>
    {
        [JsonProperty("error", Required = Required.AllowNull)] public JToken Error = null;
    }

    internal class JsonResponseV2<T> : JsonResponse<T>
    {
        [JsonProperty("error", Required = Required.DisallowNull)] public JsonResponseV2Error Error = null;

        [JsonProperty("jsonrpc", Required = Required.Always)] public string JsonRpc = null;
    }

    internal class JsonResponseV2Error
    {
        [JsonProperty("code", Required = Required.Always)] public int Code = 0;

        [JsonProperty("message", Required = Required.Always)] public string Message = null;

        [JsonProperty("data", Required = Required.Default)] public JToken Data = null;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }


    public partial class JsonRpcClient : IDisposable
    {
        private int _globalId;

        public JsonRpcClient(string baseUrl)
        {
            Url = baseUrl;
            JsonRpcUrl = new Uri(new Uri(baseUrl), "/jsonrpc").ToString();
            JsonRpcVersion = JsonRpcVersion.v1;
        }

        /// <summary>
        /// Reports only the method name in every build configuration. Request parameters
        /// can contain passwords and bearer session references and must not enter diagnostics.
        /// </summary>
        public event Action<string> RequestEvent;

        public JsonRpcVersion JsonRpcVersion { get; set; }
        public string UserAgent { get; set; }
        public bool KeepAlive { get; set; }
        public IWebProxy WebProxy { get; set; }
        public int Timeout { get; set; }
        public string ConnectionGroupName { get; set; }
        public Version ProtocolVersion { get; set; }
        public bool Expect100Continue { get; set; }
        public bool AllowAutoRedirect { get; set; }
        public bool PreAuthenticate { get; set; }
        public CookieContainer Cookies { get; set; }
        public RemoteCertificateValidationCallback ServerCertificateValidationCallback { get; set; }

        public string Url { get; private set; }

        public string JsonRpcUrl { get; private set; }

        private void Rpc(string callName, JToken parameters, JsonSerializer serializer)
        {
            Rpc<object>(callName, parameters, serializer);
        }

        private T Rpc<T>(string callName, JToken parameters, JsonSerializer serializer)
        {
            return RpcAsync<T>(callName, parameters, serializer, CancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>An asynchronous entry point for callers that already supply JSON parameters.</summary>
        public System.Threading.Tasks.Task<T> CallAsync<T>(string callName, JToken parameters, CancellationToken cancellationToken = default(CancellationToken))
            => RpcAsync<T>(callName, parameters, CreateSerializer(new List<JsonConverter>()), cancellationToken);

        /// <summary>Cancellation for synchronous generated calls on this client/session.</summary>
        public CancellationToken CancellationToken { get; set; }

        private async System.Threading.Tasks.Task<T> RpcAsync<T>(string callName, JToken parameters, JsonSerializer serializer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = JsonRpcVersion;
            var request = JsonRequest.Create(version, Interlocked.Increment(ref _globalId), callName, parameters);
            string body;
            using (var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            {
                serializer.Serialize(writer, request);
                body = writer.ToString();
            }
            RequestEvent?.Invoke(callName);
            return await SendAsync(body, reader =>
            {
                if (version == JsonRpcVersion.v2)
                {
                    var result = (JsonResponseV2<T>)serializer.Deserialize(reader, typeof(JsonResponseV2<T>));
                    if (result == null)
                        throw new WebException("JSON-RPC response was empty.", WebExceptionStatus.ServerProtocolViolation);
                    if (result.Error != null)
                    {
                        var description = new List<string> { result.Error.Message };
                        if (result.Error.Data != null) description.AddRange(result.Error.Data.ToObject<string[]>());
                        throw new Failure(description);
                    }
                    return result.Result;
                }
                var legacy = (JsonResponseV1<T>)serializer.Deserialize(reader, typeof(JsonResponseV1<T>));
                if (legacy == null)
                    throw new WebException("JSON-RPC response was empty.", WebExceptionStatus.ServerProtocolViolation);
                if (legacy.Error != null)
                {
                    var errorArray = legacy.Error.ToObject<string[]>();
                    if (errorArray != null) throw new Failure(errorArray);
                }
                return legacy.Result;
            }, cancellationToken).ConfigureAwait(false);
        }

        private JsonSerializerSettings CreateSettings(IList<JsonConverter> converters)
        {
            return new JsonSerializerSettings
            {
#if DEBUG
                Formatting = Formatting.Indented,
#endif
                Converters = converters,
                DateParseHandling = DateParseHandling.None,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        private JsonSerializer CreateSerializer(IList<JsonConverter> converters)
        {
            var settings = CreateSettings(converters);
            return JsonSerializer.Create(settings);
        }
    }
}
