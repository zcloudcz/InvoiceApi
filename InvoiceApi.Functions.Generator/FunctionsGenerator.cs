// ============================================================================
// Azure Functions HTTP Trigger Source Generator
//
// This Roslyn Incremental Source Generator automatically creates Azure Functions
// HTTP trigger wrappers for every ASP.NET Core MVC controller action found in
// referenced assemblies (specifically InvoiceApi.API).
//
// How it works:
// 1. Scans referenced assemblies for classes inheriting from ControllerBase
//    that are decorated with [ApiController].
// 2. For each public action method with [HttpGet/Post/Put/Delete], generates
//    a corresponding [Function] + [HttpTrigger] wrapper method.
// 3. The generated function sets up ControllerContext (so the controller has
//    access to HttpContext, User claims, etc.), handles parameter binding
//    (route, query, body), and normalizes the response.
//
// This approach lets us host API endpoints in Azure Functions WITHOUT rewriting
// any controller code — the controllers are injected via DI and called directly.
//
// Limitations:
// - CreatedAtAction results are converted to ObjectResult(201) since MVC routing
//   is not available in Azure Functions.
// - Model validation (ModelState) is not invoked — controllers that rely on it
//   should validate manually.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace InvoiceApi.Functions.Generator;

/// <summary>
/// Roslyn Incremental Source Generator that produces Azure Functions HTTP trigger
/// wrapper classes for each API controller found in referenced assemblies.
/// </summary>
[Generator]
public class FunctionsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the static helper class (always emitted, no analysis needed).
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("FunctionResultHelper.g.cs",
                SourceText.From(CodeEmitter.EmitHelperClass(), Encoding.UTF8));
        });

        // Main generation: scan referenced assemblies for controllers.
        // Uses CompilationProvider to access the full compilation including references.
        // Re-runs on every compilation change — acceptable for ~15 controllers.
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
        {
            Execute(spc, compilation);
        });
    }

    /// <summary>
    /// Main execution: finds controllers in referenced assemblies and generates
    /// one .g.cs file per controller with Azure Functions HTTP trigger wrappers.
    /// </summary>
    private static void Execute(SourceProductionContext context, Compilation compilation)
    {
        // Resolve ASP.NET Core base types needed for analysis.
        // If these types aren't found, the API project isn't referenced — skip generation.
        var controllerBaseType = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ControllerBase");
        if (controllerBaseType == null) return;

        var apiControllerAttr = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
        if (apiControllerAttr == null) return;

        // Find all controller classes in referenced assemblies
        var controllers = FindControllers(compilation, controllerBaseType, apiControllerAttr);

        // Generate a source file for each controller
        foreach (var controller in controllers)
        {
            var source = CodeEmitter.EmitControllerFunctions(controller);
            context.AddSource(
                $"{controller.FunctionClassName}.g.cs",
                SourceText.From(source, Encoding.UTF8));
        }
    }

    // ========================================================================
    // Controller Discovery
    // ========================================================================

    /// <summary>
    /// Searches all referenced assemblies for classes that inherit from ControllerBase
    /// and are decorated with [ApiController]. Only scans assemblies whose name
    /// contains "InvoiceApi" to avoid scanning third-party libraries.
    /// </summary>
    private static List<ControllerInfo> FindControllers(
        Compilation compilation,
        INamedTypeSymbol controllerBaseType,
        INamedTypeSymbol apiControllerAttrType)
    {
        var result = new List<ControllerInfo>();

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            // Only scan our own API assembly — skip third-party and framework assemblies
            if (assembly.Name == null ||
                !assembly.Name.Contains("InvoiceApi.API"))
                continue;

            FindControllersInNamespace(
                assembly.GlobalNamespace, controllerBaseType, apiControllerAttrType, result);
        }

        return result;
    }

    /// <summary>
    /// Recursively searches a namespace for controller types.
    /// </summary>
    private static void FindControllersInNamespace(
        INamespaceSymbol ns,
        INamedTypeSymbol controllerBaseType,
        INamedTypeSymbol apiControllerAttrType,
        List<ControllerInfo> result)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind == TypeKind.Class &&
                !type.IsAbstract &&
                InheritsFrom(type, controllerBaseType) &&
                HasAttribute(type, apiControllerAttrType))
            {
                var controller = AnalyzeController(type);
                if (controller != null)
                    result.Add(controller);
            }
        }

        // Recurse into child namespaces
        foreach (var childNs in ns.GetNamespaceMembers())
        {
            FindControllersInNamespace(childNs, controllerBaseType, apiControllerAttrType, result);
        }
    }

    // ========================================================================
    // Controller Analysis
    // ========================================================================

    /// <summary>
    /// Extracts all metadata from a controller class needed for code generation:
    /// route prefix, authorization requirements, and action method details.
    /// </summary>
    private static ControllerInfo AnalyzeController(INamedTypeSymbol type)
    {
        // Extract route prefix from [Route("api/[controller]")]
        var routeTemplate = GetAttributeStringArg(type, "RouteAttribute") ?? "";

        // Resolve the [controller] placeholder to the actual controller name (lowercase)
        var controllerName = type.Name;
        if (controllerName.EndsWith("Controller"))
            controllerName = controllerName.Substring(0, controllerName.Length - "Controller".Length);

        routeTemplate = routeTemplate.Replace("[controller]", controllerName.ToLower());

        // Get class-level authorization info
        var classAuth = GetAuthInfo(type.GetAttributes());

        // Analyze each public action method
        var actions = new List<ActionInfo>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.DeclaredAccessibility != Accessibility.Public) continue;
            if (method.IsStatic) continue;
            if (method.MethodKind != MethodKind.Ordinary) continue;

            var action = AnalyzeAction(method, routeTemplate, classAuth);
            if (action != null)
                actions.Add(action);
        }

        if (actions.Count == 0) return null;

        // Collect all namespaces needed by the generated code (for using directives).
        // Uses the actual namespace from Roslyn symbols (not string parsing) to correctly
        // handle nested types like InvoiceTemplateController.CreateTemplateFromInvoiceRequest.
        var namespaces = new HashSet<string>();
        namespaces.Add(type.ContainingNamespace.ToDisplayString());
        foreach (var action in actions)
        {
            foreach (var param in action.Parameters)
            {
                if (!string.IsNullOrEmpty(param.TypeNamespace) &&
                    !param.TypeNamespace.StartsWith("System"))
                    namespaces.Add(param.TypeNamespace);
            }
        }

        return new ControllerInfo
        {
            ClassName = type.Name,
            FullyQualifiedName = type.ToDisplayString(),
            Namespace = type.ContainingNamespace.ToDisplayString(),
            FunctionClassName = controllerName + "Functions",
            RoutePrefix = routeTemplate,
            ClassAuth = classAuth,
            Actions = actions,
            RequiredNamespaces = namespaces.ToList()
        };
    }

    /// <summary>
    /// Analyzes a single controller action method: HTTP verb, route template,
    /// parameter bindings, and authorization requirements.
    /// </summary>
    private static ActionInfo AnalyzeAction(
        IMethodSymbol method,
        string routePrefix,
        AuthInfo classAuth)
    {
        // Determine the HTTP method and action-level route template
        string httpMethod = null;
        string actionRoute = null;

        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            switch (attrName)
            {
                case "HttpGetAttribute":
                    httpMethod = "get";
                    break;
                case "HttpPostAttribute":
                    httpMethod = "post";
                    break;
                case "HttpPutAttribute":
                    httpMethod = "put";
                    break;
                case "HttpDeleteAttribute":
                    httpMethod = "delete";
                    break;
                case "HttpPatchAttribute":
                    httpMethod = "patch";
                    break;
            }

            if (httpMethod != null)
            {
                // HTTP attribute may have a route template argument: [HttpGet("paged")]
                actionRoute = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value?.ToString()
                    : null;
                break;
            }
        }

        // Not an action method if no HTTP attribute found
        if (httpMethod == null) return null;

        // Build the full route by combining class route prefix + action route
        var fullRoute = string.IsNullOrEmpty(actionRoute)
            ? routePrefix
            : routePrefix + "/" + actionRoute;

        // Determine effective authorization (method-level overrides class-level)
        var methodAuth = GetAuthInfo(method.GetAttributes());
        var effectiveAuth = methodAuth ?? classAuth;

        // Analyze each parameter
        var parameters = new List<ParamInfo>();
        foreach (var param in method.Parameters)
        {
            parameters.Add(AnalyzeParameter(param, fullRoute));
        }

        // Determine if the method is async (returns Task<T>)
        bool isAsync = IsTaskReturn(method.ReturnType);

        // Build unique function name: {ControllerShortName}_{MethodName}
        var controllerShortName = method.ContainingType.Name;
        if (controllerShortName.EndsWith("Controller"))
            controllerShortName = controllerShortName.Substring(
                0, controllerShortName.Length - "Controller".Length);

        return new ActionInfo
        {
            MethodName = method.Name,
            FunctionName = controllerShortName + "_" + method.Name,
            HttpMethod = httpMethod,
            Route = fullRoute,
            IsAsync = isAsync,
            HasBodyParam = parameters.Any(p => p.Source == BindingSource.Body),
            Parameters = parameters,
            Auth = effectiveAuth
        };
    }

    /// <summary>
    /// Analyzes a method parameter to determine its binding source
    /// (route, query, body, or cancellation token) and type information.
    /// </summary>
    private static ParamInfo AnalyzeParameter(IParameterSymbol param, string route)
    {
        var source = BindingSource.Query; // Default binding source

        // Check for explicit binding attributes
        foreach (var attr in param.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "FromBodyAttribute":
                    source = BindingSource.Body;
                    break;
                case "FromQueryAttribute":
                    source = BindingSource.Query;
                    break;
                case "FromRouteAttribute":
                    source = BindingSource.Route;
                    break;
            }
        }

        // CancellationToken is always from the request's RequestAborted token
        if (param.Type.ToDisplayString().Contains("CancellationToken"))
        {
            source = BindingSource.CancellationToken;
        }
        // If no explicit attribute and the parameter name appears in the route template,
        // it's implicitly a route parameter (e.g., "long id" with route "{id}")
        else if (source == BindingSource.Query &&
                 !param.GetAttributes().Any(a =>
                     a.AttributeClass?.Name == "FromQueryAttribute") &&
                 route.Contains("{" + param.Name))
        {
            source = BindingSource.Route;
        }

        // Get the fully qualified type name for code generation
        var fullType = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var shortType = param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // Determine if this is a nullable value type (e.g., long?, DateTime?)
        bool isNullable = param.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                          (param.Type is INamedTypeSymbol nt &&
                           nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

        // Check if this is a "simple" type (string, int, long, bool, DateTime, enum, Guid)
        // vs a complex DTO that needs special handling for query binding
        bool isSimple = IsSimpleType(param.Type);

        // Check if the type is an enum (or nullable enum)
        bool isEnum = IsEnumType(param.Type);

        // Get default value if present (e.g., "int size = 10").
        // Boolean values need special handling: Roslyn's ToString() returns "True"/"False"
        // but C# requires lowercase "true"/"false" literals.
        string defaultValue = null;
        bool hasDefault = param.HasExplicitDefaultValue;
        if (hasDefault && param.ExplicitDefaultValue != null)
        {
            var dv = param.ExplicitDefaultValue;
            if (dv is bool boolVal)
                defaultValue = boolVal ? "true" : "false";
            else
                defaultValue = dv.ToString();
        }

        // Get the actual namespace from the Roslyn symbol (handles nested types correctly).
        // For nested types like Controller.InnerDto, this returns the namespace of the controller,
        // NOT "Controller.InnerDto" which would be wrong for a using directive.
        var actualType = param.Type;
        // Unwrap Nullable<T> to get the underlying type's namespace
        if (actualType is INamedTypeSymbol nts &&
            nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nts.TypeArguments.Length == 1)
        {
            actualType = nts.TypeArguments[0];
        }
        var typeNamespace = actualType.ContainingNamespace?.ToDisplayString() ?? "";

        return new ParamInfo
        {
            Name = param.Name,
            ShortTypeName = shortType,
            FullTypeName = fullType,
            TypeNamespace = typeNamespace,
            Source = source,
            IsNullable = isNullable,
            IsSimpleType = isSimple,
            IsEnum = isEnum,
            HasDefaultValue = hasDefault,
            DefaultValue = defaultValue
        };
    }

    // ========================================================================
    // Attribute Helpers
    // ========================================================================

    /// <summary>
    /// Extracts authorization info from a list of attributes.
    /// Returns null if no auth-related attributes are present.
    /// </summary>
    private static AuthInfo GetAuthInfo(ImmutableArray<AttributeData> attributes)
    {
        // Check for [AllowAnonymous] — overrides any class-level [Authorize]
        if (attributes.Any(a => a.AttributeClass?.Name == "AllowAnonymousAttribute"))
        {
            return new AuthInfo { AllowAnonymous = true };
        }

        // Check for [Authorize] or [Authorize(Roles = "...")]
        var authorizeAttr = attributes.FirstOrDefault(
            a => a.AttributeClass?.Name == "AuthorizeAttribute");
        if (authorizeAttr != null)
        {
            string roles = null;

            // Check named arguments for Roles = "Admin,SysAdmin"
            foreach (var arg in authorizeAttr.NamedArguments)
            {
                if (arg.Key == "Roles")
                    roles = arg.Value.Value?.ToString();
            }

            // Also check constructor arguments (some patterns use positional)
            if (roles == null && authorizeAttr.ConstructorArguments.Length > 0)
            {
                var firstArg = authorizeAttr.ConstructorArguments[0].Value?.ToString();
                // If the constructor arg looks like a roles list, treat it as roles
                if (firstArg != null && firstArg.Contains(","))
                    roles = firstArg;
            }

            return new AuthInfo { RequireAuth = true, Roles = roles };
        }

        return null; // No auth attributes present
    }

    /// <summary>
    /// Gets the first string constructor argument of a named attribute.
    /// Used to extract route templates from [Route("api/...")] attributes.
    /// </summary>
    private static string GetAttributeStringArg(INamedTypeSymbol type, string attributeName)
    {
        var attr = type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == attributeName);
        if (attr == null) return null;
        return attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value?.ToString()
            : null;
    }

    // ========================================================================
    // Type Helpers
    // ========================================================================

    /// <summary>
    /// Checks if a type inherits from a given base type (direct or indirect).
    /// </summary>
    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Checks if a type has a specific attribute.
    /// </summary>
    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol attrType)
    {
        return type.GetAttributes().Any(
            a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrType));
    }

    /// <summary>
    /// Determines if a type is "simple" (can be parsed from a single string value):
    /// primitives, string, DateTime, Guid, enums, and their nullable versions.
    /// Complex DTOs (like InvoiceFilterDto) are NOT simple.
    /// </summary>
    private static bool IsSimpleType(ITypeSymbol type)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol nt &&
            nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nt.TypeArguments.Length == 1)
        {
            type = nt.TypeArguments[0];
        }

        // Check for well-known simple types
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
                return true;
        }

        // Enums are simple
        if (type.TypeKind == TypeKind.Enum)
            return true;

        // DateTime and Guid
        var fullName = type.ToDisplayString();
        if (fullName == "System.DateTime" ||
            fullName == "System.DateTimeOffset" ||
            fullName == "System.Guid")
            return true;

        return false;
    }

    /// <summary>
    /// Checks if a type is an enum or Nullable&lt;enum&gt;.
    /// </summary>
    private static bool IsEnumType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum) return true;

        // Check Nullable<TEnum>
        if (type is INamedTypeSymbol nt &&
            nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nt.TypeArguments.Length == 1 &&
            nt.TypeArguments[0].TypeKind == TypeKind.Enum)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a return type is Task or Task&lt;T&gt; (indicating an async method).
    /// </summary>
    private static bool IsTaskReturn(ITypeSymbol type)
    {
        if (type.Name == "Task" || type.Name == "ValueTask")
            return true;

        var fullName = type.ToDisplayString();
        return fullName.StartsWith("System.Threading.Tasks.Task") ||
               fullName.StartsWith("System.Threading.Tasks.ValueTask");
    }

    /// <summary>
    /// Extracts the namespace portion from a fully qualified type name.
    /// E.g., "global::InvoiceApi.Contracts.Dto.InvoiceDto" → "InvoiceApi.Contracts.Dto"
    /// </summary>
    private static string GetTypeNamespace(string fullyQualifiedName)
    {
        // Remove "global::" prefix
        var name = fullyQualifiedName;
        if (name.StartsWith("global::"))
            name = name.Substring("global::".Length);

        // Remove nullable suffix
        if (name.EndsWith("?"))
            name = name.Substring(0, name.Length - 1);

        var lastDot = name.LastIndexOf('.');
        return lastDot > 0 ? name.Substring(0, lastDot) : "";
    }
}

