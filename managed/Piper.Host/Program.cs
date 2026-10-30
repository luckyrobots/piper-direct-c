using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Host;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

bool simulated = args.Contains("--simulate");
string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i < 0 ? fallback : i+1 < args.Length ? args[i+1] : throw new ArgumentException($"Missing {name}"); }
if (!int.TryParse(Option("--port", "8765"), out int port) || port < 1024 || port > 65535) throw new ArgumentException("Invalid port");
string web = Path.GetFullPath(Option("--web-root", Path.Combine(AppContext.BaseDirectory, "web")));
string directory = Path.GetFullPath(Option("--state-dir", Path.Combine(AppContext.BaseDirectory, simulated ? "data-sim" : "data")));
string sessionOwner = Option("--session-owner", "");
if (sessionOwner.Length > 0 && !Guid.TryParse(sessionOwner, out _)) throw new ArgumentException("Invalid session owner");
string mutexName = $"PiperDirect.{Environment.UserName}.{(simulated ? $"Simulation.{port}" : "Hardware")}";
using var mutex = new Mutex(true, mutexName, out bool owns);
if (!owns) throw new InvalidOperationException("Another managed controller owns this hardware session");
using var controller = new ControllerService(simulated, directory);
string pikaDevice = simulated ? "" : Option("--pika-device", "");
string? pikaDistribution = OperatingSystem.IsWindows() ? Option("--pika-wsl", "Ubuntu") : null;
await using var pika = new PikaProcessService(pikaDevice,
    Option("--pika-agent", Path.Combine(AppContext.BaseDirectory, "pika-agent", "Piper.Pika.Agent")), pikaDistribution);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
