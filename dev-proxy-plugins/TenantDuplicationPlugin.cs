using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.Core;

namespace Microsoft.DevProxy.Plugins;

public class TenantDuplicationPluginConfiguration
{
    public List<string> Tenants { get; set; } = new();
}

public class TenantDuplicationPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(TenantDuplicationPlugin);
    private readonly TenantDuplicationPluginConfiguration _configuration = new();
    private readonly HttpClient _httpClient = new();
    private readonly TokenCredential _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions()
    {
        ExcludeInteractiveBrowserCredential = true,
        // fails on Ubuntu
        ExcludeSharedTokenCacheCredential = true
    });

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        PluginEvents.BeforeRequest += OnRequestAsync;
    }

    private async Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        if (UrlsToWatch is null || !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var originalRequest = e.Session.HttpClient.Request;
        var tasks = _configuration.Tenants.Select(tenant => DuplicateRequestAsync(originalRequest, tenant)).ToList();
        var responses = await Task.WhenAll(tasks);

        var mergedResponse = MergeResponses(responses);
        e.Session.GenericResponse(mergedResponse, HttpStatusCode.OK, originalRequest.Headers.ToArray());
    }

    private async Task<HttpResponseMessage> DuplicateRequestAsync(Titanium.Web.Proxy.Http.Request originalRequest, string tenant)
    {
        var requestMessage = new HttpRequestMessage
        {
            RequestUri = new Uri(originalRequest.Url),
            Method = new HttpMethod(originalRequest.Method)
        };

        foreach (var header in originalRequest.Headers)
        {
            requestMessage.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        if (originalRequest.HasBody)
        {
            requestMessage.Content = new StringContent(originalRequest.BodyString);
            requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue(originalRequest.ContentType);
        }

        var accessToken = await GetAccessTokenAsync(tenant);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await _httpClient.SendAsync(requestMessage);
    }

    private async Task<string> GetAccessTokenAsync(string tenant)
    {
        var tokenRequestContext = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }, tenantId: tenant);
        var tokenResult = await _credential.GetTokenAsync(tokenRequestContext, default);

        return tokenResult.Token;
    }

    private string MergeResponses(HttpResponseMessage[] responses)
    {
        var mergedContent = new List<object>();

        foreach (var response in responses)
        {
            var content = response.Content.ReadAsStringAsync().Result;
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(content);

            if (jsonElement.ValueKind == JsonValueKind.Object && jsonElement.TryGetProperty("value", out var valueProperty) && valueProperty.ValueKind == JsonValueKind.Array)
            {
                mergedContent.AddRange(valueProperty.EnumerateArray().Select(element => (object)element));
            }
            else
            {
                mergedContent.Add(jsonElement);
            }
        }

        if (mergedContent.Count > 0 && mergedContent[0] is JsonElement firstElement && firstElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Serialize(mergedContent.SelectMany(element => ((JsonElement)element).EnumerateArray()));
        }

        return JsonSerializer.Serialize(mergedContent.FirstOrDefault());
    }
}