// ============================================================================
// Data Models — used to pass extracted metadata from analysis to code generation.
// Plain classes (not records) for maximum netstandard2.0 compatibility.
// ============================================================================

/// <summary>
/// Represents a complete controller with all its action methods.
/// </summary>
internal class ControllerInfo
{
    /// <summary>Class name, e.g., "InvoiceController"</summary>
    public string ClassName { get; set; }

    /// <summary>Fully qualified name, e.g., "InvoiceApi.API.Controller.InvoiceController"</summary>
    public string FullyQualifiedName { get; set; }

    /// <summary>Namespace, e.g., "InvoiceApi.API.Controller"</summary>
    public string Namespace { get; set; }

    /// <summary>Generated class name, e.g., "InvoiceFunctions"</summary>
    public string FunctionClassName { get; set; }

    /// <summary>Base route prefix, e.g., "api/invoice"</summary>
    public string RoutePrefix { get; set; }

    /// <summary>Class-level authorization info (may be null)</summary>
    public AuthInfo ClassAuth { get; set; }

    /// <summary>All action methods to generate wrappers for</summary>
    public List<ActionInfo> Actions { get; set; }

    /// <summary>Extra namespaces needed in the generated file's using directives</summary>
    public List<string> RequiredNamespaces { get; set; }
}

