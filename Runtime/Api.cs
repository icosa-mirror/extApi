using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;

namespace extApi
{
    public class ApiAccessOptions
    {
        public bool EnableRemoteRequests { get; set; }
        public bool EnableCorsHeaders { get; set; }
        public IReadOnlyCollection<string> AllowedCorsOrigins { get; set; } = Array.Empty<string>();
    }

    public class Api : IDisposable
    {
        public const string RoutesEndpointSuffix = "openapi.json";
        public const string RoutesHtmlEndpointSuffix = "docs.html";

        private HttpListener _listener;
        private readonly ThreadMode _threadMode;
        
        private Thread _listenerThread;
        private readonly object _listenerThreadLock = new();
        private readonly Queue<ApiSession> _listenerThreadQueue = new();
        
        private Thread _responseThread;
        private readonly object _responseThreadLock = new();
        private readonly Queue<ApiSession> _responseThreadQueue = new();
        
        private CancellationTokenSource _cancellationSource;
        private CancellationToken _cancellationToken;

        private readonly ApiRouteNode _root = new("/");
        private ApiAccessOptions _accessOptions = new();


        public Api() : this(ThreadMode.OtherThread) { }
        public Api(ThreadMode mode) => _threadMode = mode;

        public void ConfigureAccess(ApiAccessOptions accessOptions)
        {
            _accessOptions = accessOptions ?? new ApiAccessOptions();
        }

        public string GetRoutesEndpoint() => ApiUtils.Combine(GetIntrospectionBasePath(), RoutesEndpointSuffix);

        public string GetRoutesHtmlEndpoint() => ApiUtils.Combine(GetIntrospectionBasePath(), RoutesHtmlEndpointSuffix);

        public ApiRoutesDocument GetRoutesDocument() => CreateRoutesDocument();

        public ApiResult InvokeLocalGet(string path, IReadOnlyDictionary<string, string> queryParameters = null)
            => InvokeLocal(HttpMethod.Get, path, queryParameters);

        public void Listen(ushort port)
        {
            if (_listener != null)
                throw new Exception("Already started");

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
            _listener.Start();

            _cancellationSource = new CancellationTokenSource();
            _cancellationToken = _cancellationSource.Token;
            
            _listenerThread = new Thread(ListenProcess) { Name = "extApi Listen Thread" };
            _listenerThread.Start();

            _responseThread = new Thread(ResponseProcess) { Name = "extApi Response Thread" };
            _responseThread.Start();
        }

        public void Update()
        {
            if (_threadMode != ThreadMode.MainThread)
                throw new Exception($"Available only in {nameof(ThreadMode.MainThread)} mode");

            while (true)
            {
                var session = (ApiSession) null;
                lock (_listenerThreadLock)
                {
                    if (!_listenerThreadQueue.TryDequeue(out session))
                        break;
                }

                ProcessSession(session);
            }
        }

        public void Close()
        {
            _listener?.Stop();
            _listener = null;
            _cancellationSource.Cancel();
            _cancellationSource = null;
            _listenerThread.Abort();
            _listenerThread = null;
            _responseThread.Abort();
            _responseThread = null;
        }

        public void AddController(object controller)
        {
            var controllerType = controller.GetType();
            var controllerRouteAttributes = controllerType.GetCustomAttributes<ApiRouteAttribute>().ToList();
            if (controllerRouteAttributes.Any() == false)
                throw new NullReferenceException(nameof(ApiRouteAttribute)); // No attribute

            var controllerMethods = controllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).ToList();

            foreach (var routeAttribute in controllerRouteAttributes)
            {
                foreach (var controllerMethod in controllerMethods)
                {
                    var methodAttributes = controllerMethod.GetCustomAttributes<ApiMethodAttribute>(true).ToList();
                    if (methodAttributes.Any() == false)
                        continue;

                    foreach (var methodAttribute in methodAttributes)
                    {
                        var routePath = ApiUtils.Combine(routeAttribute.Route, methodAttribute.Template);
                        var routeNode = CreateRouteNode(routePath);
                        if (routeNode == null)
                            throw new Exception("Route build failed");

                        if (routeNode.Methods.ContainsKey(methodAttribute.Method))
                            throw new Exception($"Path \"{routePath}\" already has \"{methodAttribute.Method}\" method");

                        routeNode.Methods.Add(methodAttribute.Method, new ApiRouteTarget
                        {
                            Controller = controller,
                            MethodInfo = controllerMethod,
                            ParameterInfos = controllerMethod.GetParameters(),
                        });
                    }
                }
            }
        }

        private void ListenProcess()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = _listener.GetContext();
                    var contextMethod = new HttpMethod(context.Request.HttpMethod);

                    var isAllowedRequest = TryApplyAccessPolicy(context);

                    if (contextMethod == HttpMethod.Options)
                    {
                        context.Response.StatusCode = isAllowedRequest
                            ? (int)HttpStatusCode.NoContent
                            : (int)HttpStatusCode.Forbidden;
                        context.Response.Close();
                        continue;
                    }

