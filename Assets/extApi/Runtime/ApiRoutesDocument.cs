using System.Collections.Generic;
using Newtonsoft.Json;

namespace extApi
{
    public class ApiRoutesDocument
    {
        public string Endpoint { get; set; }
        public List<ApiRouteDescription> Routes { get; set; }
    }

    public class ApiRouteDescription
    {
        public string Path { get; set; }
        public string HttpMethod { get; set; }
        public string ControllerType { get; set; }
        public string Action { get; set; }
        public string Summary { get; set; }
        public string ConsoleAlias { get; set; }
        public bool ConsoleEnabled { get; set; }
        public List<ApiParameterDescription> Parameters { get; set; }
        public List<ApiResponseDescription> Responses { get; set; }
        [JsonIgnore]
        public System.Type ReturnType { get; set; }
    }

    public class ApiParameterDescription
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string DisplayType { get; set; }
        public string Description { get; set; }
        public string Format { get; set; }
        public string Example { get; set; }
        public List<string> AllowedValues { get; set; }
        public List<string> BindingSources { get; set; }
        public bool Required { get; set; }
        public ApiObjectDescription Body { get; set; }
        public bool UsesLegacyBinding { get; set; }
        [JsonIgnore]
        public System.Type ClrType { get; set; }
    }

    public class ApiObjectDescription
    {
        public string Type { get; set; }
        public bool IsRecursive { get; set; }
        public string ItemType { get; set; }
        public ApiObjectDescription Item { get; set; }
        public List<ApiObjectMemberDescription> Members { get; set; }
    }

    public class ApiObjectMemberDescription
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Kind { get; set; }
        public ApiObjectDescription Object { get; set; }
    }

    public class ApiResponseDescription
    {
        public int StatusCode { get; set; }
        public string Description { get; set; }
        [JsonIgnore]
        public System.Type ClrType { get; set; }
    }
}