if (!int.TryParse(Option("--grpc-port", (port + 1).ToString()), out int grpcPort) || grpcPort < 1024 || grpcPort > 65535 || grpcPort == port) throw new ArgumentException("Invalid gRPC port");
builder.Services.AddSingleton(new PiperRpcContext(controller, pika, simulated, sessionOwner));
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 32768; options.MaxSendMessageSize = 1024 * 1024; });
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Listen(IPAddress.Loopback, grpcPort, endpoint => endpoint.Protocols = HttpProtocols.Http2); k.Limits.MaxRequestBodySize = 32768; });
var app = builder.Build();
app.MapWhen(context => context.Connection.LocalPort == grpcPort, grpc => {
    grpc.UseRouting();
    grpc.UseEndpoints(endpoints => endpoints.MapGrpcService<PiperGrpcService>());
});
var assets = new Dictionary<string,(string File,string Type)> {
    ["/"]=("index.html","text/html; charset=utf-8"), ["/app.js"]=("app.js","text/javascript; charset=utf-8"),
    ["/pika.mjs"]=("pika.mjs","text/javascript; charset=utf-8"), ["/pika_remote.mjs"]=("pika_remote.mjs","text/javascript; charset=utf-8"), ["/bench.mjs"]=("bench.mjs","text/javascript; charset=utf-8"), ["/style.css"]=("style.css","text/css; charset=utf-8"),
    ["/api/openapi.json"]=("openapi.json","application/json")
};
foreach (var asset in assets.Values) if (!File.Exists(Path.Combine(web,asset.File))) throw new FileNotFoundException($"Missing shared dashboard asset {asset.File}");
app.Run(async context => {
    var request=context.Request; var response=context.Response;
    response.Headers.CacheControl="no-store"; response.Headers.XContentTypeOptions="nosniff";
    response.Headers["Referrer-Policy"]="no-referrer";
    response.Headers.ContentSecurityPolicy="default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    async Task Error(int status,string message) { response.StatusCode=status; await response.WriteAsJsonAsync(new {ok=false,error=message}); }
    string host=request.Host.Value??"";
    string[] hosts=[$"127.0.0.1:{port}",$"localhost:{port}"];
    if (!hosts.Contains(host,StringComparer.OrdinalIgnoreCase) || request.Headers.Origin.Count>1 ||
        (request.Headers.Origin.Count==1 && !hosts.Any(h=>request.Headers.Origin==$"http://{h}")) || request.Headers["Sec-Fetch-Site"]=="cross-site") { await Error(403,"Only this local dashboard origin is permitted"); return; }
    string route=request.Path.Value??"/";
    try {
        if (HttpMethods.IsGet(request.Method)) {
            if (route=="/api/state") {
                var state=JsonNode.Parse(controller.Snapshot())!.AsObject();
                state["backend"]=OperatingSystem.IsWindows()?"dotnet-windows":"dotnet-linux";
                state["pika"]=pika.Snapshot();
                state["backend_process_id"]=Environment.ProcessId;
                state["camera"]=new JsonObject { ["configured"]=false,["running"]=false,["fresh"]=false,["error"]="Use local browser camera selection",["name"]="Local USB camera",["sequence"]=0 };
                await response.WriteAsJsonAsync(state); return;
            }
            if (route=="/api/pika/state") { await response.WriteAsJsonAsync(pika.Snapshot()); return; }
            if (route=="/api/camera/frame") { await Error(409,"Use local browser camera preview"); return; }
            if (assets.TryGetValue(route,out var asset)) { response.ContentType=asset.Type; await response.SendFileAsync(Path.Combine(web,asset.File)); return; }
            await Error(404,"Not found"); return;
        }
        if (!HttpMethods.IsPost(request.Method)||request.Headers["X-Piper-Token"].Count!=1||request.Headers["X-Piper-Token"]!=controller.Token) { await Error(403,"POST requires X-Piper-Token from /api/state"); return; }
        if (request.ContentType is not ("application/json" or "application/json; charset=utf-8")) { await Error(400,"Use application/json"); return; }
        using var reader=new StreamReader(request.Body,Encoding.UTF8,false,1024,true);
        string body=await reader.ReadToEndAsync(context.RequestAborted);
        if (Encoding.UTF8.GetByteCount(body)>16384) { await Error(413,"Request too large"); return; }
        if (body.Contains("\\u",StringComparison.Ordinal)||body.Contains('\0')) { await Error(400,"Unicode escapes and NUL are unsupported in control requests"); return; }
        using var json=JsonDocument.Parse(body,new JsonDocumentOptions {MaxDepth=32});
        static bool Unique(JsonElement element) {
            if (element.ValueKind==JsonValueKind.Object) { var keys=new HashSet<string>(StringComparer.Ordinal); foreach (var p in element.EnumerateObject()) if (!keys.Add(p.Name)||!Unique(p.Value)) return false; }
            if (element.ValueKind==JsonValueKind.Array) foreach (var e in element.EnumerateArray()) if (!Unique(e)) return false;
            return true;
        }
        if (json.RootElement.ValueKind!=JsonValueKind.Object||!Unique(json.RootElement)) { await Error(400,"Expected one JSON object with unique keys"); return; }
        if (!SessionOwnerGuard.Allows(sessionOwner,route,json.RootElement)) { await Error(409,SessionOwnerGuard.Denied); return; }
        if (route=="/api/shutdown") {
            using var current=JsonDocument.Parse(controller.Snapshot());
            if (current.RootElement.GetProperty("motion").GetProperty("active").GetBoolean()) { await Error(409,"Stop the active control action before restarting the server"); return; }
            response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
            await response.WriteAsJsonAsync(new {ok=true,message="Closing USB transports before shutdown"}); return;
        }
        if (route.StartsWith("/api/pika/",StringComparison.Ordinal)) {
            if (simulated) { await Error(409,"Pika hardware is disabled in simulation"); return; }
            if (route=="/api/pika/configure" && JsonNode.Parse(controller.Snapshot())?["motion"]?["active"]?.GetValue<bool>()==true) { await Error(409,"Finish the active arm movement before changing Pika assignment"); return; }
            try { await response.WriteAsJsonAsync(await pika.ExecuteAsync(route[10..],json.RootElement,context.RequestAborted)); }
            catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or ArgumentException) { await Error(409,e.Message); }
            return;
        }
        if (route=="/api/camera") {
            if (!json.RootElement.TryGetProperty("enabled",out var enabled)||enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { await Error(400,"enabled must be true or false"); return; }
            if (enabled.GetBoolean()) { await Error(409,"Use local browser camera preview"); return; }
            await response.WriteAsJsonAsync(new {running=false}); return;
        }
        var result=controller.Execute(route,body); response.StatusCode=result.Status; response.ContentType="application/json"; await response.WriteAsync(result.Json);
    }
    catch (JsonException) { await Error(400,"Invalid JSON body"); }
    catch (BadHttpRequestException e) { await Error(e.StatusCode,e.Message); }
    catch (Exception e) { app.Logger.LogError(e,"Controller request failed"); await Error(500,"Controller unavailable; inspect local logs"); }
});
await app.RunAsync();
