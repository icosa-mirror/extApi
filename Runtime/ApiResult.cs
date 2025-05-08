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

        public HttpStatusCode StatusCode { get; }
        public string Json { get; }

        private ApiResult(HttpStatusCode statusCode)
        {
            StatusCode = statusCode;
        }

        private ApiResult(HttpStatusCode statusCode, object result) : this(statusCode)
        {
            Json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }
    }
}