/// <summary>
/// Represents a single controller action method.
/// </summary>
internal class ActionInfo
{
    /// <summary>Original method name, e.g., "GetInvoiceById"</summary>
    public string MethodName { get; set; }

    /// <summary>Azure Function name, e.g., "Invoice_GetInvoiceById"</summary>
    public string FunctionName { get; set; }

    /// <summary>HTTP method: "get", "post", "put", "delete"</summary>
    public string HttpMethod { get; set; }

    /// <summary>Full route template, e.g., "api/invoice/{id}"</summary>
    public string Route { get; set; }

    /// <summary>True if the controller method is async (returns Task)</summary>
    public bool IsAsync { get; set; }

    /// <summary>True if any parameter is [FromBody]</summary>
    public bool HasBodyParam { get; set; }

    /// <summary>All method parameters</summary>
    public List<ParamInfo> Parameters { get; set; }

    /// <summary>Effective authorization (method-level overrides class-level)</summary>
    public AuthInfo Auth { get; set; }
}

/// <summary>
/// Represents a method parameter with its binding source and type info.
/// </summary>
internal class ParamInfo
{
    /// <summary>Parameter name, e.g., "id"</summary>
    public string Name { get; set; }

    /// <summary>Short type name, e.g., "long", "CreateInvoiceDto"</summary>
    public string ShortTypeName { get; set; }

