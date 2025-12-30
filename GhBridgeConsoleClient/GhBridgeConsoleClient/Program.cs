using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Zeroconf;

namespace GhBridgeClientFx
{
    class Program
    {
        // Used to signal when a new endpoint is announced (for mDNS wait logic)
        private static TaskCompletionSource<Endpoint> _nextAnnouncementTcs;
        // Used to signal when a missing IP announcement is detected
        private static volatile bool shouldReconnect = false;
        // -------------------- Models --------------------
        class Options
        {
            public int IntervalSec { get; set; } = 60;
            public string Cmd { get; set; } = "list";
            public string DeviceId { get; set; } = null;
            public string Host { get; set; } = null;
            public int Port { get; set; } = 8602;
            public string Token { get; set; } = null;
            public bool MdnsOff { get; set; } = false;
            public bool Tls { get; set; } = false;
            public string CmdSchedule { get; set; } = null; // "list;status:bulb1;ping;brightness:bulb1:42"
            public string LogFile { get; set; } = null;     // e.g. "logs/client.log"
            public string ConfigPath { get; set; } = "GhBridgeClientFx.json";
            public bool DevIgnoreCertErrors { get; set; } = false; // DEV ONLY
        }

        // Print a user-visible error message in red, restoring previous color
        static void PrintError(string msg)
        {
            var prev = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(msg);
            }
            finally
            {
                Console.ForegroundColor = prev;
            }
        }

