using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;

/// <summary>
/// Manages resolving an OpenAPI schema for given types and maintaining the schema cache.
/// </summary>
public class OpenApiComponentService(IOptions<JsonOptions> jsonOptions)
{
    private readonly Dictionary<Type, OpenApiSchema> _typeToOpenApiSchema = new();
    private readonly JsonSerializerOptions _serializerOptions = jsonOptions.Value.SerializerOptions;
    private readonly IJsonTypeInfoResolver? _defaultJsonTypeInfoResolver = jsonOptions.Value.SerializerOptions.TypeInfoResolver;

    internal OpenApiComponents GetOpenApiComponents()
    {
        var components = new OpenApiComponents();
        foreach (var (type, schema) in _typeToOpenApiSchema)
        {
            components.Schemas.Add(GetReferenceId(type), schema);
        }
        return components;
    }

    internal OpenApiSchema GetOrCreateOpenApiSchemaForType(Type type, ApiParameterDescription? parameterDescription = null, bool skipPolymorphismCheck = false)
    {
        if (_defaultJsonTypeInfoResolver is null)
        {
            return new OpenApiSchema { Type = "string" };
        }
        if (_typeToOpenApiSchema.TryGetValue(type, out var cachedSchema))
        {
            return cachedSchema;
        }
        var jsonType = _defaultJsonTypeInfoResolver.GetTypeInfo(type, _serializerOptions);
        if (jsonType == null)
        {
            return new OpenApiSchema { Type = "string" };
        }
        var schema = new OpenApiSchema();
        var useRef = false;
        var addToCache = false;
        if (jsonType.Type == typeof(JsonNode))
        {
            schema.Type = "object";
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = new OpenApiSchema
            {
                Type = "object"
            };
            return schema;
        }
        if (jsonType.Kind == JsonTypeInfoKind.Dictionary)
        {
            schema.Type = "object";
            schema.AdditionalPropertiesAllowed = true;
            var genericTypeArgs = jsonType.Type.GetGenericArguments();
            Type? valueType = null;
            if (genericTypeArgs.Length == 2)
            {
                valueType = jsonType.Type.GetGenericArguments().Last();
            }
            schema.AdditionalProperties = OpenApiTypeMapper.MapTypeToOpenApiPrimitiveType(valueType);
        }
        if (jsonType.Kind == JsonTypeInfoKind.None)
        {
            if (type.IsEnum)
            {
                schema = OpenApiTypeMapper.MapTypeToOpenApiPrimitiveType(type.GetEnumUnderlyingType());
                foreach (var value in Enum.GetValues(type))
                {
                    schema.Enum.Add(new OpenApiInteger((int)value));
                }
            }
            else
            {
                schema = OpenApiTypeMapper.MapTypeToOpenApiPrimitiveType(type);
                var defaultValueAttribute = jsonType.Type.GetCustomAttributes(true).OfType<DefaultValueAttribute>().FirstOrDefault();
                if (defaultValueAttribute != null)
                {
                    schema.Default = OpenApiAnyConverter.GetSpecificOpenApiAny(new OpenApiString(defaultValueAttribute.Value?.ToString()), schema);
                }
                if (parameterDescription is not null && parameterDescription.DefaultValue is not null)
                {
                    schema.Default = OpenApiAnyConverter.GetSpecificOpenApiAny(new OpenApiString(parameterDescription.DefaultValue?.ToString()), schema);
                }
            }
        }
        if (jsonType.Kind == JsonTypeInfoKind.Enumerable)
        {
            schema.Type = "array";
            var elementType = jsonType.Type.GetElementType() ?? jsonType.Type.GetGenericArguments().First();
            schema.Items = OpenApiTypeMapper.MapTypeToOpenApiPrimitiveType(elementType);
        }
        if (jsonType.Kind == JsonTypeInfoKind.Object)
        {
            if (!skipPolymorphismCheck && jsonType.PolymorphismOptions is { } polymorphismOptions && polymorphismOptions.DerivedTypes.Count > 0)
            {
                foreach (var derivedType in polymorphismOptions.DerivedTypes)
                {
                    schema.OneOf.Add(GetOrCreateOpenApiSchemaForType(derivedType.DerivedType));
                }
            }
            else if (jsonType.Type.BaseType is { } baseType && baseType != typeof(object))
            {
                schema.AllOf.Add(GetOrCreateOpenApiSchemaForType(baseType, skipPolymorphismCheck: true));
            }
            addToCache = true;
            useRef = true;
            schema.Type = "object";
            schema.AdditionalPropertiesAllowed = false;
            foreach (var property in jsonType.Properties)
            {
                if (jsonType.Type.GetProperty(property.Name) is { } propertyInfo && propertyInfo.DeclaringType != jsonType.Type)
                {
                    continue;
                }
                var innerSchema = GetOrCreateOpenApiSchemaForType(property.PropertyType);
                var defaultValueAttribute = property.AttributeProvider!.GetCustomAttributes(true).OfType<DefaultValueAttribute>().FirstOrDefault();
                if (defaultValueAttribute != null)
                {
                    innerSchema.Default = OpenApiAnyFactory.CreateFromJson(JsonSerializer.Serialize(defaultValueAttribute.Value));
                }
                innerSchema.ReadOnly = property.Set is null;
                innerSchema.WriteOnly = property.Get is null;
                ApplyValidationAttributes(property.AttributeProvider.GetCustomAttributes(true), innerSchema);
                schema.Properties.Add(property.Name, innerSchema);
            }
        }
        if (parameterDescription?.ParameterDescriptor is IParameterInfoParameterDescriptor parameterInfoParameterDescriptor)
        {
            ApplyValidationAttributes(parameterInfoParameterDescriptor.ParameterInfo.GetCustomAttributes(true), schema);
        }
        if (parameterDescription?.RouteInfo?.Constraints is not null)
        {
            ApplyRouteConstraints(parameterDescription.RouteInfo.Constraints, schema);
        }
        if (addToCache)
        {
            _typeToOpenApiSchema[type] = schema;
        }
        if (useRef)
        {
            schema.Reference = new OpenApiReference { Id = GetReferenceId(type), Type = ReferenceType.Schema };
        }
        return schema;
    }

