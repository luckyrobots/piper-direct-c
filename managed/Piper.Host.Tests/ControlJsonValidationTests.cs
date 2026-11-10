using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;

internal static class ControlJsonValidationTests
{
    internal static async Task Run(HttpClient http, PiperControl.PiperControlClient grpc, string token)
    {
        string withinDepth = "0", beyondDepth = "0";
        for (int i = 0; i < 30; i++) withinDepth = "{\"a\":" + withinDepth + "}";
        for (int i = 0; i < 33; i++) beyondDepth = "{\"a\":" + beyondDepth + "}";
        const string objectError = "Expected one JSON object with unique keys";
        const string encodingError = "Unicode escapes and NUL are unsupported in control requests";
        // A null error means the body is valid. Both transports apply one
        // ControlJson.TryParse, so gRPC reports the HTTP message verbatim.
        var cases = new (string Name, string Json, HttpStatusCode HttpStatus, string? Error)[]
        {
            ("empty object", "{}", HttpStatusCode.OK, null),
            ("separate object scopes", """{"a":[{"b":1},{"b":2}]}""", HttpStatusCode.OK, null),
            ("ordinal distinct names", """{"a":1,"A":2,"é":3}""", HttpStatusCode.OK, null),
            ("scalar array members", """{"a":[true,null,1.5,"text",{},[]]}""", HttpStatusCode.OK, null),
            ("within depth", withinDepth, HttpStatusCode.OK, null),
            ("root duplicate", """{"a":1,"a":2}""", HttpStatusCode.BadRequest, objectError),
            ("nested duplicate", """{"a":{"b":1,"b":2}}""", HttpStatusCode.BadRequest, objectError),
            ("array object duplicate", """{"a":[{"b":1,"b":2}]}""", HttpStatusCode.BadRequest, objectError),
            ("array root", "[]", HttpStatusCode.BadRequest, objectError),
            ("scalar root", "null", HttpStatusCode.BadRequest, objectError),
            ("malformed nested tree", """{"a":[}""", HttpStatusCode.BadRequest, "Invalid JSON body"),
            ("beyond depth", beyondDepth, HttpStatusCode.BadRequest, "Invalid JSON body"),
            ("Unicode escape", """{"\u0061":1}""", HttpStatusCode.BadRequest, encodingError),
            ("raw NUL", "{\"a\":\"\0\"}", HttpStatusCode.BadRequest, encodingError),
            ("UTF-8 byte limit", "{\"a\":\"" + new string('é', 8200) + "\"}", HttpStatusCode.RequestEntityTooLarge, "Request too large")
        };
        var headers = new Metadata { { "x-piper-token", token } };
        foreach (var input in cases)
        {
            // The fixture is --simulate on ephemeral ports, so valid inventory is empty.
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/pika/devices")
            { Content = new StringContent(input.Json, Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Piper-Token", token);
            using var response = await http.SendAsync(message);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (response.StatusCode != input.HttpStatus || (input.Error is null
                    ? body?["devices"] is not JsonArray { Count: 0 }
                    : body?["error"]?.GetValue<string>() != input.Error))
                throw new Exception("HTTP JSON validation changed for " + input.Name + ": " + response.StatusCode);
            try
            {
                var reply = await grpc.ExecuteAsync(new ControlRequest { Route = "/api/pika/devices", Json = input.Json },
                    headers, deadline: DateTime.UtcNow.AddSeconds(3));
                if (input.Error is not null || reply.Status != 200 ||
                    JsonNode.Parse(reply.Json)?["devices"] is not JsonArray { Count: 0 })
                    throw new Exception("gRPC accepted an invalid JSON control tree or changed valid simulated inventory: " + input.Name);
            }
            catch (RpcException error) when (input.Error is not null && error.StatusCode == StatusCode.InvalidArgument)
            {
                if (error.Status.Detail != input.Error)
                    throw new Exception("gRPC validation message differs from HTTP for " + input.Name + ": " + error.Status.Detail);
            }
        }
        Console.WriteLine($"Host JSON validation passed: {cases.Length} tree, duplicate-key, depth, encoding and byte-limit cases with one message set over HTTP and gRPC; hardware disabled.");
    }
}