    /// <summary>Fully qualified type, e.g., "global::System.Int64"</summary>
    public string FullTypeName { get; set; }

    /// <summary>Actual namespace from Roslyn symbol (handles nested types correctly)</summary>
    public string TypeNamespace { get; set; }

    /// <summary>Where the value comes from: Route, Query, Body, CancellationToken</summary>
    public BindingSource Source { get; set; }

    /// <summary>True if the type is nullable (Nullable&lt;T&gt; or string?)</summary>
    public bool IsNullable { get; set; }

    /// <summary>True if parseable from a single string (primitives, enums, DateTime, Guid)</summary>
    public bool IsSimpleType { get; set; }

    /// <summary>True if the type is an enum or Nullable&lt;enum&gt;</summary>
    public bool IsEnum { get; set; }

    /// <summary>True if the parameter has a default value</summary>
    public bool HasDefaultValue { get; set; }

    /// <summary>Default value as string, e.g., "10"</summary>
    public string DefaultValue { get; set; }
}

/// <summary>
/// Authorization requirements extracted from [Authorize] / [AllowAnonymous].
/// </summary>
internal class AuthInfo
{
    /// <summary>True if [AllowAnonymous] — skip all auth checks</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>True if [Authorize] — require authenticated user</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Required roles (comma-separated), e.g., "Admin,SysAdmin"</summary>
    public string Roles { get; set; }
}

/// <summary>
/// Where a parameter value is bound from in the HTTP request.
/// </summary>
internal enum BindingSource
{
    /// <summary>URL path segment, e.g., /api/invoice/{id}</summary>
    Route,
    /// <summary>URL query string, e.g., ?page=1&amp;pageSize=10</summary>
    Query,
    /// <summary>JSON request body</summary>
    Body,
    /// <summary>CancellationToken from HttpContext.RequestAborted</summary>
    CancellationToken
}
