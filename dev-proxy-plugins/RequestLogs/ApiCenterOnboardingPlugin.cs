// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.RequestLogs.ApiCenter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Microsoft.DevProxy.Plugins.RequestLogs
{
    internal class ApiCenterOnboardingPluginConfiguration
    {
        public string SubscriptionId { get; set; } = "";
        public string ResourceGroupName { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string WorkspaceName { get; set; } = "default";
        public bool CreateApicEntryForNewApis { get; set; } = true;
    }

    public class ApiCenterOnboardingPlugin : BaseProxyPlugin
    {
        private ApiCenterOnboardingPluginConfiguration _configuration = new();
        private readonly string[] _scopes = ["https://management.azure.com/.default"];
        private readonly TokenCredential _credential = new ChainedTokenCredential(
            new VisualStudioCredential(),
            new VisualStudioCodeCredential(),
            new AzureCliCredential(),
            new AzurePowerShellCredential(),
            new AzureDeveloperCliCredential()
        );
        private HttpClient? _httpClient;
        private JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public override string Name => nameof(ApiCenterOnboardingPlugin);

        public override void Register(IPluginEvents pluginEvents,
                                IProxyContext context,
                                ISet<UrlToWatch> urlsToWatch,
                                IConfigurationSection? configSection = null)
        {
            base.Register(pluginEvents, context, urlsToWatch, configSection);

            configSection?.Bind(_configuration);

            if (string.IsNullOrEmpty(_configuration.SubscriptionId))
            {
                _logger?.LogError("Specify SubscriptionId in the ApiCenterOnboardingPlugin configuration. The ApiCenterOnboardingPlugin will not be used.");
                return;
            }
            if (string.IsNullOrEmpty(_configuration.ResourceGroupName))
            {
                _logger?.LogError("Specify ResourceGroupName in the ApiCenterOnboardingPlugin configuration. The ApiCenterOnboardingPlugin will not be used.");
                return;
            }
            if (string.IsNullOrEmpty(_configuration.ServiceName))
            {
                _logger?.LogError("Specify ServiceName in the ApiCenterOnboardingPlugin configuration. The ApiCenterOnboardingPlugin will not be used.");
                return;
            }

            try
            {
                _ = _credential.GetTokenAsync(new TokenRequestContext(_scopes), CancellationToken.None).Result;
            }
            catch (AuthenticationFailedException ex)
            {
                _logger?.LogError(ex, "Failed to authenticate with Azure. The ApiCenterOnboardingPlugin will not be used.");
                return;
            }

            var authenticationHandler = new AuthenticationDelegatingHandler(_credential, _scopes)
            {
                InnerHandler = new HttpClientHandler()
            };
            _httpClient = new HttpClient(authenticationHandler);

            pluginEvents.AfterRecordingStop += AfterRecordingStop;
        }

        private async Task AfterRecordingStop(object sender, RecordingArgs e)
        {
            if (!e.RequestLogs.Any())
            {
                _logger?.LogDebug("No requests to process");
                return;
            }

            _logger?.LogInformation("Checking if recorded API requests belong to APIs in API Center...");

            Debug.Assert(_httpClient is not null);

            var apis = await LoadApisFromApiCenter();
            if (apis == null || !apis.Value.Any())
            {
                _logger?.LogInformation("No APIs found in API Center");
                return;
            }

            var apiDefinitions = await LoadApiDefinitions(apis.Value);

            var newApis = new List<Tuple<string, string>>();
            var interceptedRequests = e.RequestLogs
                .Where(l => l.MessageType == MessageType.InterceptedRequest)
                .Select(request => {
                    var methodAndUrl = request.MessageLines.First().Split(' ');
                    return new Tuple<string, string>(methodAndUrl[0], methodAndUrl[1]);
                })
                .Distinct();
            foreach (var request in interceptedRequests)
            {
                _logger?.LogDebug("Processing request {method} {url}...", request.Item1, request.Item2);

                var requestMethod = request.Item1;
                var requestUrl = request.Item2;

                var apiDefinition = apiDefinitions.FirstOrDefault(x => requestUrl.Contains(x.Key)).Value;
                if (apiDefinition.Id is null)
                {
                    _logger?.LogDebug("No matching API definition not found for {requestUrl}. Adding new API...", requestUrl);
                    newApis.Add(new(requestMethod, requestUrl));
                    continue;
                }

                await EnsureApiDefinition(apiDefinition);

                if (apiDefinition.Definition is null)
                {
                    _logger?.LogDebug("API definition not found for {requestUrl} so nothing to compare to. Adding new API...", requestUrl);
                    newApis.Add(new(requestMethod, requestUrl));
                    continue;
                }

                var pathItem = FindMatchingPathItem(requestUrl, apiDefinition.Definition);
                if (pathItem is null)
                {
                    _logger?.LogDebug("No matching path found for {requestUrl}. Adding new API...", requestUrl);
                    newApis.Add(new(requestMethod, requestUrl));
                    continue;
                }

                var operation = pathItem.Operations.FirstOrDefault(x => x.Key.ToString().Equals(requestMethod, StringComparison.OrdinalIgnoreCase)).Value;
                if (operation is null)
                {
                    _logger?.LogDebug("No matching operation found for {requestMethod} {requestUrl}. Adding new API...", requestMethod, requestUrl);

                    newApis.Add(new(requestMethod, requestUrl));
                    continue;
                }
            }

            if (!newApis.Any())
            {
                _logger?.LogInformation("No new APIs found");
                return;
            }

            // dedupe newApis
            newApis = newApis.Distinct().ToList();

            var apisPerHost = newApis.GroupBy(x => new Uri(x.Item2).Host);

            var newApisMessageChunks = new List<string>(["New APIs that aren't registered in Azure API Center:", ""]);
            foreach (var apiPerHost in apisPerHost)
            {
                newApisMessageChunks.Add($"{apiPerHost.Key}:");
                newApisMessageChunks.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}"));
            }

            _logger?.LogInformation(string.Join(Environment.NewLine, newApisMessageChunks));

            if (!_configuration.CreateApicEntryForNewApis)
            {
                return;
            }

            await CreateApisInApiCenter(apisPerHost);
        }

        async Task CreateApisInApiCenter(IEnumerable<IGrouping<string, Tuple<string, string>>> apisPerHost)
        {
            Debug.Assert(_httpClient is not null);

            _logger?.LogInformation("{newLine}Creating new API entries in API Center...", Environment.NewLine);

            foreach (var apiPerHost in apisPerHost)
            {
                var host = apiPerHost.Key;
                // trim to 50 chars which is max length for API name
                var apiName = MaxLength($"new-{host.Replace(".", "-")}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", 50);
                _logger?.LogInformation("  Creating API {apiName} for {host}...", apiName, host);

                var title = $"New APIs: {host}";
                var description = new List<string>(["New APIs discovered by Dev Proxy", ""]);
                description.AddRange(apiPerHost.Select(a => $"  {a.Item1} {a.Item2}").ToArray());
                var payload = new
                {
                    properties = new
                    {
                        title,
                        description = string.Join(Environment.NewLine, description),
                        kind = "REST",
                        type = "rest"
                    }
                };
                var content = new StringContent(JsonSerializer.Serialize(payload, _jsonSerializerOptions), Encoding.UTF8, "application/json");
                var createRes = await _httpClient.PutAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis/{apiName}?api-version=2024-03-01", content);
                if (createRes.IsSuccessStatusCode)
                {
                    _logger?.LogDebug("API created successfully");
                }
                else
                {
                    _logger?.LogError("Failed to create API {apiName} for {host}", apiName, host);
                }
                var createResContent = await createRes.Content.ReadAsStringAsync();
                _logger?.LogDebug(createResContent);
            }

            _logger?.LogInformation("DONE");
        }

        async Task<Collection<Api>?> LoadApisFromApiCenter()
        {
            Debug.Assert(_httpClient is not null);

            _logger?.LogInformation("Loading APIs from API Center...");

            var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis?api-version=2024-03-01");
            return JsonSerializer.Deserialize<Collection<Api>>(res, _jsonSerializerOptions);
        }

        OpenApiPathItem? FindMatchingPathItem(string requestUrl, OpenApiDocument openApiDocument)
        {
            foreach (var path in openApiDocument.Paths)
            {
                var urlPath = path.Key;
                _logger?.LogDebug("Checking path {urlPath}...", urlPath);

                // check if path contains parameters. If it does,
                // replace them with regex
                if (urlPath.Contains('{'))
                {
                    _logger?.LogDebug("Path {urlPath} contains parameters and will be converted to Regex", urlPath);

                    foreach (var parameter in path.Value.Parameters)
                    {
                        urlPath = urlPath.Replace($"{{{parameter.Name}}}", $"([^/]+)");
                    }

                    _logger?.LogDebug("Converted path to Regex: {urlPath}", urlPath);
                    var regex = new Regex(urlPath);
                    if (regex.IsMatch(requestUrl))
                    {
                        _logger?.LogDebug("Regex matches {requestUrl}", requestUrl);

                        return path.Value;
                    }

                    _logger?.LogDebug("Regex does not match {requestUrl}", requestUrl);
                }
                else
                {
                    if (requestUrl.Contains(urlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogDebug("{requestUrl} contains {urlPath}", requestUrl, urlPath);

                        return path.Value;
                    }

                    _logger?.LogDebug("{requestUrl} doesn't contain {urlPath}", requestUrl, urlPath);
                }
            }

            return null;
        }

        async Task<Dictionary<string, ApiDefinition>> LoadApiDefinitions(Api[] apis)
        {
            _logger?.LogInformation("Loading API definitions from API Center...");

            var apiDefinitions = new Dictionary<string, ApiDefinition>();

            foreach (var api in apis)
            {
                Debug.Assert(api.Name is not null);

                var apiName = api.Name;
                _logger?.LogDebug("Loading API definitions for {apiName}...", apiName);

                var deployments = await LoadApiDeployments(apiName);
                if (deployments == null || !deployments.Value.Any())
                {
                    _logger?.LogDebug("No deployments found for API {apiName}", apiName);
                    continue;
                }

                foreach (var deployment in deployments.Value)
                {
                    Debug.Assert(deployment?.Properties?.Server is not null);
                    Debug.Assert(deployment?.Properties?.DefinitionId is not null);

                    if (!deployment.Properties.Server.RuntimeUri.Any())
                    {
                        _logger?.LogDebug("No runtime URIs found for deployment {deploymentName}", deployment.Name);
                        continue;
                    }

                    foreach (var runtimeUri in deployment.Properties.Server.RuntimeUri)
                    {
                        apiDefinitions.Add(runtimeUri, new ApiDefinition
                        {
                            Id = deployment.Properties.DefinitionId
                        });
                    }
                }
            }

            return apiDefinitions;
        }

        async Task<Collection<ApiDeployment>?> LoadApiDeployments(string apiName)
        {
            Debug.Assert(_httpClient is not null);

            _logger?.LogDebug("Loading API deployments for {apiName}...", apiName);

            var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis/{apiName}/deployments?api-version=2024-03-01");
            return JsonSerializer.Deserialize<Collection<ApiDeployment>>(res, _jsonSerializerOptions);
        }

        async Task EnsureApiDefinition(ApiDefinition apiDefinition)
        {
            Debug.Assert(_httpClient is not null);

            if (apiDefinition.Properties is not null)
            {
                _logger?.LogDebug("API definition already loaded for {apiDefinitionId}", apiDefinition.Id);
                return;
            }

            _logger?.LogDebug("Loading API definition for {apiDefinitionId}...", apiDefinition.Id);

            var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}{apiDefinition.Id}?api-version=2024-03-01");
            var definition = JsonSerializer.Deserialize<ApiDefinition>(res, _jsonSerializerOptions);
            if (definition is null)
            {
                _logger?.LogError("Failed to deserialize API definition for {apiDefinitionId}", apiDefinition.Id);
                return;
            }

            apiDefinition.Properties = definition.Properties;
            if (apiDefinition.Properties?.Specification?.Name != "openapi")
            {
                _logger?.LogDebug("API definition is not OpenAPI for {apiDefinitionId}", apiDefinition.Id);
                return;
            }

            var definitionRes = await _httpClient.PostAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}{apiDefinition.Id}/exportSpecification?api-version=2024-03-01", null);
            var exportResult = await definitionRes.Content.ReadFromJsonAsync<ApiSpecExportResult>();
            if (exportResult is null)
            {
                _logger?.LogError("Failed to deserialize exported API definition for {apiDefinitionId}", apiDefinition.Id);
                return;
            }

            if (exportResult.Format != ApiSpecExportResultFormat.Inline)
            {
                _logger?.LogDebug("API definition is not inline for {apiDefinitionId}", apiDefinition.Id);
                return;
            }

            try
            {
                apiDefinition.Definition = new OpenApiStringReader().Read(exportResult.Value, out var diagnostic);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to parse OpenAPI document for {apiDefinitionId}", apiDefinition.Id);
                return;
            }
        }

        private string MaxLength(string input, int maxLength)
        {
            return input.Length <= maxLength ? input : input.Substring(0, maxLength);
        }
    }

    internal class AuthenticationDelegatingHandler : DelegatingHandler
    {
        private readonly TokenCredential _credential;
        private readonly string[] _scopes;

        public AuthenticationDelegatingHandler(TokenCredential credential, string[] scopes)
        {
            _credential = credential;
            _scopes = scopes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var accessToken = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            return await base.SendAsync(request, cancellationToken);
        }
    }
}

