// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

public class GraphRequestInfo
{
    [JsonPropertyName("requestUrl")]
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
}