    private static void ApplyValidationAttributes(object[] attributes, OpenApiSchema schema)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.GetType().IsSubclassOf(typeof(ValidationAttribute)))
            {
                if (attribute is RangeAttribute rangeAttribute)
                {
                    schema.Minimum = decimal.Parse(rangeAttribute.Minimum.ToString()!, CultureInfo.InvariantCulture);
                    schema.Maximum = decimal.Parse(rangeAttribute.Maximum.ToString()!, CultureInfo.InvariantCulture);
                }
                if (attribute is RegularExpressionAttribute regularExpressionAttribute)
                {
                    schema.Pattern = regularExpressionAttribute.Pattern;
                }
                if (attribute is MaxLengthAttribute maxLengthAttribute)
                {
                    schema.MaxLength = maxLengthAttribute.Length;
                }
                if (attribute is MinLengthAttribute minLengthAttribute)
                {
                    schema.MinLength = minLengthAttribute.Length;
                }
                if (attribute is StringLengthAttribute stringLengthAttribute)
                {
                    schema.MinLength = stringLengthAttribute.MinimumLength;
                    schema.MaxLength = stringLengthAttribute.MaximumLength;
                }
            }
        }
    }

    private static void ApplyRouteConstraints(IEnumerable<IRouteConstraint> constraints, OpenApiSchema schema)
    {
        foreach (var constraint in constraints)
        {
            if (constraint is MinRouteConstraint minRouteConstraint)
            {
                schema.Minimum = minRouteConstraint.Min;
            }
            else if (constraint is MaxRouteConstraint maxRouteConstraint)
            {
                schema.Maximum = maxRouteConstraint.Max;
            }
            else if (constraint is MinLengthRouteConstraint minLengthRouteConstraint)
            {
                schema.MinLength = minLengthRouteConstraint.MinLength;
            }
            else if (constraint is MaxLengthRouteConstraint maxLengthRouteConstraint)
            {
                schema.MaxLength = maxLengthRouteConstraint.MaxLength;
            }
            else if (constraint is RangeRouteConstraint rangeRouteConstraint)
            {
                schema.Minimum = rangeRouteConstraint.Min;
                schema.Maximum = rangeRouteConstraint.Max;
            }
            else if (constraint is RegexRouteConstraint regexRouteConstraint)
            {
                schema.Pattern = regexRouteConstraint.Constraint.ToString();
            }
            else if (constraint is LengthRouteConstraint lengthRouteConstraint)
            {
                schema.MinLength = lengthRouteConstraint.MinLength;
                schema.MaxLength = lengthRouteConstraint.MaxLength;
            }
            else if (constraint is FloatRouteConstraint or DecimalRouteConstraint)
            {
                schema.Type = "number";
            }
            else if (constraint is LongRouteConstraint or IntRouteConstraint)
            {
                schema.Type = "integer";
            }
            else if (constraint is GuidRouteConstraint or StringRouteConstraint)
            {
                schema.Type = "string";
            }
            else if (constraint is BoolRouteConstraint)
            {
                schema.Type = "boolean";
            }
        }
    }

    private string GetReferenceId(Type type)
    {
        if (!type.IsConstructedGenericType)
        {
            return type.Name.Replace("[]", "Array");
        }

        var prefix = type.GetGenericArguments()
            .Select(GetReferenceId)
            .Aggregate((previous, current) => previous + current);

        if (IsAnonymousType(type))
        {
            return prefix + "AnonymousType";
        }

        return prefix + type.Name.Split('`').First();
    }

    private static bool IsAnonymousType(Type type) => type.GetTypeInfo().IsClass
        && type.GetTypeInfo().IsDefined(typeof(CompilerGeneratedAttribute))
        && !type.IsNested
        && type.Name.StartsWith("<>", StringComparison.Ordinal)
        && type.Name.Contains("__Anonymous");
}
