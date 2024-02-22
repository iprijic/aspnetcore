using System.Linq;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>
/// Service for generating OpenAPI document.
/// </summary>
public class OpenApiDocumentService
{
    /// <summary>
    /// Gets the OpenAPI document.
    /// </summary>
    public OpenApiDocument Document { get; }

    private readonly IApiDescriptionGroupCollectionProvider _apiDescriptionGroupCollectionProvider;
    private readonly OpenApiComponentService _openApiComponentService;
    private readonly IServer _server;
    private readonly HashSet<string> _capturedTags = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiDocumentService"/> class.
    /// </summary>
    /// <param name="apiDescriptionGroupCollectionProvider"></param>
    /// <param name="openApiComponentService"></param>
    /// <param name="server"></param>
    public OpenApiDocumentService(IApiDescriptionGroupCollectionProvider apiDescriptionGroupCollectionProvider, OpenApiComponentService openApiComponentService, IServer server)
    {
        _apiDescriptionGroupCollectionProvider = apiDescriptionGroupCollectionProvider;
        _openApiComponentService = openApiComponentService;
        _server = server;
        Document = GenerateOpenApiDocument();
    }

    internal OpenApiDocument GenerateOpenApiDocument()
    {
        var document = new OpenApiDocument
        {
            Info = GetOpenApiInfo(),
            Servers = GetOpenApiServers(),
            Paths = GetOpenApiPaths(),
            Components = _openApiComponentService.GetOpenApiComponents()
        };
        document.Tags = _capturedTags.Select(tag => new OpenApiTag { Name = tag }).ToList();
        return document;
    }

    private static OpenApiInfo GetOpenApiInfo()
    {
        var assembly = Assembly.GetEntryAssembly();
        var assemblyName = assembly?.GetName().Name;
        return new OpenApiInfo
        {
            Title = assemblyName,
            Version = "1.0"
        };
    }

