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
                var parameterType = parameterInfo.ParameterType;
                if (context.Request.QueryString.Count > 0)
                {
                    var queryString = context.Request.QueryString[parameterInfo.Name];
                    if (!string.IsNullOrEmpty(queryString))
                    {
                        args.Add(TypeDescriptor.GetConverter(parameterType)
                            .ConvertFromString(null, CultureInfo.InvariantCulture, queryString));
                        continue;
                    }
                }

                if (parameterInfo.GetCustomAttribute<ApiBodyAttribute>() == null)
                {
                    args.Add(routeParameters.TryGetValue(parameterInfo.Name, out var value)
                        ? TypeDescriptor.GetConverter(parameterType)
                            .ConvertFromString(null, CultureInfo.InvariantCulture, value)
                        : ApiUtils.CreateDefault(parameterInfo.ParameterType));
                }
                else
                {
                    if (context.Request.HasEntityBody)
                    {
                        using var stream = context.Request.InputStream;
                        using var reader = new StreamReader(stream, context.Request.ContentEncoding);

                        if (context.Request.ContentType.StartsWith("application/json",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                args.Add(JsonUtility.FromJson(reader.ReadToEnd(), parameterType));
                            }
                            catch (Exception)
                            {
                                Debug.LogWarning("Unable to parse"); // TODO: More info
                                args.Add(ApiUtils.CreateDefault(parameterType));
                            }
                        }
                        else if (context.Request.ContentType.StartsWith("application/x-www-form-urlencoded",
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            var instance = Activator.CreateInstance(parameterType);
                            var formContent = reader.ReadToEnd();
                            var pairs = formContent.Split('&');
                            foreach (var pair in pairs)
                            {
                                var keyValue = pair.Split('=');
                                if (keyValue.Length == 2)
                                {
                                    var key = WebUtility.UrlDecode(keyValue[0]);
                                    var value = WebUtility.UrlDecode(keyValue[1]);
                                    var prop = parameterType.GetProperty(key);
                                    if (prop != null)
                                    {
                                        if (prop != null && prop.CanWrite)
                                        {
                                            prop.SetValue(instance, TypeDescriptor.GetConverter(prop.PropertyType)
                                                .ConvertFromString(null, CultureInfo.InvariantCulture, value));
                                        }
                                    }
                                    else
                                    {
                                        var field = parameterType.GetField(key);
                                        if (field != null)
                                        {
                                            field.SetValue(instance, TypeDescriptor.GetConverter(field.FieldType)
                                                .ConvertFromString(null, CultureInfo.InvariantCulture, value));
                                        }
                                    }
                                }
                            }
                            args.Add(instance);
                        }
                    }
                }
            }

            return new ApiSession
            {
                Controller = Controller,
                MethodInfo = MethodInfo,
                Context = context,
                Arguments = args.ToArray()
            };
        }
    }
}