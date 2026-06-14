using System;
using System.Net;
using UnityEngine;

namespace extApi
{
    public class ApiResult
    {
        public static ApiResult Ok(object result = null) => new(HttpStatusCode.OK, result);
        public static ApiResult BadRequest(object result = null) => new(HttpStatusCode.BadRequest, result);
        public static ApiResult NotFound(object result = null) => new(HttpStatusCode.NotFound, result);
        public static ApiResult InternalServerError(object result = null) => new(HttpStatusCode.InternalServerError, result);
        public static ApiResult Redirect(Uri url = null) => new(HttpStatusCode.Found, url);
        public static ApiResult Status(HttpStatusCode code, object result = null) => new(code, result);
        public static ApiResult Html(string html, HttpStatusCode code = HttpStatusCode.OK) => new(code, html, "text/html; charset=utf-8");

        public HttpStatusCode StatusCode { get; }
        public string Json { get; }
        public string RawBody { get; }
        public string Location { get; }
        public string ContentType { get; }

        private ApiResult(HttpStatusCode statusCode)
        {
            StatusCode = statusCode;
        }

        private ApiResult(HttpStatusCode statusCode, object result) : this(statusCode)
        {
            Json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }

        private ApiResult(HttpStatusCode statusCode, Uri url) : this(statusCode)
        {
            Location = url?.ToString();
        }

        public ApiResult(HttpStatusCode statusCode, string result) : this(statusCode)
            => RawBody = result?.ToString();

        private ApiResult(HttpStatusCode statusCode, string result, string contentType) : this(statusCode)
        {
            RawBody = result?.ToString();
            ContentType = contentType;
        }
    }
}
