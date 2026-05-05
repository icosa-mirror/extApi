using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using UnityEngine;

namespace extApi
{
    internal class ApiRouteTarget
    {
        public object Controller;
        public MethodInfo MethodInfo;
        public ParameterInfo[] ParameterInfos;

        public ApiSession GetSession(HttpListenerContext context, Dictionary<string, string> routeParameters)
        {
            var args = new List<object>();

            if (Controller is IApiController apiController)
            {
                apiController.Context = context;
                apiController.Request = context.Request;
                apiController.Response = context.Response;
            }

            foreach (var parameterInfo in ParameterInfos)
            {
                if (!TryBindParameter(context, routeParameters, parameterInfo, out var value, out var errorResult))
                {
                    return new ApiSession
                    {
                        Controller = Controller,
                        MethodInfo = MethodInfo,
                        Context = context,
                        Arguments = Array.Empty<object>(),
                        Result = errorResult
                    };
                }

                args.Add(value);
            }

            return new ApiSession
            {
                Controller = Controller,
                MethodInfo = MethodInfo,
                Context = context,
                Arguments = args.ToArray()
            };
        }

        public ApiSession GetSession(
            IReadOnlyDictionary<string, string> queryParameters,
            Dictionary<string, string> routeParameters)
        {
            var args = new List<object>();

            foreach (var parameterInfo in ParameterInfos)
            {
                if (!TryBindParameter(null, queryParameters, routeParameters, parameterInfo, out var value, out var errorResult))
                {
                    return new ApiSession
                    {
                        Controller = Controller,
                        MethodInfo = MethodInfo,
                        Arguments = Array.Empty<object>(),
                        Result = errorResult
                    };
                }

                args.Add(value);
            }

            return new ApiSession
            {
                Controller = Controller,
                MethodInfo = MethodInfo,
                Arguments = args.ToArray()
            };
        }

        private bool TryBindParameter(
            HttpListenerContext context,
            IReadOnlyDictionary<string, string> routeParameters,
            ParameterInfo parameterInfo,
            out object value,
            out ApiResult errorResult)
        {
            return TryBindParameter(context, GetQueryParameters(context), routeParameters, parameterInfo, out value, out errorResult);
        }

        private bool TryBindParameter(
            HttpListenerContext context,
            IReadOnlyDictionary<string, string> queryParameters,
            IReadOnlyDictionary<string, string> routeParameters,
            ParameterInfo parameterInfo,
            out object value,
            out ApiResult errorResult)
        {
            var bodyAttribute = parameterInfo.GetCustomAttribute<ApiBodyAttribute>();
            if (bodyAttribute != null)
                return TryBindBody(context, parameterInfo, bodyAttribute, out value, out errorResult);

            var routeAttribute = parameterInfo.GetCustomAttribute<ApiRouteParamAttribute>();
            if (routeAttribute != null)
                return TryBindRoute(parameterInfo, routeParameters, routeAttribute, out value, out errorResult);

            var queryAttribute = parameterInfo.GetCustomAttribute<ApiQueryAttribute>();
            if (queryAttribute != null)
                return TryBindQuery(queryParameters, parameterInfo, queryAttribute, out value, out errorResult);

            return TryBindLegacy(queryParameters, routeParameters, parameterInfo, out value, out errorResult);
        }

        private bool TryBindBody(HttpListenerContext context, ParameterInfo parameterInfo, ApiBodyAttribute attribute, out object value, out ApiResult errorResult)
        {
            value = ApiUtils.CreateDefault(parameterInfo.ParameterType);
            errorResult = null;

            if (context == null)
            {
                if (attribute.Required)
                {
                    errorResult = CreateBadRequest($"Body parameter \"{parameterInfo.Name}\" is not supported for local invocation.");
                    return false;
                }

                return true;
            }

            if (!context.Request.HasEntityBody)
            {
                if (attribute.Required)
                {
                    errorResult = CreateBadRequest($"Missing request body for parameter \"{parameterInfo.Name}\".");
                    return false;
                }

                return true;
            }

            using var stream = context.Request.InputStream;
            using var reader = new StreamReader(stream, context.Request.ContentEncoding);
            var bodyText = reader.ReadToEnd();
            var contentType = context.Request.ContentType ?? string.Empty;

            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    value = JsonUtility.FromJson(bodyText, parameterInfo.ParameterType);
                    if (value == null && attribute.Required)
                    {
                        errorResult = CreateBadRequest($"Unable to parse JSON body for parameter \"{parameterInfo.Name}\".");
                        return false;
                    }

                    return true;
                }
                catch (Exception)
                {
                    Debug.LogWarning("Unable to parse request body.");
                    errorResult = CreateBadRequest($"Unable to parse JSON body for parameter \"{parameterInfo.Name}\".");
                    return false;
                }
            }

