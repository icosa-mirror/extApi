using System;
using System.Net.Http;

namespace extApi
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public abstract class ApiParameterAttribute : Attribute
    {
        protected ApiParameterAttribute(string name, bool required)
        {
            Name = name;
            Required = required;
        }

        public string Name { get; }
        public bool Required { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ApiBodyAttribute : ApiParameterAttribute
    {
        public ApiBodyAttribute(bool required = true) : base(null, required)
        { }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ApiQueryAttribute : ApiParameterAttribute
    {
        public ApiQueryAttribute(string name = null, bool required = false) : base(name, required)
        { }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ApiRouteParamAttribute : ApiParameterAttribute
    {
        public ApiRouteParamAttribute(string name = null, bool required = true) : base(name, required)
        { }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ApiDocAttribute : Attribute
    {
        public ApiDocAttribute(string description = null)
        {
            Description = description;
        }

        public string Description { get; }
        public string DisplayType { get; set; }
        public string Format { get; set; }
        public string Example { get; set; }
        public string[] AllowedValues { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ApiRouteAttribute : Attribute
    {
        public ApiRouteAttribute(string route) => Route = route;
        public readonly string Route;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class ApiMethodAttribute : Attribute
    {
        public readonly HttpMethod Method;
        public readonly string Template;

        protected ApiMethodAttribute(HttpMethod method, string template)
        {
            Method = method;
            Template = template;
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class ApiResponseAttribute : Attribute
    {
        public ApiResponseAttribute(int statusCode, Type responseType = null, string description = null)
        {
            StatusCode = statusCode;
            ResponseType = responseType;
            Description = description;
        }

        public int StatusCode { get; }
        public Type ResponseType { get; }
        public string Description { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ApiSummaryAttribute : Attribute
    {
        public ApiSummaryAttribute(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ApiConsoleAttribute : Attribute
    {
        public ApiConsoleAttribute(string alias = null, bool enabled = true)
        {
            Alias = alias;
            Enabled = enabled;
        }

        public string Alias { get; }
        public bool Enabled { get; }
    }

    public class ApiHeadAttribute : ApiMethodAttribute
    {
        public ApiHeadAttribute(string template) : base(HttpMethod.Head, template)
        { }
    }

    public class ApiGetAttribute : ApiMethodAttribute
    {
        public ApiGetAttribute(string template) : base(HttpMethod.Get, template)
        { }
    }

    public class ApiPostAttribute : ApiMethodAttribute
    {
        public ApiPostAttribute(string template) : base(HttpMethod.Post, template)
        { }
    }

    public class ApiPutAttribute : ApiMethodAttribute
    {
        public ApiPutAttribute(string template) : base(HttpMethod.Put, template)
        { }
    }

    public class ApiDeleteAttribute : ApiMethodAttribute
    {
        public ApiDeleteAttribute(string template) : base(HttpMethod.Delete, template)
        { }
    }
}
