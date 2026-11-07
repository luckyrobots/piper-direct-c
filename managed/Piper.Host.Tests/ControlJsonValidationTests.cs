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
        var cases = new (string Name, string Json, HttpStatusCode HttpStatus, string HttpError)[]
        {
            ("empty object", "{}", HttpStatusCode.Conflict, "Pika hardware is disabled in simulation"),
            ("separate object scopes", """{"a":[{"b":1},{"b":2}]}""", HttpStatusCode.Conflict, "Pika hardware is disabled in simulation"),
            ("ordinal distinct names", """{"a":1,"A":2,"é":3}""", HttpStatusCode.Conflict, "Pika hardware is disabled in simulation"),
            ("scalar array members", """{"a":[true,null,1.5,"text",{},[]]}""", HttpStatusCode.Conflict, "Pika hardware is disabled in simulation"),
            ("within depth", withinDepth, HttpStatusCode.Conflict, "Pika hardware is disabled in simulation"),
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
            // The fixture is --simulate on ephemeral ports. Valid POST inventory
            // stays disabled over HTTP; gRPC intentionally exposes empty inventory.
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/pika/devices")
            { Content = new StringContent(input.Json, Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Piper-Token", token);
            using var response = await http.SendAsync(message);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (response.StatusCode != input.HttpStatus || body?["error"]?.GetValue<string>() != input.HttpError)
                throw new Exception("HTTP JSON validation changed for " + input.Name + ": " + response.StatusCode);
            try
            {
                var reply = await grpc.ExecuteAsync(new ControlRequest { Route = "/api/pika/devices", Json = input.Json },
                    headers, deadline: DateTime.UtcNow.AddSeconds(3));
                if (input.HttpStatus != HttpStatusCode.Conflict || reply.Status != 200 ||
                    JsonNode.Parse(reply.Json)?["devices"] is not JsonArray { Count: 0 })
                    throw new Exception("gRPC accepted an invalid JSON control tree or changed valid simulated inventory: " + input.Name);
            }
            catch (RpcException error) when (input.HttpStatus != HttpStatusCode.Conflict && error.StatusCode == StatusCode.InvalidArgument)
            {
                if (input.HttpError == objectError && error.Status.Detail != "Expected an object with unique keys.")
                    throw new Exception("gRPC object-validation message changed for " + input.Name);
                if (input.HttpError is encodingError or "Request too large" && error.Status.Detail != "Invalid control request encoding or size.")
                    throw new Exception("gRPC encoding-validation message changed for " + input.Name);
            }
        }
        Console.WriteLine($"Host JSON validation passed: {cases.Length} tree, duplicate-key, depth, encoding and byte-limit cases over HTTP and gRPC; hardware disabled.");
    }
}