                    if (!isAllowedRequest)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        context.Response.Close();
                        continue;
                    }

                    if (TryHandleIntrospectionRequest(context, contextMethod))
                        continue;
                    
                    var routeUri = context.Request.Url;
                    var routeParameters = new Dictionary<string, string>();
                    var target = GetRouteTarget(contextMethod, routeUri.Segments, routeParameters);
                    if (target != null)
                    {
                        try
                        {
                            var session = target.GetSession(context, routeParameters);

                            if (_threadMode == ThreadMode.OtherThread)
                            {
                                ProcessSession(session); // TODO: Сделать асинхронный отлов. 
                            }
                            else
                            {
                                lock (_listenerThreadLock)
                                {
                                    _listenerThreadQueue.Enqueue(session);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError(e);

                            context.Response.ContentType = "application/json";
                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            context.Response.Close();
                        }
                    }
                    else
                    {
                        context.Response.ContentType = "application/json";
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.Close();
                    }
                }
                catch (ThreadAbortException)
                { }
                catch (Exception e)
                {
                    Debug.LogError(e);
                }
            }
        }
        
        private void ResponseProcess()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ApiSession session;
                    
                    lock (_responseThreadLock)
                    {
                        if (!_responseThreadQueue.TryDequeue(out session))
                            continue;
                    }

                    session.Context.Response.StatusCode = (int)session.Result.StatusCode;
                    byte[] rawData = Array.Empty<byte>();

                    if (session.Result.Location != null)
                    {
                        session.Context.Response.RedirectLocation = session.Result.Location;
                    }
                    else if (session.Result.RawBody != null)
                    {
                        rawData = Encoding.UTF8.GetBytes(session.Result.RawBody);
                        session.Context.Response.ContentType = string.IsNullOrEmpty(session.Result.ContentType)
                            ? "text/plain"
                            : session.Result.ContentType;
                    }
                    else if (session.Result.Json != null)
                    {
                        var json = session.Result.Json;
                        rawData = Encoding.UTF8.GetBytes(json);
                        session.Context.Response.ContentType = "application/json";
                    }

                    session.Context.Response.ContentLength64 = rawData.Length;
                    session.Context.Response.OutputStream.Write(rawData);
                    session.Context.Response.OutputStream.Flush();

                    session.Context.Response.Close();
                }
                catch (ThreadAbortException)
                { }
                catch (Exception e)
                {
                    Debug.LogError(e);
                }
            }
        }

        private bool TryApplyAccessPolicy(HttpListenerContext context)
        {
            if (!context.Request.IsLocal && !_accessOptions.EnableRemoteRequests)
                return false;

            if (!_accessOptions.EnableCorsHeaders)
                return true;

            var origin = context.Request.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin))
                return true;

            if (!TryGetAllowedCorsOrigin(origin, out var allowedOrigin))
                return true;

            context.Response.AddHeader("Access-Control-Allow-Origin", allowedOrigin);
            context.Response.AddHeader("Vary", "Origin");
            context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, HEAD, OPTIONS");
            context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            return true;
        }

        private bool TryGetAllowedCorsOrigin(string origin, out string allowedOrigin)
        {
            if (_accessOptions.AllowedCorsOrigins != null &&
                _accessOptions.AllowedCorsOrigins.Any(allowed => allowed?.Trim() == "*"))
            {
                allowedOrigin = "*";
                return true;
            }

            if (IsLocalCorsOrigin(origin))
            {
                allowedOrigin = origin;
                return true;
            }

            if (_accessOptions.AllowedCorsOrigins != null &&
                _accessOptions.AllowedCorsOrigins.Any(allowed =>
                    string.Equals(allowed?.Trim(), origin, StringComparison.OrdinalIgnoreCase)))
            {
                allowedOrigin = origin;
                return true;
            }

            allowedOrigin = null;
            return false;
        }

        private static bool IsLocalCorsOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            return uri.IsLoopback;
        }

        private bool TryHandleIntrospectionRequest(HttpListenerContext context, HttpMethod contextMethod)
        {
            if (contextMethod != HttpMethod.Get)
                return false;

            var absolutePath = context.Request.Url?.AbsolutePath?.TrimEnd('/');
            if (string.IsNullOrEmpty(absolutePath))
                absolutePath = "/";

            if (string.Equals(absolutePath, GetRoutesEndpoint(), StringComparison.OrdinalIgnoreCase))
            {
                EnqueueResponse(new ApiSession
                {
                    Context = context,
                    Result = ApiResult.Ok(CreateOpenApiDocument())
                });

                return true;
            }

            if (string.Equals(absolutePath, GetRoutesHtmlEndpoint(), StringComparison.OrdinalIgnoreCase))
            {
                EnqueueResponse(new ApiSession
                {
                    Context = context,
                    Result = ApiResult.Html(CreateRoutesHtmlDocument())
                });

                return true;
            }

            return false;
        }

        private ApiResult ExecuteSession(ApiSession session)
        {
            if (session.HasBindingError)
            {
                return session.Result;
            }

            try
            {
                var resultBoxed = session.MethodInfo.Invoke(session.Controller, session.Arguments);
                if (resultBoxed != null)
                {
                    var resultType = resultBoxed.GetType();
                    if (resultType == typeof(ApiResult) ||
                        resultType.IsSubclassOf(typeof(ApiResult)))
                    {
                        session.Result = (ApiResult)resultBoxed;
                    }
                    else
                    {
                        session.Result = ApiResult.Ok(resultBoxed);
                    }
                }
                else
                {
                    session.Result = ApiResult.Ok();
                }
            }
            catch
            {
                session.Result = ApiResult.InternalServerError();
            }

            return session.Result;
        }

        private void ProcessSession(ApiSession session)
        {
            ExecuteSession(session);

            EnqueueResponse(session);
        }

        private void EnqueueResponse(ApiSession session)
        {
            lock (_responseThreadLock)
            {
                _responseThreadQueue.Enqueue(session);
            }
        }

        private object CreateOpenApiDocument()
        {
            var routesDocument = CreateRoutesDocument();
            var paths = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var componentSchemas = new SortedDictionary<string, object>(StringComparer.Ordinal);

            foreach (var route in routesDocument.Routes)
            {
                if (!paths.TryGetValue(route.Path, out var pathItemObject))
                {
                    pathItemObject = new Dictionary<string, object>(StringComparer.Ordinal);
                    paths[route.Path] = pathItemObject;
                }

                var pathItem = (Dictionary<string, object>)pathItemObject;
                pathItem[route.HttpMethod.ToLowerInvariant()] = CreateOpenApiOperation(route, componentSchemas);
            }

            var document = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["openapi"] = "3.0.3",
                ["info"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["title"] = "extApi",
                    ["version"] = "1.0.0"
                },
                ["paths"] = paths
            };

            if (componentSchemas.Count > 0)
            {
                document["components"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["schemas"] = componentSchemas
                };
            }

            return document;
        }

        private ApiRoutesDocument CreateRoutesDocument()
        {
            var routes = new List<ApiRouteDescription>();
            CollectRouteDescriptions(_root, null, routes);

            return new ApiRoutesDocument
            {
                Endpoint = GetRoutesEndpoint(),
                Routes = routes
                    .OrderBy(route => route.Path, StringComparer.Ordinal)
                    .ThenBy(route => route.HttpMethod, StringComparer.Ordinal)
                    .ToList()
            };
        }

        private Dictionary<string, object> CreateOpenApiOperation(ApiRouteDescription route, IDictionary<string, object> componentSchemas)
        {
            var operation = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operationId"] = $"{route.ControllerType}.{route.Action}",
                ["tags"] = new[] { route.ControllerType ?? "Api" },
                ["responses"] = CreateOpenApiResponses(route, componentSchemas)
            };

            if (!string.IsNullOrWhiteSpace(route.Summary))
                operation["summary"] = route.Summary;

            var parameters = new List<object>();
            foreach (var parameter in route.Parameters.Where(parameter => parameter.BindingSources.Contains("route")))
            {
                parameters.Add(CreateOpenApiParameter(parameter, "path", componentSchemas, true,
                    parameter.UsesLegacyBinding
                        ? "Legacy extApi binding also accepts this value from the query string and prefers the query string when both are present."
                        : null));
            }

            foreach (var parameter in route.Parameters.Where(parameter => !parameter.BindingSources.Contains("route") && !parameter.BindingSources.Contains("body")))
            {
                parameters.Add(CreateOpenApiParameter(parameter, "query", componentSchemas, parameter.Required, null));
            }

            if (parameters.Count > 0)
                operation["parameters"] = parameters;

            var bodyParameter = route.Parameters.FirstOrDefault(parameter => parameter.BindingSources.Contains("body"));
            if (bodyParameter != null)
            {
                operation["requestBody"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["required"] = bodyParameter.Required,
                    ["content"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["schema"] = CreateOpenApiSchema(bodyParameter, componentSchemas)
                        },
                        ["application/x-www-form-urlencoded"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["schema"] = CreateOpenApiSchema(bodyParameter, componentSchemas)
                        }
                    }
                };
            }

            return operation;
        }

        private Dictionary<string, object> CreateOpenApiResponses(ApiRouteDescription route, IDictionary<string, object> componentSchemas)
        {
            if (route.Responses != null && route.Responses.Count > 0)
                return CreateAttributedOpenApiResponses(route.Responses, componentSchemas);

            var responses = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["200"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = "Success"
                },
                ["400"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = "Bad request"
                },
                ["500"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = "Internal server error"
                }
            };

            if (TryGetResponseSchema(route, componentSchemas, out var responseSchema))
            {
                responses["200"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = "Success",
                    ["content"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["schema"] = responseSchema
                        }
                    }
                };
            }

            return responses;
        }

        private Dictionary<string, object> CreateAttributedOpenApiResponses(
            IEnumerable<ApiResponseDescription> responseDescriptions,
            IDictionary<string, object> componentSchemas)
        {
            var responses = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var responseDescription in responseDescriptions)
            {
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["description"] = string.IsNullOrEmpty(responseDescription.Description)
                        ? GetDefaultResponseDescription(responseDescription.StatusCode)
                        : responseDescription.Description
                };

                if (responseDescription.ClrType != null)
                {
                    response["content"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["schema"] = CreateOpenApiSchema(responseDescription.ClrType, componentSchemas)
                        }
                    };
                }

                responses[responseDescription.StatusCode.ToString()] = response;
            }

            return responses;
        }

        private bool TryGetResponseSchema(ApiRouteDescription route, IDictionary<string, object> componentSchemas, out object schema)
        {
            schema = null;

            if (route.ReturnType == null)
                return false;

            if (route.ReturnType == typeof(void) ||
                route.ReturnType == typeof(ApiResult) ||
                route.ReturnType.IsSubclassOf(typeof(ApiResult)))
            {
                return false;
            }

            schema = CreateOpenApiSchema(route.ReturnType, componentSchemas);
            return true;
        }

        private static string GetDefaultResponseDescription(int statusCode)
        {
            return statusCode switch
            {
                200 => "Success",
                201 => "Created",
                204 => "No content",
                400 => "Bad request",
                401 => "Unauthorized",
                403 => "Forbidden",
                404 => "Not found",
                409 => "Conflict",
                500 => "Internal server error",
                _ => $"HTTP {statusCode}"
            };
        }

        private Dictionary<string, object> CreateOpenApiParameter(
            ApiParameterDescription parameter,
            string location,
            IDictionary<string, object> componentSchemas,
            bool required,
            string extraDescription)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = parameter.Name,
                ["in"] = location,
                ["required"] = required,
                ["schema"] = CreateOpenApiSchema(parameter, componentSchemas),
                ["x-extapi-binding"] = parameter.BindingSources
            };

            var description = BuildParameterDescription(parameter, extraDescription);
            if (!string.IsNullOrEmpty(description))
                result["description"] = description;

            if (!string.IsNullOrWhiteSpace(parameter.Example))
                result["example"] = parameter.Example;

            return result;
        }

        private object CreateOpenApiSchema(ApiParameterDescription parameter, IDictionary<string, object> componentSchemas)
        {
            var schema = CreateOpenApiSchema(parameter.ClrType, componentSchemas);
            if (schema is Dictionary<string, object> schemaDictionary)
            {
                if (parameter.AllowedValues != null && parameter.AllowedValues.Count > 0)
                    schemaDictionary["enum"] = parameter.AllowedValues.ToArray();

                if (!string.IsNullOrWhiteSpace(parameter.Format))
                    schemaDictionary["format"] = parameter.Format;

                if (!string.IsNullOrWhiteSpace(parameter.Example))
                    schemaDictionary["example"] = ParseOpenApiExample(parameter);

                if (!string.IsNullOrWhiteSpace(parameter.DisplayType))
                    schemaDictionary["x-extapi-displayType"] = parameter.DisplayType;
            }

            return schema;
        }

        private static object ParseOpenApiExample(ApiParameterDescription parameter)
        {
            if (!parameter.BindingSources.Contains("body"))
                return parameter.Example;

            try
            {
                return JsonConvert.DeserializeObject(parameter.Example);
            }
            catch
            {
                return parameter.Example;
            }
        }

        private object CreateOpenApiSchema(Type type, IDictionary<string, object> componentSchemas)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
                type = nullableType;

            if (TryCreatePrimitiveOpenApiSchema(type, out var primitiveSchema))
                return primitiveSchema;

            if (ApiUtils.IsCollectionType(type))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "array",
                    ["items"] = CreateOpenApiSchema(ApiUtils.GetCollectionElementType(type), componentSchemas)
                };
            }

            var schemaId = GetOpenApiSchemaId(type);
            if (!componentSchemas.ContainsKey(schemaId))
            {
                var schema = new Dictionary<string, object>(StringComparer.Ordinal);
                componentSchemas[schemaId] = schema;
                PopulateObjectSchema(type, schema, componentSchemas);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["$ref"] = $"#/components/schemas/{schemaId}"
            };
        }

        private static bool TryCreatePrimitiveOpenApiSchema(Type type, out Dictionary<string, object> schema)
        {
            schema = null;

            if (type.IsEnum)
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["enum"] = Enum.GetNames(type)
                };
                return true;
            }

            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "string" };
                return true;
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["format"] = "date-time"
                };
                return true;
            }

            if (type == typeof(TimeSpan))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "string" };
                return true;
            }

            if (type == typeof(bool))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "boolean" };
                return true;
            }

            if (type == typeof(float))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "number",
                    ["format"] = "float"
                };
                return true;
            }

            if (type == typeof(double) || type == typeof(decimal))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "number",
                    ["format"] = "double"
                };
                return true;
            }

            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "integer",
                    ["format"] = "int32"
                };
                return true;
            }

            if (type == typeof(long) || type == typeof(ulong))
            {
                schema = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "integer",
                    ["format"] = "int64"
                };
                return true;
            }

            return false;
        }

        private void PopulateObjectSchema(Type type, IDictionary<string, object> schema, IDictionary<string, object> componentSchemas)
        {
            schema["type"] = "object";

            var properties = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                properties[field.Name] = CreateOpenApiSchema(field.FieldType, componentSchemas);
            }

            foreach (var property in type
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0))
            {
                properties[property.Name] = CreateOpenApiSchema(property.PropertyType, componentSchemas);
            }

            schema["properties"] = properties;
        }

        private static string GetOpenApiSchemaId(Type type)
        {
            var source = type.FullName ?? type.Name;
            return source
                .Replace("+", ".")
                .Replace("`", "_")
                .Replace("[", "_")
                .Replace("]", "_")
                .Replace(",", "_")
                .Replace(" ", string.Empty);
        }

        private string CreateRoutesHtmlDocument()
        {
            var document = CreateRoutesDocument();
            var html = new StringBuilder();

            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html lang=\"en\">");
            html.AppendLine("<head>");
            html.AppendLine("  <meta charset=\"utf-8\">");
            html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.AppendLine("  <title>API Documentation</title>");
            html.AppendLine("  <style>");
            html.AppendLine("    :root { color-scheme: light; }");
            html.AppendLine("    body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; color: #1f2937; background: #f8fafc; }");
            html.AppendLine("    h1, h2, h3, p { margin: 0; }");
            html.AppendLine("    .page { max-width: 1100px; margin: 0 auto; }");
            html.AppendLine("    .summary { margin: 12px 0 24px; color: #475569; }");
            html.AppendLine("    .route { background: #ffffff; border: 1px solid #dbe2ea; border-radius: 12px; padding: 16px; margin-bottom: 16px; box-shadow: 0 1px 2px rgba(15, 23, 42, 0.04); }");
            html.AppendLine("    .route-header { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 10px; }");
            html.AppendLine("    .method { font-weight: 700; font-size: 12px; letter-spacing: 0.08em; text-transform: uppercase; color: #0f172a; background: #e2e8f0; border-radius: 999px; padding: 6px 10px; }");
            html.AppendLine("    .path { font-family: Consolas, monospace; font-size: 18px; color: #0f172a; }");
            html.AppendLine("    .meta { color: #64748b; font-size: 14px; margin-bottom: 14px; }");
            html.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 8px; }");
            html.AppendLine("    th, td { text-align: left; padding: 10px 12px; border-top: 1px solid #e2e8f0; vertical-align: top; }");
            html.AppendLine("    th { width: 15%; color: #475569; font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; }");
            html.AppendLine("    .empty { color: #64748b; font-style: italic; }");
            html.AppendLine("    .chip { display: inline-block; margin: 0 6px 6px 0; padding: 4px 8px; border-radius: 999px; background: #eef2ff; color: #3730a3; font-size: 12px; }");
            html.AppendLine("    .schema { margin-top: 8px; padding-left: 18px; }");
            html.AppendLine("    .schema ul { margin: 6px 0 0; padding-left: 18px; }");
            html.AppendLine("    .schema li { margin: 4px 0; }");
            html.AppendLine("    details.disclosure { margin: 0; }");
            html.AppendLine("    details.disclosure > summary { cursor: pointer; color: #0f172a; font-weight: 600; }");
            html.AppendLine("    details.disclosure > summary::marker { color: #475569; }");
            html.AppendLine("    details.disclosure > .content { margin-top: 8px; }");
            html.AppendLine("    .try-panel { margin-top: 14px; }");
            html.AppendLine("    .try-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; margin-top: 10px; }");
            html.AppendLine("    .try-field { display: flex; flex-direction: column; gap: 6px; }");
            html.AppendLine("    .try-field label { font-size: 12px; font-weight: 600; color: #334155; text-transform: uppercase; letter-spacing: 0.04em; }");
            html.AppendLine("    .try-field input, .try-field select, .try-field textarea { width: 100%; box-sizing: border-box; border: 1px solid #cbd5e1; border-radius: 8px; padding: 9px 10px; font: inherit; color: #0f172a; background: #fff; }");
            html.AppendLine("    .try-field textarea { min-height: 140px; resize: vertical; font-family: Consolas, monospace; }");
            html.AppendLine("    .try-actions { display: flex; align-items: center; gap: 10px; margin-top: 12px; }");
            html.AppendLine("    .try-button { border: 0; border-radius: 8px; padding: 10px 14px; font: inherit; font-weight: 600; color: #fff; background: #0f172a; cursor: pointer; }");
            html.AppendLine("    .try-button:hover { background: #1e293b; }");
            html.AppendLine("    .try-request { color: #475569; font-size: 13px; }");
            html.AppendLine("    .try-result { margin-top: 12px; padding: 12px; border-radius: 8px; background: #0f172a; color: #e2e8f0; white-space: pre-wrap; word-break: break-word; font-family: Consolas, monospace; font-size: 13px; }");
            html.AppendLine("    .try-result.empty { background: #f8fafc; color: #64748b; border: 1px dashed #cbd5e1; }");
            html.AppendLine("    code { font-family: Consolas, monospace; }");
            html.AppendLine("  </style>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");
            html.AppendLine("  <div class=\"page\">");
            html.AppendLine("    <h1>API Documentation</h1>");
            html.AppendLine($"    <p class=\"summary\">OpenAPI JSON: <code>{HtmlEncode(document.Endpoint)}</code> | HTML: <code>{HtmlEncode(GetRoutesHtmlEndpoint())}</code> | Routes: {document.Routes.Count}</p>");

            foreach (var route in document.Routes)
            {
                html.AppendLine("    <section class=\"route\">");
                html.AppendLine("      <div class=\"route-header\">");
                html.AppendLine($"        <span class=\"method\">{HtmlEncode(route.HttpMethod)}</span>");
                html.AppendLine($"        <div class=\"path\">{HtmlEncode(route.Path)}</div>");
                html.AppendLine("      </div>");
                if (!string.IsNullOrWhiteSpace(route.Summary))
                {
                    html.AppendLine($"      <div class=\"meta\">{HtmlEncode(route.Summary)}</div>");
                }

                html.AppendLine(RenderTryPanel(route));

                if (route.Parameters.Count == 0)
                {
                    html.AppendLine("      <div class=\"empty\">No parameters</div>");
                }
                else
                {
                    html.AppendLine("      <table>");
                    html.AppendLine("        <thead><tr><th>Name</th><th>Type</th><th>Binding</th><th>Required</th><th>Details</th><th>Body Schema</th></tr></thead>");
                    html.AppendLine("        <tbody>");

                    foreach (var parameter in route.Parameters)
                    {
                        html.AppendLine("          <tr>");
                        html.AppendLine($"            <td><code>{HtmlEncode(parameter.Name)}</code></td>");
                        html.AppendLine($"            <td>{RenderParameterType(parameter)}</td>");
                        html.AppendLine($"            <td>{RenderBindingSources(parameter.BindingSources)}</td>");
                        html.AppendLine($"            <td>{(parameter.Required ? "yes" : "no")}</td>");
                        html.AppendLine($"            <td>{RenderParameterDetails(parameter)}</td>");
                        html.AppendLine($"            <td>{RenderBodySchema(parameter.Body)}</td>");
                        html.AppendLine("          </tr>");
                    }

                    html.AppendLine("        </tbody>");
                    html.AppendLine("      </table>");
                }

                var responses = GetHtmlResponses(route);
                if (responses.Count == 0)
                {
                    html.AppendLine("      <div class=\"empty\">No documented responses</div>");
                }
                else
                {
                    html.AppendLine("      <table>");
                    html.AppendLine("        <thead><tr><th>Status</th><th>Description</th><th>Response Schema</th></tr></thead>");
                    html.AppendLine("        <tbody>");

                    foreach (var response in responses)
                    {
                        html.AppendLine("          <tr>");
                        html.AppendLine($"            <td><code>{response.StatusCode}</code></td>");
                        html.AppendLine($"            <td>{HtmlEncode(response.Description)}</td>");
                        html.AppendLine($"            <td>{RenderBodySchema(response.Body)}</td>");
                        html.AppendLine("          </tr>");
                    }

                    html.AppendLine("        </tbody>");
                    html.AppendLine("      </table>");
                }

                html.AppendLine("    </section>");
            }

            html.AppendLine("  </div>");
            html.AppendLine("  <script>");
            html.AppendLine("    async function extApiTryRoute(button) {");
            html.AppendLine("      const form = button.closest('.try-form');");
            html.AppendLine("      const result = form.querySelector('.try-result');");
            html.AppendLine("      const requestLine = form.querySelector('.try-request code');");
            html.AppendLine("      const method = form.dataset.method;");
            html.AppendLine("      let path = form.dataset.path;");
            html.AppendLine("      const query = new URLSearchParams();");
            html.AppendLine("      let body = null;");
            html.AppendLine("      for (const field of form.querySelectorAll('[data-try-binding]')) {");
            html.AppendLine("        const binding = field.dataset.tryBinding;");
            html.AppendLine("        const name = field.dataset.tryName;");
            html.AppendLine("        const value = field.value;");
            html.AppendLine("        if (binding === 'route') {");
            html.AppendLine("          path = path.replace(`{${name}}`, encodeURIComponent(value));");
            html.AppendLine("        } else if (binding === 'query') {");
            html.AppendLine("          if (value !== '') query.append(name, value);");
            html.AppendLine("        } else if (binding === 'body') {");
            html.AppendLine("          body = value;");
            html.AppendLine("        }");
            html.AppendLine("      }");
            html.AppendLine("      const url = query.toString() ? `${path}?${query.toString()}` : path;");
            html.AppendLine("      requestLine.textContent = `${method} ${url}`;");
            html.AppendLine("      result.classList.remove('empty');");
            html.AppendLine("      result.textContent = 'Loading...';");
            html.AppendLine("      const options = { method, headers: {} };");
            html.AppendLine("      if (body !== null && body.trim() !== '') {");
            html.AppendLine("        options.headers['Content-Type'] = 'application/json';");
            html.AppendLine("        options.body = body;");
            html.AppendLine("      }");
            html.AppendLine("      try {");
            html.AppendLine("        const response = await fetch(url, options);");
            html.AppendLine("        const text = await response.text();");
            html.AppendLine("        let pretty = text;");
            html.AppendLine("        try {");
            html.AppendLine("          pretty = text ? JSON.stringify(JSON.parse(text), null, 2) : '';");
            html.AppendLine("        } catch (error) { }");
            html.AppendLine("        result.textContent = `${response.status} ${response.statusText}\\n${pretty}`.trimEnd();");
            html.AppendLine("      } catch (error) {");
            html.AppendLine("        result.textContent = `Request failed\\n${error}`;");
            html.AppendLine("      }");
            html.AppendLine("    }");
            html.AppendLine("  </script>");
            html.AppendLine("</body>");
            html.AppendLine("</html>");

            return html.ToString();
        }

        private void CollectRouteDescriptions(ApiRouteNode node, string currentPath, ICollection<ApiRouteDescription> routes)
        {
            var normalizedPath = string.IsNullOrEmpty(currentPath) ? "/" : currentPath;
            foreach (var methodPair in node.Methods)
            {
                routes.Add(CreateRouteDescription(normalizedPath, methodPair.Key, methodPair.Value));
            }

            foreach (var child in node.Nodes)
            {
                var segmentName = child.IsDynamic ? $"{{{child.DynamicName}}}" : child.Name;
                var childPath = normalizedPath == "/"
                    ? $"/{segmentName}"
                    : $"{normalizedPath}/{segmentName}";

                CollectRouteDescriptions(child, childPath, routes);
            }
        }

        private string GetIntrospectionBasePath()
        {
            var staticRootSegments = _root.Nodes
                .Where(node => !node.IsDynamic && !string.IsNullOrWhiteSpace(node.Name))
                .Select(node => node.Name.Trim('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (staticRootSegments.Count == 1)
                return $"/{staticRootSegments[0]}";

            return string.Empty;
        }

        private ApiRouteDescription CreateRouteDescription(string path, HttpMethod method, ApiRouteTarget target)
        {
            var routeParameterNames = GetRouteParameterNames(path);
            var parameters = target.ParameterInfos
                .Select(parameterInfo => CreateParameterDescription(parameterInfo, routeParameterNames))
                .ToList();
            var summaryAttribute = target.MethodInfo.GetCustomAttribute<ApiSummaryAttribute>(true);
            var consoleAttribute = target.MethodInfo.GetCustomAttribute<ApiConsoleAttribute>(true);

            return new ApiRouteDescription
            {
                Path = path,
                HttpMethod = method.Method,
                ControllerType = target.Controller.GetType().FullName,
                Action = target.MethodInfo.Name,
                Summary = summaryAttribute?.Text,
                ConsoleAlias = consoleAttribute?.Alias,
                ConsoleEnabled = consoleAttribute?.Enabled ?? true,
                Parameters = parameters,
                Responses = CreateResponseDescriptions(target.MethodInfo),
                ReturnType = target.MethodInfo.ReturnType
            };
        }

        private static List<ApiResponseDescription> CreateResponseDescriptions(MethodInfo methodInfo)
        {
            return methodInfo
                .GetCustomAttributes<ApiResponseAttribute>(true)
                .Select(attribute => new ApiResponseDescription
                {
                    StatusCode = attribute.StatusCode,
                    Description = attribute.Description,
                    ClrType = attribute.ResponseType
                })
                .OrderBy(response => response.StatusCode)
                .ToList();
        }

        private List<ApiHtmlResponseDescription> GetHtmlResponses(ApiRouteDescription route)
        {
            if (route.Responses != null && route.Responses.Count > 0)
            {
                return route.Responses
                    .Select(response => new ApiHtmlResponseDescription
                    {
                        StatusCode = response.StatusCode,
                        Description = string.IsNullOrEmpty(response.Description)
                            ? GetDefaultResponseDescription(response.StatusCode)
                            : response.Description,
                        Body = response.ClrType == null ? null : DescribeObject(response.ClrType, new HashSet<Type>())
                    })
                    .OrderBy(response => response.StatusCode)
                    .ToList();
            }

            if (route.ReturnType == null || route.ReturnType == typeof(void) || route.ReturnType == typeof(ApiResult))
                return new List<ApiHtmlResponseDescription>();

            return new List<ApiHtmlResponseDescription>
            {
                new()
                {
                    StatusCode = 200,
                    Description = GetDefaultResponseDescription(200),
                    Body = DescribeObject(route.ReturnType, new HashSet<Type>())
                }
            };
        }

        private ApiParameterDescription CreateParameterDescription(ParameterInfo parameterInfo, ISet<string> routeParameterNames)
        {
            var bodyAttribute = parameterInfo.GetCustomAttribute<ApiBodyAttribute>();
            var routeAttribute = parameterInfo.GetCustomAttribute<ApiRouteParamAttribute>();
            var queryAttribute = parameterInfo.GetCustomAttribute<ApiQueryAttribute>();
            var docAttribute = parameterInfo.GetCustomAttribute<ApiDocAttribute>();

            var isBody = bodyAttribute != null;
            var usesLegacyBinding = false;
            List<string> bindingSources;
            bool required;
            string parameterName;

            if (bodyAttribute != null)
            {
                bindingSources = new List<string> { "body" };
                required = bodyAttribute.Required;
                parameterName = parameterInfo.Name;
            }
            else if (routeAttribute != null)
            {
                bindingSources = new List<string> { "route" };
                required = routeAttribute.Required;
                parameterName = string.IsNullOrEmpty(routeAttribute.Name) ? parameterInfo.Name : routeAttribute.Name;
            }
            else if (queryAttribute != null)
            {
                bindingSources = new List<string> { "query" };
                required = queryAttribute.Required;
                parameterName = string.IsNullOrEmpty(queryAttribute.Name) ? parameterInfo.Name : queryAttribute.Name;
            }
            else
            {
                usesLegacyBinding = true;
                bindingSources = routeParameterNames.Contains(parameterInfo.Name)
                    ? new List<string> { "query", "route" }
                    : new List<string> { "query" };
                required = routeParameterNames.Contains(parameterInfo.Name);
                parameterName = parameterInfo.Name;
            }

            return new ApiParameterDescription
            {
                Name = parameterName,
                Type = ApiUtils.GetFriendlyTypeName(parameterInfo.ParameterType),
                DisplayType = string.IsNullOrWhiteSpace(docAttribute?.DisplayType) ? null : docAttribute.DisplayType,
                Description = docAttribute?.Description,
                Format = docAttribute?.Format,
                Example = docAttribute?.Example,
                AllowedValues = docAttribute?.AllowedValues?.ToList(),
                BindingSources = bindingSources,
                Required = required,
                Body = isBody ? DescribeObject(parameterInfo.ParameterType, new HashSet<Type>()) : null,
                UsesLegacyBinding = usesLegacyBinding,
                ClrType = parameterInfo.ParameterType
            };
        }

        private static HashSet<string> GetRouteParameterNames(string path)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in path.Split('/'))
            {
                if (segment.StartsWith("{") && segment.EndsWith("}") && segment.Length > 2)
                    names.Add(segment.Substring(1, segment.Length - 2));
            }

            return names;
        }

        private static ApiObjectDescription DescribeObject(Type type, ISet<Type> visitedTypes)
        {
            var description = new ApiObjectDescription
            {
                Type = ApiUtils.GetFriendlyTypeName(type)
            };

            if (ApiUtils.IsSimpleType(type))
                return description;

            if (ApiUtils.IsCollectionType(type))
            {
                var itemType = ApiUtils.GetCollectionElementType(type);
                description.ItemType = ApiUtils.GetFriendlyTypeName(itemType);
                description.Item = ApiUtils.IsSimpleType(itemType)
                    ? null
                    : DescribeObject(itemType, new HashSet<Type>(visitedTypes));
                return description;
            }

            if (!visitedTypes.Add(type))
            {
                description.IsRecursive = true;
                return description;
            }

            description.Members = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => new ApiObjectMemberDescription
                {
                    Name = field.Name,
                    Type = ApiUtils.GetFriendlyTypeName(field.FieldType),
                    Kind = "field",
                    Object = ApiUtils.IsSimpleType(field.FieldType)
                        ? null
                        : DescribeObject(field.FieldType, new HashSet<Type>(visitedTypes))
                })
                .Concat(type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0)
                    .Select(property => new ApiObjectMemberDescription
                    {
                        Name = property.Name,
                        Type = ApiUtils.GetFriendlyTypeName(property.PropertyType),
                        Kind = "property",
                        Object = ApiUtils.IsSimpleType(property.PropertyType)
                            ? null
                            : DescribeObject(property.PropertyType, new HashSet<Type>(visitedTypes))
                    }))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ToList();

            return description;
        }

        private static string RenderBindingSources(IReadOnlyCollection<string> bindingSources)
        {
            if (bindingSources == null || bindingSources.Count == 0)
                return "<span class=\"empty\">None</span>";

            return string.Join(string.Empty, bindingSources.Select(source => $"<span class=\"chip\">{HtmlEncode(source)}</span>"));
        }

        private static string RenderParameterType(ApiParameterDescription parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter.DisplayType) ||
                string.Equals(parameter.DisplayType, parameter.Type, StringComparison.OrdinalIgnoreCase))
            {
                return $"<code>{HtmlEncode(parameter.Type)}</code>";
            }

            return $"<div><code>{HtmlEncode(parameter.DisplayType)}</code></div><div class=\"empty\">encoded as <code>{HtmlEncode(parameter.Type)}</code></div>";
        }

        private static string RenderParameterDetails(ApiParameterDescription parameter)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(parameter.Description))
                parts.Add($"<div>{HtmlEncode(parameter.Description)}</div>");

            if (parameter.AllowedValues != null && parameter.AllowedValues.Count > 0)
            {
                parts.Add("<div>Allowed values:</div>");
                parts.Add($"<div>{string.Join(string.Empty, parameter.AllowedValues.Select(value => $"<span class=\"chip\">{HtmlEncode(value)}</span>"))}</div>");
            }

            if (!string.IsNullOrWhiteSpace(parameter.Format))
                parts.Add($"<div>Format: <code>{HtmlEncode(parameter.Format)}</code></div>");

            if (!string.IsNullOrWhiteSpace(parameter.Example))
                parts.Add($"<div>Example: <code>{HtmlEncode(parameter.Example)}</code></div>");

            if (parts.Count == 0)
                return "<span class=\"empty\">None</span>";

            return $"<details class=\"disclosure\"><summary>View details</summary><div class=\"content\">{string.Join(string.Empty, parts)}</div></details>";
        }

        private static string BuildParameterDescription(ApiParameterDescription parameter, string extraDescription)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(parameter.Description))
                parts.Add(parameter.Description.Trim());

            if (!string.IsNullOrWhiteSpace(parameter.DisplayType) &&
                !string.Equals(parameter.DisplayType, parameter.Type, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"Semantic type: {parameter.DisplayType}.");
            }

            if (!string.IsNullOrWhiteSpace(parameter.Format))
                parts.Add($"Format: {parameter.Format}.");

            if (!string.IsNullOrWhiteSpace(parameter.Example))
                parts.Add($"Example: {parameter.Example}.");

            if (!string.IsNullOrWhiteSpace(extraDescription))
                parts.Add(extraDescription.Trim());

            return string.Join(" ", parts);
        }

        private string RenderTryPanel(ApiRouteDescription route)
        {
            var nonBodyParameters = route.Parameters
                .Where(parameter => !parameter.BindingSources.Contains("body"))
                .ToList();
            var bodyParameter = route.Parameters.FirstOrDefault(parameter => parameter.BindingSources.Contains("body"));
            var requestPreview = BuildTryRequestPreview(route, nonBodyParameters);
            var routeKey = $"{route.HttpMethod}-{route.Path}".Replace("/", "-").Replace("{", string.Empty).Replace("}", string.Empty).Replace("?", string.Empty).Replace("&", string.Empty);

            var html = new StringBuilder();
            html.AppendLine("      <details class=\"disclosure try-panel\">");
            html.AppendLine("        <summary>Try it</summary>");
            html.AppendLine("        <div class=\"content\">");
            html.AppendLine($"          <form class=\"try-form\" onsubmit=\"event.preventDefault(); extApiTryRoute(this.querySelector('.try-button'));\" data-method=\"{HtmlEncode(route.HttpMethod)}\" data-path=\"{HtmlEncode(route.Path)}\">");
            html.AppendLine($"            <div class=\"try-request\">Request: <code>{HtmlEncode(requestPreview)}</code></div>");

            if (nonBodyParameters.Count > 0 || bodyParameter != null)
            {
                html.AppendLine("            <div class=\"try-grid\">");
                foreach (var parameter in nonBodyParameters)
                {
                    html.AppendLine(RenderTryField(routeKey, parameter, parameter.BindingSources.Contains("route") ? "route" : "query"));
                }

                if (bodyParameter != null)
                {
                    html.AppendLine(RenderTryBodyField(routeKey, bodyParameter));
                }

                html.AppendLine("            </div>");
            }

            html.AppendLine("            <div class=\"try-actions\">");
            html.AppendLine("              <button type=\"submit\" class=\"try-button\">Try it</button>");
            html.AppendLine("            </div>");
            html.AppendLine("            <pre class=\"try-result empty\">No response yet.</pre>");
            html.AppendLine("          </form>");
            html.AppendLine("        </div>");
            html.AppendLine("      </details>");
            return html.ToString();
        }

        private string RenderTryField(string routeKey, ApiParameterDescription parameter, string binding)
        {
            var sampleValue = GetSampleParameterValue(parameter);
            var details = BuildParameterDescription(parameter, null);
            var fieldId = $"try-{routeKey}-{parameter.Name}";
            var html = new StringBuilder();
            html.AppendLine("              <div class=\"try-field\">");
            html.AppendLine($"                <label for=\"{HtmlEncode(fieldId)}\">{HtmlEncode(parameter.Name)}</label>");

            if (parameter.AllowedValues != null && parameter.AllowedValues.Count > 0)
            {
                html.AppendLine($"                <select id=\"{HtmlEncode(fieldId)}\" data-try-binding=\"{HtmlEncode(binding)}\" data-try-name=\"{HtmlEncode(parameter.Name)}\">");
                foreach (var allowedValue in parameter.AllowedValues)
                {
                    var selectedAttribute = string.Equals(allowedValue, sampleValue, StringComparison.Ordinal) ? " selected" : string.Empty;
                    html.AppendLine($"                  <option value=\"{HtmlEncode(allowedValue)}\"{selectedAttribute}>{HtmlEncode(allowedValue)}</option>");
                }

                html.AppendLine("                </select>");
            }
            else
            {
                var inputType = GetTryInputType(parameter);
                var stepAttribute = string.Equals(inputType, "number", StringComparison.Ordinal) ? " step=\"any\"" : string.Empty;
                html.AppendLine($"                <input id=\"{HtmlEncode(fieldId)}\" type=\"{HtmlEncode(inputType)}\" value=\"{HtmlEncode(sampleValue)}\" data-try-binding=\"{HtmlEncode(binding)}\" data-try-name=\"{HtmlEncode(parameter.Name)}\"{stepAttribute}>");
            }

            if (!string.IsNullOrWhiteSpace(details))
                html.AppendLine($"                <div class=\"empty\">{HtmlEncode(details)}</div>");

            html.AppendLine("              </div>");
            return html.ToString();
        }

        private string RenderTryBodyField(string routeKey, ApiParameterDescription parameter)
        {
            var sampleValue = GetSampleBodyValue(parameter);
            var fieldId = $"try-body-{routeKey}-{parameter.Name}";
            var html = new StringBuilder();
            html.AppendLine("              <div class=\"try-field\" style=\"grid-column: 1 / -1;\">");
            html.AppendLine($"                <label for=\"{HtmlEncode(fieldId)}\">{HtmlEncode(parameter.Name)}</label>");
            html.AppendLine($"                <textarea id=\"{HtmlEncode(fieldId)}\" data-try-binding=\"body\" data-try-name=\"{HtmlEncode(parameter.Name)}\">{HtmlEncode(sampleValue)}</textarea>");
            html.AppendLine("              </div>");
            return html.ToString();
        }

        private string BuildTryRequestPreview(ApiRouteDescription route, IEnumerable<ApiParameterDescription> nonBodyParameters)
        {
            var path = route.Path;
            var queryParts = new List<string>();

            foreach (var parameter in nonBodyParameters)
            {
                var sampleValue = GetSampleParameterValue(parameter);
                if (parameter.BindingSources.Contains("route"))
                {
                    path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(sampleValue));
                }
                else
                {
                    queryParts.Add($"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(sampleValue)}");
                }
            }

            if (queryParts.Count > 0)
                path = $"{path}?{string.Join("&", queryParts)}";

            return $"{route.HttpMethod} {path}";
        }

        private static string GetSampleParameterValue(ApiParameterDescription parameter)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Example))
                return parameter.Example;

            if (parameter.AllowedValues != null && parameter.AllowedValues.Count > 0)
                return parameter.AllowedValues[0];

            if (string.Equals(parameter.DisplayType, "Vector3", StringComparison.OrdinalIgnoreCase))
                return "1,1,1";

            if (string.Equals(parameter.DisplayType, "int[]", StringComparison.OrdinalIgnoreCase))
                return "1,2,3";

            var type = Nullable.GetUnderlyingType(parameter.ClrType) ?? parameter.ClrType;
            if (type.IsEnum)
                return Enum.GetNames(type).FirstOrDefault() ?? "example";

            if (type == typeof(bool))
                return "true";

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "1.0";

            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            {
                return "1";
            }

            return "example";
        }

        private string GetSampleBodyValue(ApiParameterDescription parameter)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Example))
                return parameter.Example;

            if (parameter.Body == null)
                return "{}";

            var sampleObject = BuildSampleObject(parameter.Body);
            return JsonConvert.SerializeObject(sampleObject, Formatting.Indented);
        }

        private object BuildSampleObject(ApiObjectDescription description)
        {
            if (description == null)
                return null;

            if (description.IsRecursive)
                return null;

            if (!string.IsNullOrWhiteSpace(description.ItemType))
            {
                return new[] { BuildSampleObject(description.Item ?? new ApiObjectDescription { Type = description.ItemType }) };
            }

            if (description.Members == null || description.Members.Count == 0)
                return BuildSampleScalarValue(description.Type);

            var sample = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var member in description.Members)
            {
                sample[member.Name] = member.Object == null
                    ? BuildSampleScalarValue(member.Type)
                    : BuildSampleObject(member.Object);
            }

            return sample;
        }

        private static object BuildSampleScalarValue(string typeName)
        {
            return typeName switch
            {
                "Boolean" => true,
                "Byte" => 1,
                "SByte" => 1,
                "Int16" => 1,
                "UInt16" => 1,
                "Int32" => 1,
                "UInt32" => 1,
                "Int64" => 1,
                "UInt64" => 1,
                "Single" => 1.0f,
                "Double" => 1.0d,
                "Decimal" => 1.0m,
                "String" => "example",
                _ => "example"
            };
        }

        private static string GetTryInputType(ApiParameterDescription parameter)
        {
            var type = Nullable.GetUnderlyingType(parameter.ClrType) ?? parameter.ClrType;
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            {
                return "number";
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";

            return "text";
        }

        private static string RenderBodySchema(ApiObjectDescription body)
        {
            if (body == null)
                return "<span class=\"empty\">None</span>";

            return $"<details class=\"disclosure\"><summary>View schema</summary><div class=\"content schema\">{RenderObjectDescription(body)}</div></details>";
        }

        private static string RenderObjectDescription(ApiObjectDescription description)
        {
            var html = new StringBuilder();
            html.Append($"<div><code>{HtmlEncode(description.Type)}</code>");

            if (!string.IsNullOrEmpty(description.ItemType))
                html.Append($" of <code>{HtmlEncode(description.ItemType)}</code>");

            if (description.IsRecursive)
                html.Append(" <span class=\"chip\">recursive</span>");

            html.Append("</div>");

            if (description.Item != null)
            {
                html.Append("<ul><li>");
                html.Append(RenderObjectDescription(description.Item));
                html.Append("</li></ul>");
            }

            if (description.Members != null && description.Members.Count > 0)
            {
                html.Append("<ul>");
                foreach (var member in description.Members)
                {
                    html.Append("<li>");
                    html.Append($"<code>{HtmlEncode(member.Name)}</code>: <code>{HtmlEncode(member.Type)}</code> <span class=\"chip\">{HtmlEncode(member.Kind)}</span>");
                    if (member.Object != null)
                        html.Append(RenderObjectDescription(member.Object));

                    html.Append("</li>");
                }

                html.Append("</ul>");
            }

            return html.ToString();
        }

        private static string HtmlEncode(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private ApiRouteNode CreateRouteNode(string route)
        {
            return string.IsNullOrEmpty(route) ? _root : GetRouteNode(route.Split('/'), true, null);
        }

        private ApiRouteTarget GetRouteTarget(HttpMethod method, IReadOnlyList<string> segments, IDictionary<string, string> parameters)
        {
            var node = GetRouteNode(segments, false, parameters);
            return node?.Methods.GetValueOrDefault(method);
        }

        private ApiResult InvokeLocal(HttpMethod method, string path, IReadOnlyDictionary<string, string> queryParameters)
        {
            var routeParameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var target = GetRouteTarget(method, SplitRoutePath(path), routeParameters);
            if (target == null)
                return ApiResult.NotFound();

            var session = target.GetSession(
                queryParameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                routeParameters);

            return ExecuteSession(session);
        }

        private static IReadOnlyList<string> SplitRoutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new[] { "/" };

            var normalizedPath = path.StartsWith("/") ? path : $"/{path}";
            return new[] { "/" }
                .Concat(normalizedPath
                    .Trim('/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
        }

        private ApiRouteNode GetRouteNode(IReadOnlyList<string> segments, bool create, IDictionary<string, string> parameters)
        {
            if (segments[0] == "/" && segments.Count == 1)
                return _root;

            var startIndex = segments[0] == "/" ? 1 : 0;
            var currentNode = _root;

            for (var i = startIndex; i < segments.Count; i++)
            {
                var name = segments[i].TrimEnd('/');
                ApiRouteNode node = null;

                if (currentNode == null || currentNode.Nodes == null) break;

                node = currentNode.Nodes.Find(n => n.Name == name);
                if (node == null)
                {
                    if (create)
                    {
                        node = new ApiRouteNode(name);
                        node.IsDynamic = name.StartsWith('{') && name.EndsWith('}');
                        node.DynamicName = name.Trim('{', '}');

                        currentNode.Nodes.Add(node);
                    }
                    else
                    {
                        if (parameters != null)
                        {
                            node = currentNode.Nodes.Find(n => n.IsDynamic);
                            if (node != null)
                            {
                                parameters.Add(node.DynamicName, name);
                            }
                        }
                        else
                        {
                            return null;
                        }
                    }
                }

                currentNode = node;
            }

            return currentNode;
        }

        private sealed class ApiHtmlResponseDescription
        {
            public int StatusCode { get; set; }
            public string Description { get; set; }
            public ApiObjectDescription Body { get; set; }
        }

        public void Dispose() => Close();
    }
}
