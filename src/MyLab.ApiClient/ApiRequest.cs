using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MyLab.ApiClient
{
    /// <summary>
    /// Provides abilities to tune and send request
    /// </summary>
    public class ApiRequest<TRes>
    {
        /// <summary>
        /// Gets request modifiers collection
        /// </summary>
        public List<IRequestMessageModifier> RequestModifiers { get; }
            = new List<IRequestMessageModifier>();

        /// <summary>
        /// Contains expected response http status codes
        /// </summary>
        public List<HttpStatusCode> ExpectedCodes { get; }
            = new List<HttpStatusCode>();

        private readonly string _baseUrl;
        private readonly MethodDescription _methodDescription;
        private readonly IHttpClientProvider _httpClientProvider;
        private readonly Type _returnType;
        private readonly IReadOnlyList<IParameterApplier> _paramAppliers;

        internal ApiRequest(
            string baseUrl,
            MethodDescription methodDescription, 
            IEnumerable<IParameterApplier> paramAppliers,
            IHttpClientProvider httpClientProvider,
            Type returnType = null)
        {
            if (paramAppliers == null) throw new ArgumentNullException(nameof(paramAppliers));
            _baseUrl = baseUrl;
            _methodDescription = methodDescription ?? throw new ArgumentNullException(nameof(methodDescription));
            _httpClientProvider = httpClientProvider ?? throw new ArgumentNullException(nameof(httpClientProvider));
            _paramAppliers = paramAppliers.ToList().AsReadOnly();

            _returnType = returnType ?? typeof(TRes);
            if (!typeof(TRes).IsAssignableFrom(_returnType))
                throw new InvalidOperationException($"Specified return type '{_returnType.FullName}' must be assignable to method return type '{typeof(TRes).FullName}'");

            ExpectedCodes.AddRange(_methodDescription.ExpectedStatusCodes);
        }

        protected ApiRequest(ApiRequest<TRes> origin)
            :this(origin._baseUrl, origin._methodDescription, origin._paramAppliers, origin._httpClientProvider, origin._returnType)
        {
            RequestModifiers.AddRange(origin.RequestModifiers);
        }

        /// <summary>
        /// Clones an object
        /// </summary>
        public ApiRequest<TRes> Clone()
        {
            return new ApiRequest<TRes>(this);
        }

        /// <summary>
        /// Send request and return serialized response
        /// </summary>
        public async Task<TRes> GetResult(CancellationToken cancellationToken)
        {
            var resp = await SendRequestAsync(cancellationToken);

            await IsStatusCodeUnexpected(resp.Response, true);

            return (TRes) await ResponseProcessing.DeserializeContent(
                _returnType, 
                resp.Response.Content, 
                resp.Response.StatusCode);
        }

        /// <summary>
        /// Send request and return detailed information about operation
        /// </summary>
        public async Task<CallDetails<TRes>> GetDetailed(CancellationToken cancellationToken)
        {
            var resp = await SendRequestAsync(cancellationToken);
            var respContent = await ResponseProcessing.DeserializeContent(
                _returnType, 
                resp.Response.Content, 
                resp.Response.StatusCode);

            var msgDumper = new HttpMessageDumper();
            var reqDump = await msgDumper.Dump(resp.Request);
            var respDump = await msgDumper.Dump(resp.Response);
            var isUnexpectedStatusCode = await IsStatusCodeUnexpected(resp.Response, false);

            return new CallDetails<TRes>
            {
                RequestMessage = resp.Request,
                ResponseMessage = resp.Response,
                ResponseContent = (TRes) respContent,
                RequestDump = reqDump,
                ResponseDump = respDump,
                StatusCode = resp.Response.StatusCode,
                IsUnexpectedStatusCode = isUnexpectedStatusCode
            };
        }

        async Task<(HttpResponseMessage Response, HttpRequestMessage Request)> SendRequestAsync(CancellationToken cancellationToken)
        {
            Uri addr;

            try
            {
                addr = new Uri((_baseUrl?.TrimEnd('/') ?? "") + "/" + _methodDescription.Url, UriKind.RelativeOrAbsolute);
            }
            catch (Exception e)
            {
                e.Data.Add("baseUrl", _baseUrl);
                e.Data.Add("methodUrl", _methodDescription.Url);

                throw;
            }

            var reqMsg = new HttpRequestMessage
            {
                Method = _methodDescription.HttpMethod,
                RequestUri = addr
            };

            ApplyParameters(reqMsg);

            ApplyModifiers(reqMsg);

            HttpResponseMessage response;
            HttpClient httpClient = null;

            try
            {
                httpClient = _httpClientProvider.Provide();
                response = await httpClient.SendAsync(reqMsg, cancellationToken);
            }
            catch (Exception e)
            {
                e.Data.Add("httpClient is null", httpClient == null);

                if (httpClient != null) 
                {
                    e.Data.Add("httpClient.BaseAddress", httpClient.BaseAddress);
                }

                throw;
            }

            return (response, reqMsg);
        }

        private void ApplyParameters(HttpRequestMessage reqMsg)
        {
            foreach (var pApplier in _paramAppliers)
            {
                pApplier.Apply(reqMsg);
            }
        }

        private void ApplyModifiers(HttpRequestMessage reqMsg)
        {
            foreach (var requestModifier in RequestModifiers)
            {
                try
                {
                    requestModifier?.Modify(reqMsg);
                }
                catch (Exception e)
                {
                    throw new ApiClientException("An error occured while request modifying", e);
                }
            }
        }

        private async Task<bool> IsStatusCodeUnexpected(HttpResponseMessage response, bool throwIfTrue)
        {
            if (response.StatusCode != HttpStatusCode.OK &&
                !ExpectedCodes.Contains(response.StatusCode))
            {
                var contentString = await GetMessageFromResponseContent(response.Content);
                string msg = !string.IsNullOrWhiteSpace(contentString) 
                    ? contentString
                    : response.ReasonPhrase;

                if(throwIfTrue)
                    throw new ResponseCodeException(response.StatusCode, msg);
                else
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<string> GetMessageFromResponseContent(HttpContent responseContent)
        {
            var contentStream = await responseContent.ReadAsStreamAsync();

            using (var rdr = new StreamReader(contentStream))
            {
                var buff = new char[1024];
                var read = await rdr.ReadBlockAsync(buff, 0, 1024);

                return new string(buff, 0, read);
            }
        }
    }
}