#region models

namespace Microsoft.DevProxy.Plugins.RequestLogs.ApiCenter
{
    internal class Collection<T>()
    {
        public T[] Value { get; set; } = [];
    }

    internal class Api
    {
        public ApiProperties? Properties { get; set; }
        public string? Name { get; set; }
    }

    internal class ApiProperties
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApiKind? Kind { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApiLifecycleStage? LifecycleStage { get; set; }
        public ApiContact[] Contacts { get; set; } = [];
        public dynamic CustomProperties { get; set; } = new object();
    }

    internal class ApiContact
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Url { get; set; }
    }

    internal class ApiDeployment
    {
        public ApiDeploymentProperties? Properties { get; set; }
        public string? Name { get; set; }
    }

    internal class ApiDeploymentProperties
    {
        public string? Title { get; set; }
        public string? DefinitionId { get; set; }
        public ApiDeploymentServer? Server { get; set; }
        public dynamic CustomProperties { get; set; } = new object();
    }

    internal class ApiDeploymentServer
    {
        public string[] RuntimeUri { get; set; } = [];
    }

    internal class ApiDefinition
    {
        public string? Id { get; set; }
        public ApiDefinitionProperties? Properties { get; set; }
        public OpenApiDocument? Definition { get; set; }
    }

    internal class ApiDefinitionProperties
    {
        public ApiDefinitionPropertiesSpecification? Specification { get; set; }
    }

    internal class ApiDefinitionPropertiesSpecification
    {
        public string? Name { get; set; }
    }

    internal class ApiSpecExportResult
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApiSpecExportResultFormat? Format { get; set; }
        public string? Value { get; set; }
    }

    internal enum ApiSpecExportResultFormat
    {
        Inline,
        Link
    }

    internal enum ApiKind
    {
        GraphQL,
        gRPC,
        REST,
        SOAP,
        Webhook,
        WebSocket
    }

    internal enum ApiLifecycleStage
    {
        Deprecated,
        Design,
        Development,
        Preview,
        Production,
        Retired,
        Testing
    }

}

#endregion