    private IList<OpenApiServer> GetOpenApiServers()
    {
        IList<OpenApiServer> servers = [];
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            servers.Add(new OpenApiServer { Url = address });
        }
        return servers;
    }

    private OpenApiPaths GetOpenApiPaths()
    {
        var descriptionsByPath = _apiDescriptionGroupCollectionProvider.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .GroupBy(apiDesc => apiDesc.RelativePath);
        var paths = new OpenApiPaths();
        foreach (var descriptions in descriptionsByPath)
        {
            Debug.Assert(descriptions.Key != null, "Relative paths cannot be null.");
            var path = descriptions.Key.Trim('/');
            if (!path.StartsWith('/'))
            {
                path = "/" + path;
            }
            paths.Add(path, new OpenApiPathItem { Operations = GetOperations(descriptions) });
        }
        return paths;
    }

    private Dictionary<OperationType, OpenApiOperation> GetOperations(IEnumerable<ApiDescription> descriptions)
    {
        var descriptionsByMethod = descriptions.GroupBy(description => description.HttpMethod);
        var operations = new Dictionary<OperationType, OpenApiOperation>();
        foreach (var item in descriptionsByMethod)
        {
            var httpMethod = item.Key;
            var description = item.SingleOrDefault();
            if (description == null)
            {
                continue;
            }
            var tags = GetTags(description);
            foreach (var tag in tags ?? [])
            {
                _capturedTags.Add(tag.Name);
            }
            operations.Add(httpMethod.ToOperationType(), new OpenApiOperation
            {
                Tags = tags,
                OperationId = GetOperationId(description),
                RequestBody = GetRequestBody(description),
                Responses = GetResponses(description),
                Parameters = GetParameters(description)
            });
        }
        return operations;
    }

    private static string? GetOperationId(ApiDescription description) =>
        description.ActionDescriptor.AttributeRouteInfo?.Name
        ?? (description.ActionDescriptor.EndpointMetadata?.LastOrDefault(m => m is IEndpointNameMetadata) as IEndpointNameMetadata)?.EndpointName
        ?? GenerateOperationId(description);

    private static string GenerateOperationId(ApiDescription description)
    {
        var method = description.HttpMethod;
        var relativePath = description.RelativePath?.Replace("/", "_").Replace('-', '_');
        return method?.ToLowerInvariant() + "_" + relativePath?.Replace("{", string.Empty).Replace("}", string.Empty);
    }

    private static IList<OpenApiTag>? GetTags(ApiDescription description)
    {
        var actionDescriptor = description.ActionDescriptor;
        if (actionDescriptor.EndpointMetadata?.LastOrDefault(m => m is ITagsMetadata) is ITagsMetadata tagsMetadata)
        {
            return tagsMetadata.Tags.Select(tag => new OpenApiTag { Name = tag }).ToList();
        }
        return [new OpenApiTag { Name = description.ActionDescriptor.RouteValues["controller"] }];
    }

    private OpenApiResponses GetResponses(ApiDescription description)
    {
        var supportedResponseTypes = description.SupportedResponseTypes.DefaultIfEmpty(new ApiResponseType { StatusCode = 200 });

        var responses = new OpenApiResponses();
        foreach (var responseType in supportedResponseTypes)
        {
            var statusCode = responseType.IsDefaultResponse ? StatusCodes.Status200OK : responseType.StatusCode;
            responses.Add(statusCode.ToString(CultureInfo.InvariantCulture), GetResponse(description, statusCode, responseType));
        }
        return responses;
    }

    private OpenApiResponse GetResponse(ApiDescription apiDescription, int statusCode, ApiResponseType apiResponseType)
    {
        var description = ReasonPhrases.GetReasonPhrase(statusCode);

        IEnumerable<string> responseContentTypes = [];

        var explicitContentTypes = apiDescription.ActionDescriptor.EndpointMetadata.OfType<ProducesAttribute>().SelectMany(attr => attr.ContentTypes).Distinct();
        if (explicitContentTypes.Any())
        {
            responseContentTypes = explicitContentTypes;
        }

        var apiExplorerContentTypes = apiResponseType.ApiResponseFormats
            .Select(responseFormat => responseFormat.MediaType)
            .Distinct();
        if (apiExplorerContentTypes.Any())
        {
            responseContentTypes = apiExplorerContentTypes;
        }

        return new OpenApiResponse
        {
            Description = description,
            Content = responseContentTypes.ToDictionary(
                contentType => contentType,
                contentType => new OpenApiMediaType { Schema = _openApiComponentService.GetOrCreateOpenApiSchemaForType(apiResponseType.Type!) }
            )
        };
    }

    private OpenApiRequestBody? GetRequestBody(ApiDescription description)
    {
        var supportedRequestFormats = description.SupportedRequestFormats;
        if (supportedRequestFormats.Count == 0)
        {
            supportedRequestFormats = [new ApiRequestFormat { MediaType = "application/json" }];
        }

        var targetParameter = description.ParameterDescriptions.SingleOrDefault(parameter => parameter.Source == BindingSource.Body);
        if (targetParameter == null)
        {
            return null;
        }
        var contentTypes = supportedRequestFormats.Select(requestFormat => requestFormat.MediaType).Distinct();
        var requestBody = new OpenApiRequestBody
        {
            Required = description.ParameterDescriptions.Any(parameter => parameter.Source == BindingSource.Body),
            Content = contentTypes.ToDictionary(
                contentType => contentType,
                contentType => new OpenApiMediaType
                {
                    Schema = _openApiComponentService.GetOrCreateOpenApiSchemaForType(targetParameter.Type, targetParameter)
                }
            )
        };
        return requestBody;
    }

    private List<OpenApiParameter> GetParameters(ApiDescription description)
    {
        return description.ParameterDescriptions
                .Where(parameter =>
                    parameter.Source != BindingSource.Body && parameter.Source != BindingSource.Form)
                .Select(GetParameter)
                .ToList();
    }

    private OpenApiParameter GetParameter(ApiParameterDescription parameterDescription)
    {
        var parameter = new OpenApiParameter
        {
            Name = parameterDescription.Name,
            In = parameterDescription.Source.ToParameterLocation(),
            Required = parameterDescription.Source == BindingSource.Path || parameterDescription.IsRequired,
            Schema = _openApiComponentService.GetOrCreateOpenApiSchemaForType(parameterDescription.Type, parameterDescription)
        };
        return parameter;
    }
}
