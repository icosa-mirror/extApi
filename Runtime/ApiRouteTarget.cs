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
                        args.Add(TypeDescriptor.GetConverter(parameterType).ConvertFromString(null, CultureInfo.InvariantCulture, queryString));
                        continue;
                    }
                }
                if (parameterInfo.GetCustomAttribute<ApiBodyAttribute>() == null)
                {
                    args.Add(routeParameters.TryGetValue(parameterInfo.Name, out var value)
                        ? TypeDescriptor.GetConverter(parameterType).ConvertFromString(null, CultureInfo.InvariantCulture, value)
                        : ApiUtils.CreateDefault(parameterInfo.ParameterType));
                }
                else
                {
                    if (context.Request.HasEntityBody)
                    {
                        using var stream = context.Request.InputStream;
                        using var reader = new StreamReader(stream, context.Request.ContentEncoding);

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
                    else
                    {
                        args.Add(ApiUtils.CreateDefault(parameterType));
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