            if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    value = BindFormBody(parameterInfo.ParameterType, bodyText);
                    return true;
                }
                catch (Exception)
                {
                    errorResult = CreateBadRequest($"Unable to parse form body for parameter \"{parameterInfo.Name}\".");
                    return false;
                }
            }

            errorResult = CreateBadRequest($"Unsupported content type \"{contentType}\" for parameter \"{parameterInfo.Name}\".");
            return false;
        }

        private bool TryBindRoute(
            ParameterInfo parameterInfo,
            IReadOnlyDictionary<string, string> routeParameters,
            ApiRouteParamAttribute attribute,
            out object value,
            out ApiResult errorResult)
        {
            errorResult = null;
            var routeName = string.IsNullOrEmpty(attribute.Name) ? parameterInfo.Name : attribute.Name;
            if (routeParameters.TryGetValue(routeName, out var rawValue))
            {
                return TryConvertValue(parameterInfo.ParameterType, rawValue, parameterInfo.Name, "route", out value, out errorResult);
            }

            if (attribute.Required)
            {
                value = null;
                errorResult = CreateBadRequest($"Missing route parameter \"{routeName}\" for \"{parameterInfo.Name}\".");
                return false;
            }

            value = ApiUtils.CreateDefault(parameterInfo.ParameterType);
            return true;
        }

        private bool TryBindQuery(
            IReadOnlyDictionary<string, string> queryParameters,
            ParameterInfo parameterInfo,
            ApiQueryAttribute attribute,
            out object value,
            out ApiResult errorResult)
        {
            errorResult = null;
            var queryName = string.IsNullOrEmpty(attribute.Name) ? parameterInfo.Name : attribute.Name;
            if (queryParameters != null && queryParameters.TryGetValue(queryName, out var rawValue) && !string.IsNullOrEmpty(rawValue))
            {
                return TryConvertValue(parameterInfo.ParameterType, rawValue, parameterInfo.Name, "query", out value, out errorResult);
            }

            if (attribute.Required)
            {
                value = null;
                errorResult = CreateBadRequest($"Missing query parameter \"{queryName}\" for \"{parameterInfo.Name}\".");
                return false;
            }

            value = ApiUtils.CreateDefault(parameterInfo.ParameterType);
            return true;
        }

        private bool TryBindLegacy(
            IReadOnlyDictionary<string, string> queryParameters,
            IReadOnlyDictionary<string, string> routeParameters,
            ParameterInfo parameterInfo,
            out object value,
            out ApiResult errorResult)
        {
            errorResult = null;
            if (queryParameters != null &&
                queryParameters.TryGetValue(parameterInfo.Name, out var rawQueryValue) &&
                !string.IsNullOrEmpty(rawQueryValue))
            {
                return TryConvertValue(parameterInfo.ParameterType, rawQueryValue, parameterInfo.Name, "query", out value, out errorResult);
            }

            if (routeParameters.TryGetValue(parameterInfo.Name, out var routeValue))
            {
                return TryConvertValue(parameterInfo.ParameterType, routeValue, parameterInfo.Name, "route", out value, out errorResult);
            }

            value = ApiUtils.CreateDefault(parameterInfo.ParameterType);
            return true;
        }

        private static IReadOnlyDictionary<string, string> GetQueryParameters(HttpListenerContext context)
        {
            var queryParameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (context?.Request?.QueryString == null)
                return queryParameters;

            foreach (var key in context.Request.QueryString.AllKeys)
            {
                if (!string.IsNullOrEmpty(key))
                    queryParameters[key] = context.Request.QueryString[key];
            }

            return queryParameters;
        }

        private static bool TryConvertValue(Type targetType, string rawValue, string parameterName, string source, out object value, out ApiResult errorResult)
        {
            errorResult = null;
            try
            {
                value = TypeDescriptor.GetConverter(targetType)
                    .ConvertFromString(null, CultureInfo.InvariantCulture, rawValue);
                return true;
            }
            catch (Exception)
            {
                value = null;
                errorResult = CreateBadRequest($"Invalid {source} value for parameter \"{parameterName}\": \"{rawValue}\".");
                return false;
            }
        }

        private static object BindFormBody(Type parameterType, string formContent)
        {
            var instance = Activator.CreateInstance(parameterType);
            var pairs = formContent.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var keyValue = pair.Split('=');
                if (keyValue.Length != 2)
                    continue;

                var key = WebUtility.UrlDecode(keyValue[0]);
                var value = WebUtility.UrlDecode(keyValue[1]);
                var property = parameterType.GetProperty(key);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(instance, TypeDescriptor.GetConverter(property.PropertyType)
                        .ConvertFromString(null, CultureInfo.InvariantCulture, value));
                    continue;
                }

                var field = parameterType.GetField(key);
                if (field != null)
                {
                    field.SetValue(instance, TypeDescriptor.GetConverter(field.FieldType)
                        .ConvertFromString(null, CultureInfo.InvariantCulture, value));
                }
            }

            return instance;
        }

        private static ApiResult CreateBadRequest(string message)
        {
            return ApiResult.BadRequest(new ApiErrorResponse
            {
                error = message
            });
        }

        [Serializable]
        private class ApiErrorResponse
        {
            public string error;
        }
    }
}
