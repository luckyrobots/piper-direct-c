using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Host;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

bool simulated = args.Contains("--simulate");
string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i < 0 ? fallback : i+1 < args.Length ? args[i+1] : throw new ArgumentException($"Missing {name}"); }
if (!int.TryParse(Option("--sim-robots", "1"), out int simulatedRobotCount) || simulatedRobotCount is < 1 or > 16 || !simulated && simulatedRobotCount != 1)
    throw new ArgumentException("--sim-robots requires --simulate and a count from 1 to 16");
if (!int.TryParse(Option("--port", "8765"), out int port) || port < 1024 || port > 65535) throw new ArgumentException("Invalid port");
string web = Path.GetFullPath(Option("--web-root", Path.Combine(AppContext.BaseDirectory, "web")));
string directory = Path.GetFullPath(Option("--state-dir", Path.Combine(AppContext.BaseDirectory, simulated ? "data-sim" : "data")));
string sessionOwner = Option("--session-owner", "");
if (sessionOwner.Length > 0 && !Guid.TryParse(sessionOwner, out _)) throw new ArgumentException("Invalid session owner");
string supervisorIdentity = Option("--restart-supervisor-id", "");
string supervisorProcess = Option("--restart-supervisor-pid", "");
int? supervisorPid = null;
if (supervisorIdentity.Length > 0 || supervisorProcess.Length > 0)
{
    if (!Guid.TryParse(supervisorIdentity, out _) || !int.TryParse(supervisorProcess, out int value) || value <= 0)
        throw new ArgumentException("Invalid restart supervisor identity");
    supervisorPid = value;
}
string mutexName = $"PiperDirect.{Environment.UserName}.{(simulated ? $"Simulation.{port}" : "Hardware")}";
using var mutex = new Mutex(true, mutexName, out bool owns);
if (!owns) throw new InvalidOperationException("Another managed controller owns this hardware session");
using var controller = new ControllerService(simulated, directory, simulatedRobotCount);
string pikaDevice = simulated ? "" : Option("--pika-device", "");
string? pikaDistribution = OperatingSystem.IsWindows() ? Option("--pika-wsl", "Ubuntu") : null;
if (!double.TryParse(Option("--pika-command-period-ms", "30"), System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out double pikaCommandPeriodMs)) throw new ArgumentException("Invalid --pika-command-period-ms");
await using var pika = new PikaSessionRegistry(pikaDevice,
    Option("--pika-agent", Path.Combine(AppContext.BaseDirectory, "pika-agent", "Piper.Pika.Agent")), pikaDistribution, pikaCommandPeriodMs / 1000);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
if (!int.TryParse(Option("--grpc-port", (port + 1).ToString()), out int grpcPort) || grpcPort < 1024 || grpcPort > 65535 || grpcPort == port) throw new ArgumentException("Invalid gRPC port");
BackendMaintenance? maintenance = null;
builder.Services.AddSingleton(provider => new PiperRpcContext(controller, pika, simulated, sessionOwner,
    maintenance ?? throw new InvalidOperationException("Backend maintenance was not initialized.")));
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 32768; options.MaxSendMessageSize = 1024 * 1024; });
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Listen(IPAddress.Loopback, grpcPort, endpoint => endpoint.Protocols = HttpProtocols.Http2); k.Limits.MaxRequestBodySize = 32768; });
var app = builder.Build();
maintenance = new BackendMaintenance(() => JsonNode.Parse(controller.Snapshot())!.AsObject(), () => pika.Snapshots(),
    () => { Environment.ExitCode = BackendMaintenance.RestartExitCode; app.Lifetime.StopApplication(); },
    Environment.ProcessId, supervisorPid, supervisorPid.HasValue ? supervisorIdentity : null,
    pluginOwner: sessionOwner.Length > 0 ? sessionOwner : null);
var control = app.Services.GetRequiredService<PiperRpcContext>().Control;
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
    string? Query(string key) => request.Query.TryGetValue(key, out var value) ? value.ToString() : null;
    try {
        if (HttpMethods.IsGet(request.Method)) {
            if (route=="/api/state") {
                await response.WriteAsJsonAsync(control.ReadState(OperatingSystem.IsWindows() ? "dotnet-windows" : "dotnet-linux")); return;
            }
            if (route=="/api/pika/state") {
                await response.WriteAsJsonAsync(control.PikaState(Query("pika_device"), Query("pika_distribution"))); return;
            }
            if (route=="/api/pika/devices") {
                if (simulated) { await response.WriteAsJsonAsync(HostControlDispatcher.EmptySimulatedInventory); return; }
                try { await response.WriteAsJsonAsync(await control.DiscoverPikaAsync(Query("distribution"), context.RequestAborted)); }
                catch (Exception e) when (HostControlDispatcher.IsControlOperationError(e)) { await Error(409,e.Message); }
                return;
            }
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
        if (json.RootElement.ValueKind!=JsonValueKind.Object||!ControlJson.HasUniqueKeys(json.RootElement)) { await Error(400,"Expected one JSON object with unique keys"); return; }
        using var admission = control.Admit(route, json.RootElement, HostControlTransport.Http);
        if (admission.Error is { } denied) { await Error(409, denied); return; }
        if (admission.Restart is { } restarting)
        {
            response.StatusCode = restarting.Ok ? 202 : 409;
            await response.WriteAsJsonAsync(restarting); return;
        }
        if (admission.Shutdown) {
            response.OnCompleted(() => { maintenance.CommitShutdown(app.Lifetime.StopApplication); return Task.CompletedTask; });
            await response.WriteAsJsonAsync(new {ok=true,message="Closing USB transports before shutdown"}); return;
        }
        var result = await control.ExecuteAsync(route, body, json.RootElement, HostControlTransport.Http, context.RequestAborted);
        response.StatusCode = result.Status;
        if (result.PikaAudit is { } audit)
            await response.WriteAsJsonAsync(new { error = result.Error, pika_follow_audit = audit });
        else if (result.Error is { } error)
            await Error(result.Status, error);
        else if (result.RawJson is { } raw)
        {
            response.ContentType = "application/json";
            await response.WriteAsync(raw);
        }
        else if (result.Payload is JsonObject gripper)
        {
            try { await response.WriteAsJsonAsync(gripper); }
            catch (Exception e) when (HostControlDispatcher.IsControlOperationError(e)) { await Error(409, e.Message); }
        }
        else await response.WriteAsJsonAsync(result.Payload);
    }
    catch (JsonException) { await Error(400,"Invalid JSON body"); }
    catch (BadHttpRequestException e) { await Error(e.StatusCode,e.Message); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) { app.Logger.LogError(e,"Controller request failed"); await Error(500,"Controller unavailable; inspect local logs"); }
});
await app.RunAsync();