        class BridgeRequest
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("cmd")] public string Cmd { get; set; }
            [JsonProperty("payload")] public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
        }

        class BridgeError
        {
            [JsonProperty("code")] public string Code { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
        }

        class BridgeResponse<T>
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("ok")] public bool Ok { get; set; }
            [JsonProperty("data")] public T Data { get; set; }
            [JsonProperty("error")] public BridgeError Error { get; set; }
        }

        // -------------------- Logger --------------------
        class Logger : IDisposable
        {
            private readonly object _lock = new object();
            private readonly string _filePath;
            private FileStream _fs;
            private StreamWriter _sw;
            private const long MaxBytes = 1_000_000; // 1MB rolling

            public Logger(string filePath)
            {
                _filePath = filePath;
                if (!string.IsNullOrEmpty(filePath))
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    _fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _sw = new StreamWriter(_fs) { AutoFlush = true };
                }
            }

            public void Info(string msg)
            {
                var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} INF {1}", DateTime.Now, msg);
                Console.WriteLine(line);
                WriteFile(line);
            }

            public void Err(string msg)
            {
                var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ERR {1}", DateTime.Now, msg);
                var prevColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = prevColor;
                }
                WriteFile(line);
            }

            private void WriteFile(string line)
            {
                if (_sw == null) return;
                lock (_lock)
                {
                    _sw.WriteLine(line);
                    if (_fs.Length > MaxBytes)
                    {
                        try
                        {
                            _sw.Dispose();
                            _fs.Dispose();
                            var roll = _filePath + ".1";
                            if (File.Exists(roll)) File.Delete(roll);
                            File.Move(_filePath, roll);
                            _fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                            _sw = new StreamWriter(_fs) { AutoFlush = true };
                        }
                        catch { /* ignore rolling errors */ }
                    }
                }
            }

            public void Dispose()
            {
                try { if (_sw != null) _sw.Dispose(); if (_fs != null) _fs.Dispose(); } catch { }
            }
        }

        // -------------------- Discovery --------------------
        class Endpoint
        {
            public string Host { get; set; }
            public int Port { get; set; }
            public override string ToString() { return Host + ":" + Port; }
        }

        class ScheduleItem
        {
            public string Cmd;
            public string DeviceId;
            public int? Value;
        }

        static async Task<Endpoint> ResolveAsync(Logger log, int fallbackPort)
        {
            try
            {
                var results = await ZeroconfResolver.ResolveAsync("_ghbridge._tcp.local.");
                var best = results != null ? results.FirstOrDefault() : null;
                if (best != null && best.IPAddress != null && best.Services != null)
                {
                    var svc = best.Services.Values.FirstOrDefault();
                    var host = best.IPAddress;
                    var port = svc != null ? svc.Port : fallbackPort;
                    log.Info("Resolve: " + host + ":" + port);
                    return new Endpoint { Host = host, Port = port };
                }
            }
            catch (Exception ex)
            {
                log.Err("ResolveAsync error: " + ex.Message);
            }
            return null;
        }

        static CancellationTokenSource _annCts;

        // FIXED: Use the 2-arg ListenForAnnouncementsAsync and filter inside the callback.
        static void ListenForAnnouncements(Logger log, Action<Endpoint> onEndpoint)
        {
            _annCts = new CancellationTokenSource();
            var token = _annCts.Token;

            // Start listening; this Task completes when cancelled.
            var _ = ZeroconfResolver.ListenForAnnouncementsAsync(ann =>
            {
                try
                {
                    // ann.Host is IZeroconfHost; filter by service type name
                    if (ann != null && ann.Host != null && ann.Host.Services != null)
                    {
                        foreach (var svc in ann.Host.Services.Values)
                        {
                            // Library migration note: use IService.Name (SRV record name) to check the service type.
                            // Accept either fully-qualified or non-local variants.
                            var typeName = svc.Name;
                            bool matches =
                                string.Equals(typeName, "_ghbridge._tcp.local.", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(typeName, "_ghbridge._tcp.", StringComparison.OrdinalIgnoreCase);

                            if (matches)
                            {
                                var host = ann.Host.IPAddress;
                                var port = svc.Port;
                                if (string.IsNullOrEmpty(host))
                                {
                                    log.Info("Announcement ignored: missing IP address for service " + typeName);
                                    shouldReconnect = true;
                                    continue;
                                }
                                log.Info("Announcement: " + host + ":" + port);
                                if (onEndpoint != null)
                                    onEndpoint(new Endpoint { Host = host, Port = port });
                                // Signal any waiter for next announcement
                                if (_nextAnnouncementTcs != null && !_nextAnnouncementTcs.Task.IsCompleted)
                                    _nextAnnouncementTcs.TrySetResult(new Endpoint { Host = host, Port = port });
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Err("Announcement handler error: " + ex.Message);
                }
            }, token);
        }

        // -------------------- CLI & Config --------------------
        static void PrintHelp()
        {
            Console.WriteLine(
@"GhBridgeClientFx (NET Fx 4.8)
Usage:
  GhBridgeClientFx.exe [--interval N] [--cmd list|status|on|off|toggle|brightness|ping]
                       [--deviceId <id>] [--host <ip>] [--port <port>]
                       [--token <secret>] [--mdns-off] [--tls]
                       [--cmd-schedule ""list;status:bulb1;ping;brightness:bulb1:42""]
                       [--log <path>] [--config <path>] [--dev-ignore-cert-errors]
");
        }

        static Options LoadOptions(string[] args)
        {
            var opt = new Options();
            bool configParseError = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    Environment.Exit(0);
                }
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--"))
                {
                    var key = a.TrimStart('-');
                    var val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "true";
                    dict[key] = val;
                }
            }

            Func<string, string, string> get = (key, def) =>
            {
                string v;
                return dict.TryGetValue(key, out v) ? v : def;
            };

            var cfgPath = get("config", null);
            if (string.IsNullOrEmpty(cfgPath)) cfgPath = opt.ConfigPath;
            if (File.Exists(cfgPath))
            {
                try
                {
                    var json = File.ReadAllText(cfgPath);
                    var fromFile = JsonConvert.DeserializeObject<Options>(json);
                    if (fromFile != null) opt = fromFile;
                }
                catch
                {
                    configParseError = true;
                }
            }

            int iv;
            if (dict.ContainsKey("interval") && int.TryParse(get("interval", null), out iv)) opt.IntervalSec = iv;
            var cmd = get("cmd", null); if (!string.IsNullOrEmpty(cmd)) opt.Cmd = cmd;
            var did = get("deviceId", null); if (!string.IsNullOrEmpty(did)) opt.DeviceId = did;
            var host = get("host", null); if (!string.IsNullOrEmpty(host)) opt.Host = host;
            int port;
            if (dict.ContainsKey("port") && int.TryParse(get("port", null), out port)) opt.Port = port;
            var token = get("token", null); if (!string.IsNullOrEmpty(token)) opt.Token = token;
            bool mdnsOff;
            if (bool.TryParse(get("mdns-off", opt.MdnsOff ? "true" : "false"), out mdnsOff)) opt.MdnsOff = mdnsOff;
            bool tls;
            if (bool.TryParse(get("tls", opt.Tls ? "true" : "false"), out tls)) opt.Tls = tls;
            var sched = get("cmd-schedule", null); if (!string.IsNullOrEmpty(sched)) opt.CmdSchedule = sched;
            var logFile = get("log", null); if (!string.IsNullOrEmpty(logFile)) opt.LogFile = logFile;
            opt.ConfigPath = cfgPath;
            bool ignore;
            if (bool.TryParse(get("dev-ignore-cert-errors", opt.DevIgnoreCertErrors ? "true" : "false"), out ignore)) opt.DevIgnoreCertErrors = ignore;

            if (configParseError)
            {
                PrintError("WARNING: Failed to parse config file. Using defaults and command-line options.");
            }
            return opt;
        }

        // -------------------- Request & Schedule --------------------
        static BridgeRequest BuildRequest(string cmd, string deviceId, Dictionary<string, object> extraPayload)
        {
            var payload = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(deviceId))
                payload["deviceId"] = deviceId;
            if (extraPayload != null)
            {
                foreach (var kv in extraPayload) payload[kv.Key] = kv.Value;
            }

            return new BridgeRequest
            {
                Id = Guid.NewGuid().ToString(),
                Cmd = cmd,
                Payload = payload
            };
        }

        static List<ScheduleItem> BuildSchedule(string raw)
        {
            var list = new List<ScheduleItem>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            var parts = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var p = part.Trim();
                var segs = p.Split(':');
                if (segs.Length == 1)
                {
                    list.Add(new ScheduleItem { Cmd = segs[0], DeviceId = null, Value = null });
                }
                else if (segs.Length == 2)
                {
                    list.Add(new ScheduleItem { Cmd = segs[0], DeviceId = segs[1], Value = null });
                }
                else
                {
                    int v;
                    if (int.TryParse(segs[2], out v))
                        list.Add(new ScheduleItem { Cmd = segs[0], DeviceId = segs[1], Value = v });
                    else
                        list.Add(new ScheduleItem { Cmd = segs[0], DeviceId = segs[1], Value = null });
                }
            }
            return list;
        }

        // -------------------- WebSocket Helpers --------------------
        static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken cancel)
        {
            var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var result = await ws.ReceiveAsync(buffer, cancel);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                    if (result.EndOfMessage) break;
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // -------------------- Entry Point --------------------
        static void Main(string[] args)
        {
            RunAsync(args).GetAwaiter().GetResult();
        }

        static async Task<int> RunAsync(string[] args)
        {
            var opt = LoadOptions(args);
            using (var log = new Logger(opt.LogFile))
            {
                log.Info("GhBridgeClientFx starting...");
                log.Info(string.Format("Options: interval={0}s cmd={1} deviceId={2} host={3} port={4} mdnsOff={5} tls={6} schedule={7}",
                    opt.IntervalSec, opt.Cmd, opt.DeviceId, opt.Host, opt.Port, opt.MdnsOff, opt.Tls, opt.CmdSchedule));

                Endpoint current = null;

                if (!opt.MdnsOff)
                {
                    ListenForAnnouncements(log, ep => { current = ep; });
                    var initial = await ResolveAsync(log, opt.Port);
                    if (initial != null) current = initial;
                }

                if (opt.MdnsOff && opt.Host != null)
                {
                    current = new Endpoint { Host = opt.Host, Port = opt.Port };
                }

                if (current == null)
                {
                    log.Info("No endpoint yet. Waiting for discovery...");
                }



                Func<string> scheme = () => opt.Tls ? "wss" : "ws";

                // ---- TLS dev ignore (unsafe; for local testing only) ----
                if (opt.Tls && opt.DevIgnoreCertErrors)
                {
                    ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslErrors) => true;
                    log.Err("DEV MODE: Ignoring TLS certificate validation. Do not use in production.");
                }

                int loopCount = 0;
                while (true)
                {
                    loopCount++;
                    log.Info($"[Loop {loopCount}] Top of main loop");
                    shouldReconnect = false;
                    bool exitedForReconnect = false;
                    // Discover endpoint every time
                    Endpoint epToUse = null;
                    if (!opt.MdnsOff)
                    {
                        var ep = await ResolveAsync(log, opt.Port);
                        if (ep != null) current = ep;
                    }
                    // If a missing IP announcement was detected, restart the main loop
                    if (shouldReconnect || exitedForReconnect)
                    {
                        log.Info("Restarting main loop after missing IP announcement.");
                        await Task.Delay(2000); // Prevent tight reconnect loop
                        continue;
                    }
                    else if (opt.Host != null)
                    {
                        current = new Endpoint { Host = opt.Host, Port = opt.Port };
                    }
                    epToUse = current != null ? current : (opt.Host != null ? new Endpoint { Host = opt.Host, Port = opt.Port } : null);
                    if (epToUse == null)
                    {
                        log.Info("No endpoint. Sleeping 2s and retrying discovery...");
                        await Task.Delay(2000);
                        continue;
                    }

                    var baseUri = scheme() + "://" + epToUse.Host + ":" + epToUse.Port + "/ws";
                    var uri = baseUri;

                    log.Info($"[Loop {loopCount}] Entering WebSocket block for endpoint: {uri}");
                    using (var cws = new ClientWebSocket())
                    {
                        cws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        log.Info("Connecting to " + uri + "...");
                        try
                        {
                            await cws.ConnectAsync(new Uri(uri), CancellationToken.None);
                            log.Info("Connected.");

                            // Fetch device list
                            var req = BuildRequest("list", null, null);
                            var json = JsonConvert.SerializeObject(req);
                            log.Info(string.Format("--> req id={0} cmd={1} payload={2}", req.Id, req.Cmd, JsonConvert.SerializeObject(req.Payload)));
                            var sendBuf = Encoding.UTF8.GetBytes(json);
                            await cws.SendAsync(new ArraySegment<byte>(sendBuf), WebSocketMessageType.Text, true, CancellationToken.None);

                            // Read response
                            var respText = await ReceiveTextAsync(cws, CancellationToken.None);
                            if (respText == null)
                            {
                                log.Info("Server initiated close.");
                                await Task.Delay(2000);
                                continue;
                            }
                            // Comment out the device list JSON response logging
                            // log.Info("<-- resp " + respText);

                            // Parse device list
                            JObject root = null;
                            List<JToken> devices = null;
                            try { root = JObject.Parse(respText); }
                            catch (Exception ex) { log.Err("Response parse error: " + ex.Message); await Task.Delay(2000); continue; }
                            var ok = (bool?)root["ok"] ?? false;
                            if (!ok)
                            {
                                var errCode = (string)root.SelectToken("error.code");
                                var errMsg = (string)root.SelectToken("error.message");
                                log.Err("List command failed: " + errCode + ":" + errMsg);
                                await Task.Delay(2000);
                                continue;
                            }
                            var devArr = root.SelectToken("data.devices") as JArray;
                            if (devArr == null || devArr.Count == 0)
                            {
                                log.Info("No devices found.");
                                devices = new List<JToken>();
                            }
                            else
                            {
                                devices = devArr.ToList();
                            }


                            // Display devices with state
                            Console.WriteLine("\nDevices:");
                            if (devices.Count == 0)
                            {
                                PrintError("  No devices found.");
                                // Wait a moment before next loop
                                await Task.Delay(5000);
                                continue;
                            }
                            else
                            {
                                for (int i = 0; i < devices.Count; i++)
                                {
                                    var d = devices[i];
                                    var name = (string)d["name"];
                                    string stateStr = null;
                                    var stateToken = d["state"];
                                    if (stateToken != null && stateToken.Type == JTokenType.Object)
                                    {
                                        var onlineProp = stateToken["online"];
                                        var onProp = stateToken["on"];
                                        bool isOnline = onlineProp != null && onlineProp.Type == JTokenType.Boolean && (bool)onlineProp;
                                        if (!isOnline)
                                        {
                                            stateStr = "OFFLINE";
                                        }
                                        else if (onProp != null && onProp.Type == JTokenType.Boolean)
                                        {
                                            stateStr = (bool)onProp ? "ON" : "OFF";
                                        }
                                        else
                                        {
                                            stateStr = "Unknown";
                                        }
                                    }
                                    else
                                    {
                                        stateStr = "Unknown";
                                    }
                                    Console.WriteLine($"  [{i + 1}]. {name} ({stateStr})");
                                }

                                // User menu
                                Console.WriteLine("\nOptions:");
                                Console.WriteLine("  1. Select a device to toggle");
                                Console.WriteLine("  2. Refresh device list");
                                Console.WriteLine("  3. Exit");
                                Console.Write("Choose an option [1-3]: ");

                                string choice = null;
                                var inputTask = Task.Run(() => Console.ReadLine());
                                var timeout = TimeSpan.FromMinutes(1);
                                var pollInterval = TimeSpan.FromMilliseconds(100);
                                var start = DateTime.UtcNow;
                                bool menuTimeout = false;
                                while (!inputTask.IsCompleted && (DateTime.UtcNow - start) < timeout)
                                {
                                    if (shouldReconnect)
                                    {
                                        log.Info("Detected missing IP announcement during menu wait. Disconnecting and restarting loop.");
                                        exitedForReconnect = true;
                                        await Task.Delay(2000);
                                        continue;
                                    }
                                    await Task.Delay(pollInterval);
                                }
                                if (inputTask.IsCompleted)
                                {
                                    choice = inputTask.Result;
                                }
                                else if (shouldReconnect)
                                {
                                    // Detected missing IP, continue to outer loop
                                    exitedForReconnect = true;
                                    continue;
                                }
                                else
                                {
                                    log.Info("\nNo menu selection received after 1 minute. Disconnecting and continuing loop.");
                                    // Wait a moment for user to see the message
                                    await Task.Delay(1000);
                                    continue;
                                }

                                // Menu choice handling (must be in scope of 'choice')
                                if (choice == "1")
                                {
                                    if (devices.Count == 0)
                                    {
                                        PrintError("No devices to toggle.");
                                        await Task.Delay(5000);
                                        continue;
                                    }
                                    int sel = -1;
                                    while (true)
                                    {
                                        Console.Write($"Select device [1-{devices.Count}]: ");
                                        var input = Console.ReadLine();
                                        if (int.TryParse(input, out sel) && sel >= 1 && sel <= devices.Count) break;
                                        PrintError("Invalid selection. Try again.");
                                    }
                                    var selected = devices[sel - 1];
                                    var selectedId = (string)selected["id"];
                                    // Determine current state and online status
                                    var stateToken = selected["state"];
                                    bool? currentOn = null;
                                    bool? isOnline = null;
                                    if (stateToken != null && stateToken.Type == JTokenType.Object)
                                    {
                                        var onlineProp = stateToken["online"];
                                        var onProp = stateToken["on"];
                                        if (onlineProp != null && onlineProp.Type == JTokenType.Boolean)
                                            isOnline = (bool)onlineProp;
                                        if (onProp != null && onProp.Type == JTokenType.Boolean)
                                            currentOn = (bool)onProp;
                                    }

                                    // If device is known to be offline, show error and don't send commands
                                    if (isOnline.HasValue && !isOnline.Value)
                                    {
                                        PrintError("Selected device is OFFLINE. Cannot send command.");
                                        await Task.Delay(5000);
                                        continue;
                                    }

                                    if (currentOn == null)
                                    {
                                        PrintError("Unable to determine current state for toggle.");
                                        await Task.Delay(5000);
                                        continue;
                                    }
                                    bool newState = !currentOn.Value;
                                    // Build toggle command as top-level properties
                                    var toggleObj = new JObject
                                    {
                                        ["cmd"] = "toggle",
                                        ["id"] = Guid.NewGuid().ToString(),
                                        ["deviceId"] = selectedId,
                                        ["state"] = newState
                                    };
                                    var toggleJson = toggleObj.ToString(Formatting.None);
                                    log.Info($"--> req id={toggleObj["id"]} cmd=toggle deviceId={selectedId} state={newState}");
                                    var toggleBuf = Encoding.UTF8.GetBytes(toggleJson);
                                    await cws.SendAsync(new ArraySegment<byte>(toggleBuf), WebSocketMessageType.Text, true, CancellationToken.None);
                                    // Read toggle response
                                    var toggleRespText = await ReceiveTextAsync(cws, CancellationToken.None);
                                    if (toggleRespText == null)
                                    {
                                        log.Info("Server initiated close after toggle.");
                                        await Task.Delay(2000);
                                        continue;
                                    }
                                    log.Info("<-- resp " + toggleRespText);
                                    Console.WriteLine("\nToggle command sent. Response: ");
                                    // Parse response to check for success or error
                                    bool isSuccess = false;
                                    bool isHomeException = false;
                                    string errorMsg = null;
                                    try
                                    {
                                        var respObj = JObject.Parse(toggleRespText);
                                        isSuccess = (bool?)respObj["ok"] == true;
                                        var errorObj = respObj["error"];
                                        if (errorObj != null && errorObj.Type == JTokenType.Object)
                                        {
                                            var code = (string)errorObj["code"];
                                            if (!string.IsNullOrEmpty(code) && code.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                                                isHomeException = true;
                                            errorMsg = (string)errorObj["message"];
                                        }
                                    }
                                    catch { }

                                    if (isSuccess)
                                    {
                                        var prevColor = Console.ForegroundColor;
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.WriteLine(toggleRespText);
                                        Console.ForegroundColor = prevColor;
                                    }
                                    else
                                    {
                                        var prevColor = Console.ForegroundColor;
                                        Console.ForegroundColor = ConsoleColor.Red;
                                        Console.WriteLine(toggleRespText);
                                        Console.ForegroundColor = prevColor;
                                    }
                                    // Wait 5 seconds before continuing
                                    await Task.Delay(5000);
                                    continue;
                                }
                                else if (choice == "2")
                                {
                                    // Refresh: just continue to next loop iteration
                                    // No delay for refresh
                                    continue;
                                }
                                else if (choice == "3")
                                {
                                    Console.WriteLine("Exiting application.");
                                    return 0;
                                }
                                else
                                {
                                    PrintError("Invalid option. Try again.");
                                    await Task.Delay(5000);
                                    continue;
                                }

                                // Add delay after each iteration unless user chose refresh (which continues immediately)
                                await Task.Delay(opt.IntervalSec * 1000);

                                // If a missing IP announcement was detected, break to restart loop
                                if (shouldReconnect)
                                {
                                    log.Info("Detected missing IP announcement. Disconnecting and restarting loop.");
                                    exitedForReconnect = true;
                                    break;
                                }
                            }
                        }
                        catch (WebSocketException wse)
                        {
                            log.Err("Connect/Send error: " + wse.Message);
                            // Wait for next announcement before retrying
                            if (!opt.MdnsOff)
                            {
                                log.Info("Waiting for next mDNS announcement before retrying connection...");
                                _nextAnnouncementTcs = new TaskCompletionSource<Endpoint>();
                                var completed = await Task.WhenAny(_nextAnnouncementTcs.Task, Task.Delay(TimeSpan.FromMinutes(5)));
                                if (completed == _nextAnnouncementTcs.Task)
                                {
                                    var newEp = _nextAnnouncementTcs.Task.Result;
                                    log.Info($"New endpoint announced: {newEp.Host}:{newEp.Port}");
                                    current = newEp;
                                }
                                else
                                {
                                    log.Info("No new announcement received after 5 minutes. Retrying discovery.");
                                }
                                _nextAnnouncementTcs = null;
                            }
                            else
                            {
                                await Task.Delay(2000); // Add delay before retrying on connection error
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Err("General error: " + ex.Message);
                            await Task.Delay(2000); // Add delay before retrying on general error
                        }
                        finally
                        {
                            log.Info($"[Loop {loopCount}] Exiting WebSocket block for endpoint: {uri}");
                            if (cws.State == WebSocketState.Open || cws.State == WebSocketState.CloseReceived)
                            {
                                log.Info($"Disconnecting from {uri} ...");
                                try { await cws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
                            }
                        }
                    }
                    log.Info($"[Loop {loopCount}] End of main loop iteration");
                }
                log.Info("[EXIT] Application is about to return from RunAsync. This should not happen unless user selected Exit.");
                // (Unreachable, but required for Task<int> signature)
                return 0;
            }
        }
    }
}
