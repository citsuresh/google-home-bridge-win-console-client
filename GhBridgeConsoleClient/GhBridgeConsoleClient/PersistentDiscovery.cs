
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace GhBridgeConsoleClient
{
    public static class PersistentDiscovery
    {
        /// <summary>
        /// Starts continuous mDNS listening. When a GHBridge service appears,
        /// onBridgeFound(wsUri) is invoked. Stop via cancellation.
        /// </summary>
        public static async Task StartAsync(Action<Uri> onBridgeFound, CancellationToken cancel)
        {
            // Listen for unsolicited mDNS announcements.
            // NOTE: This overload REQUIRES the callback argument.
            var listeningTask = ZeroconfResolver.ListenForAnnouncementsAsync(
                announcement =>
                {
                    try
                    {
                        // Each announcement includes a Host with Services.
                        // IService.Name is the DNS-SD service type (e.g. "_ghbridge._tcp").
                        const string ghType = "_ghbridge._tcp";

                        var host = announcement.Host;
                        var match = host.Services.Values.FirstOrDefault(s => s.Name == ghType);

                        if (match != null && !string.IsNullOrWhiteSpace(host.IPAddress))
                        {
                            var port = match.Port > 0 ? match.Port : 8602;
                            var wsUri = new Uri($"ws://{host.IPAddress}:{port}/ws");
                            onBridgeFound?.Invoke(wsUri);
                        }
                        else
                        {
                            // Optional: do a quick active probe to fill gaps.
                            Task.Run(async () =>
                            {
                                var results = await ZeroconfResolver.ResolveAsync(
                                    "_ghbridge._tcp.local.",
                                    scanTime: TimeSpan.FromSeconds(1),
                                    cancellationToken: cancel);

                                foreach (var h in results)
                                {
                                    var svc = h.Services.Values.FirstOrDefault(s => s.Name == ghType);
                                    var port = svc?.Port ?? 8602;
                                    if (!string.IsNullOrWhiteSpace(h.IPAddress))
                                    {
                                        var wsUri2 = new Uri($"ws://{h.IPAddress}:{port}/ws");
                                        onBridgeFound?.Invoke(wsUri2);
                                        break;
                                    }
                                }
                            }, cancel);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Announcement handling error: " + ex.Message);
                    }
                },
                cancel // cancellation stops the listening task
            );

            // Keep the task alive until cancelled.
            await listeningTask;
        }

        /// <summary>
        /// Minimal example: connect and issue a "list" command.
        /// </summary>
        public static async Task ConnectAndListAsync(Uri serverUri, CancellationToken cancel)
        {
            using (var ws = new ClientWebSocket())
            {
                try
                {
                    Console.WriteLine($"Connecting to {serverUri} …");
                    await ws.ConnectAsync(serverUri, cancel);
                    Console.WriteLine("Connected.");

                    var json = Encoding.UTF8.GetBytes("{\"cmd\":\"list\"}");
                    await ws.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, cancel);

                    var buffer = new byte[8192];
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                    Console.WriteLine("<< " + Encoding.UTF8.GetString(buffer, 0, res.Count));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("WebSocket error: " + ex.Message);
                }
            }
        }
    }
}
