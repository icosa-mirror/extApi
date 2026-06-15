# extApi - Simple Http Server for Unity

Created by [iam1337](https://github.com/iam1337)

![](https://img.shields.io/badge/unity-2022.1%20or%20later-green.svg)
[![⚙ Build and Release](https://github.com/Iam1337/extApi/actions/workflows/ci.yml/badge.svg)](https://github.com/Iam1337/extApi/actions/workflows/ci.yml)
[![openupm](https://img.shields.io/npm/v/com.iam1337.extapi?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.iam1337.extapi/)
[![](https://img.shields.io/github/license/iam1337/extApi.svg)](https://github.com/Iam1337/extApi/blob/master/LICENSE)
[![semantic-release: angular](https://img.shields.io/badge/semantic--release-angular-e10079?logo=semantic-release)](https://github.com/semantic-release/semantic-release)

### Table of Contents
- [Introduction](#introduction)
- [Installation](#installation)
- [Route Discovery](#route-discovery)
- [Examples](#examples)
- [Author Contacts](#author-contacts)

## Introduction
extApi - It is a simple Http Server, which requires a minimum of specific code to work with. 

### Features:

## Installation
**Old school**

Just copy the [Assets/extApi](Assets/extApi) folder into your Assets directory within your Unity project.

**OpenUPM**

Via [openupm-cli](https://github.com/openupm/openupm-cli):<br>
```
openupm add com.iam1337.extapi
```

Or if you don't have it, add the scoped registry to manifest.json with the desired dependency semantic version:
```
"scopedRegistries": [
	{
		"name": "package.openupm.com",
		"url": "https://package.openupm.com",
		"scopes": [
			"com.iam1337.extapi",
		]
	}
],
"dependencies": {
	"com.iam1337.extapi": "1.0.0"
}
```

## Route Discovery

extApi exposes:
- `GET /<api-root>/openapi.json` for OpenAPI 3.0.3 JSON
- `GET /<api-root>/docs.html` for a human-readable HTML view

If all registered routes share the same top-level static segment, extApi mounts the
introspection endpoints under that same root. For example, routes under `/api/...`
will expose:
- `GET /api/openapi.json`
- `GET /api/docs.html`

If there is no single shared top-level root, extApi falls back to:
- `GET /openapi.json`
- `GET /docs.html`

For accurate binding metadata, prefer the explicit parameter attributes:
- `[ApiRouteParam]` for route segments
- `[ApiQuery]` for query-string parameters
- `[ApiBody]` for request bodies

For accurate response schemas in the OpenAPI output, annotate methods with:
- `[ApiResponse(statusCode, typeof(ResponseType))]`

For better generated help and console integration, you can also annotate methods with:
- `[ApiSummary("Short human-readable description")]`
- `[ApiConsole("custom alias")]`
- `[ApiConsole(enabled: false)]` to keep a route out of the auto-generated console surface

For parameter metadata that reflection cannot infer on its own, annotate parameters with:
- `[ApiDoc("Human-readable description")]`
- `DisplayType = "Vector3"` for semantic types that are transported as strings
- `Format = "x,y,z"` for serialized query/path formats
- `Example = "1,2,3"` for example values
- `AllowedValues = new string[] { "A", "B" }` for constrained string parameters

If a parameter has no explicit binding attribute, extApi uses the legacy binder:
- query string is checked first
- route parameters are checked second
- missing values fall back to the type default

The HTML view also shows body schemas and whether a parameter is using legacy binding.

Example response:

```json
{
  "openapi": "3.0.3",
  "info": {
    "title": "extApi",
    "version": "1.0.0"
  },
  "paths": {
    "/api/vector/{x}/{y}/{z}": {
      "get": {
        "operationId": "ExampleController.GetVector",
        "parameters": [
          {
            "name": "x",
            "in": "path",
            "required": true,
            "schema": {
              "type": "number",
              "format": "float"
            }
          }
        ]
      }
    }
  }
}
```

## Examples

To make a simple Web Api, the following lines are enough:
```csharp
// Create Api Server
_api = new Api();
_api.AddController(new ApiController()); // <--- Add controller
_api.Listen(8080, IPAddress.Any, IPAddress.Loopback);

// Simple controller example
[ApiRoute("api")]
public class ApiController
{
        [ApiGet("vector/{x}/{y}/{z}")] // GET /api/vector/1/2.5/5
        public ApiResult GetVector(float x, float y, float z)
        {
		return ApiResult.Ok(new Vector3(x, y, z));
        }


        [ApiPost("vector")] // POST /api/vector { "x": 1.0, "y": 2.5, "z": 5.0 }
        public ApiResult GetVector([ApiBody] Vector vector)
        {
		// TODO: ...
        }
}
```

## Author Contacts
\> [telegram.me/iam1337](http://telegram.me/iam1337) <br>
\> [ext@iron-wall.org](mailto:ext@iron-wall.org)

## License
This project is under the MIT License.

