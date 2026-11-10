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
if (!int.TryParse(Option("--port", HostEndpoints.DefaultHttpPort.ToString()), out int port) || port is < HostEndpoints.MinimumPort or > HostEndpoints.MaximumPort) throw new ArgumentException("Invalid port");
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
string? pikaDistribution = PikaDistribution.ForCurrentPlatform(Option("--pika-wsl", ""));
if (!double.TryParse(Option("--pika-command-period-ms", "30"), System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out double pikaCommandPeriodMs)) throw new ArgumentException("Invalid --pika-command-period-ms");
await using var pika = new PikaSessionRegistry(pikaDevice,
    Option("--pika-agent", Path.Combine(AppContext.BaseDirectory, "pika-agent", "Piper.Pika.Agent")), pikaDistribution, pikaCommandPeriodMs / 1000);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
if (!int.TryParse(Option("--grpc-port", (port + 1).ToString()), out int grpcPort) || grpcPort is < HostEndpoints.MinimumPort or > HostEndpoints.MaximumPort || grpcPort == port) throw new ArgumentException("Invalid gRPC port");
BackendMaintenance? maintenance = null;
builder.Services.AddSingleton(provider => new PiperRpcContext(controller, pika, simulated, sessionOwner,
    maintenance ?? throw new InvalidOperationException("Backend maintenance was not initialized.")));
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = HostEndpoints.MaxRequestBytes; options.MaxSendMessageSize = HostEndpoints.MaxGrpcSendBytes; });
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Listen(IPAddress.Loopback, grpcPort, endpoint => endpoint.Protocols = HttpProtocols.Http2); k.Limits.MaxRequestBodySize = HostEndpoints.MaxRequestBytes; });
var app = builder.Build();
maintenance = new BackendMaintenance(controller.SnapshotJson, () => pika.Snapshots(),
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
    ["/api.mjs"]=("api.mjs","text/javascript; charset=utf-8"), ["/pika.mjs"]=("pika.mjs","text/javascript; charset=utf-8"), ["/pika_remote.mjs"]=("pika_remote.mjs","text/javascript; charset=utf-8"), ["/bench.mjs"]=("bench.mjs","text/javascript; charset=utf-8"), ["/style.css"]=("style.css","text/css; charset=utf-8"),
    [PiperApi.Paths.OpenApi]=("openapi.json","application/json")
};
foreach (var asset in assets.Values) if (!File.Exists(Path.Combine(web,asset.File))) throw new FileNotFoundException($"Missing shared dashboard asset {asset.File}");
app.Run(async context => {
    var request=context.Request; var response=context.Response;
    response.Headers.CacheControl="no-store"; response.Headers.XContentTypeOptions="nosniff";
    response.Headers["Referrer-Policy"]="no-referrer";
    response.Headers.ContentSecurityPolicy="default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    async Task Error(int status,string message) { response.StatusCode=status; await response.WriteAsJsonAsync(new {ok=false,error=message}); }
    async Task Reply(HostControlResult result) {
        response.StatusCode=result.Status; response.ContentType="application/json; charset=utf-8";
        await response.WriteAsync(result.ToJson());
    }
    string host=request.Host.Value??"";
    string[] hosts=HostEndpoints.LoopbackHosts(port);
    if (!hosts.Contains(host,StringComparer.OrdinalIgnoreCase) || request.Headers.Origin.Count>1 ||
        (request.Headers.Origin.Count==1 && !hosts.Any(h=>request.Headers.Origin==$"http://{h}")) || request.Headers["Sec-Fetch-Site"]=="cross-site") { await Error(403,"Only this local dashboard origin is permitted"); return; }
    string route=request.Path.Value??"/";
    string? Query(string key) => request.Query.TryGetValue(key, out var value) ? value.ToString() : null;
    try {
        if (HttpMethods.IsGet(request.Method)) {
            if (route==PiperApi.Paths.State) {
                await response.WriteAsJsonAsync(control.ReadState(OperatingSystem.IsWindows() ? "dotnet-windows" : "dotnet-linux")); return;
            }
            if (route==PiperApi.Paths.PikaState) {
                await response.WriteAsJsonAsync(control.PikaState(Query("pika_device"), Query("pika_distribution"))); return;
            }
            if (route==PiperApi.Paths.PikaInventory) { await Reply(await control.ReadPikaInventoryAsync(Query("distribution"), context.RequestAborted)); return; }
            if (assets.TryGetValue(route,out var asset)) { response.ContentType=asset.Type; await response.SendFileAsync(Path.Combine(web,asset.File)); return; }
            await Error(404,"Not found"); return;
        }
        if (!HttpMethods.IsPost(request.Method)||request.Headers["X-Piper-Token"].Count!=1||request.Headers["X-Piper-Token"]!=controller.Token) { await Error(403,"POST requires X-Piper-Token from /api/state"); return; }
        if (request.ContentType is not ("application/json" or "application/json; charset=utf-8")) { await Error(400,"Use application/json"); return; }
        using var reader=new StreamReader(request.Body,Encoding.UTF8,false,1024,true);
        string body=await reader.ReadToEndAsync(context.RequestAborted);
        if (!ControlJson.TryParse(body, out var parsed, out var rejected)) { await Error(rejected.Status, rejected.Message); return; }
        using var json = parsed;
        using var admission = control.Admit(route, json.RootElement);
        if (admission.Shutdown is { } shutdown) {
            response.OnCompleted(() => { shutdown(app.Lifetime.StopApplication); return Task.CompletedTask; });
            await response.WriteAsJsonAsync(new {ok=true,message="Closing USB transports before shutdown"}); return;
        }
        await Reply(admission.Reply ?? await control.ExecuteAsync(route, body, json.RootElement, context.RequestAborted));
    }
    catch (JsonException) { await Error(400,"Invalid JSON body"); }
    catch (BadHttpRequestException e) { await Error(e.StatusCode,e.Message); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) { app.Logger.LogError(e,"Controller request failed"); await Error(500,"Controller unavailable; inspect local logs"); }
});
await app.RunAsync();
