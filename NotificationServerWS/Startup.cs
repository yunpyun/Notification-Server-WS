using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.WebSockets;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace NotificationServerWS
{
    public class NotificationItem
    {
        public string urlws;
        public string callid;
        public string cmd;
        public string ext;
        public string phone;
        public string type;
        public string duration;
        public string link;
        public string status;
        public string clientFio;
    }

    public class Startup
    {
        // лог
        private readonly ILogger<Startup> _logger;

        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddConsole();
                builder.AddEventSourceLogger();
            });
            _logger = loggerFactory.CreateLogger<Startup>();
        }

        // список всех клиентов
        private static List<WebSocket> Clients = new List<WebSocket>();

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            _logger.LogInformation("Configure called");

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            var wsOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(120) };
            app.UseWebSockets(wsOptions);
            app.Use(async (context, next) =>
            {
                // принять http-запрос
                if (context.Request.Path == "/httpsend")
                {
                    _logger.LogInformation("/httpsend called");
                    // URL для обращения к WebSocket
                    string urlWS = "";
                    // входящий JSON
                    string inputJSON = "";

                    // считывание содержимого тела запроса в inputJSON
                    using (StreamReader stream = new StreamReader(context.Request.Body))
                    {
                        inputJSON = await stream.ReadToEndAsync();
                    }

                    // десериализация JSON для получения значения ключа urlws
                    NotificationItem item = JsonConvert.DeserializeObject<NotificationItem>(inputJSON);
                    // получение значения urlws и запись в переменную
                    urlWS = item.urlws;

                    _logger.LogInformation("Item callid: " + item.callid);

                    using var ws = new ClientWebSocket();
                    // подключение к WS по переданному в http-запросе URL
                    await ws.ConnectAsync(new Uri(urlWS), CancellationToken.None);
                    _logger.LogInformation("HTTP ConnectAsync called");
                    // отправка JSON в WebSocket
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes($"{inputJSON}")), WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
                    _logger.LogInformation("HTTP SendAsync called");
                    // закрытие соединения
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing after sending", CancellationToken.None);
                    _logger.LogInformation("HTTP CloseAsync called");
                }
                // принять запрос по пути /send
                if (context.Request.Path == "/send")
                {
                    _logger.LogInformation("/send called");
                    // если запрос является запросом веб сокета
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                        {
                            Clients.Add(webSocket);
                            await Send(context, webSocket);
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
            });
        }

        private async Task Send(HttpContext context, WebSocket webSocket)
        {
            _logger.LogInformation("Send websocket called");
            var buffer = new byte[1024 * 4];

            // получаем данные
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), System.Threading.CancellationToken.None);

            try
            {
                while (!result.CloseStatus.HasValue)
                {
                    // сообщение от клиента
                    string msg = Encoding.UTF8.GetString(new ArraySegment<byte>(buffer, 0, result.Count));
                    _logger.LogInformation("New msg: " + msg);

                    List<WebSocket> ErrorClients = new List<WebSocket>();

                    foreach (WebSocket client in Clients)
                    {
                        _logger.LogInformation("Current client.State: " + client.State);
                        // передаем сообщение от сервера всем клиентам
                        try
                        {
                            await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes($"{msg}")), result.MessageType, result.EndOfMessage, System.Threading.CancellationToken.None);
                            _logger.LogInformation("SendAsync called");
                        }
                        catch (Exception exep)
                        {
                            ErrorClients.Add(client);
                            _logger.LogError("Current error client.State: " + client.State);
                            _logger.LogError("Error websocket SendAsync: " + exep.ToString());
                        }
                    }
                    Clients = Clients.Except(ErrorClients).ToList<WebSocket>();

                    // ожидание другого сообщения от клиента

                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), System.Threading.CancellationToken.None);
                    _logger.LogInformation("ReceiveAsync called");
                }
                await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, System.Threading.CancellationToken.None);
                _logger.LogInformation("CloseAsync called with closeStatus: " + result.CloseStatus.Value);
                Clients.Remove(webSocket);
            }
            catch (Exception ex)
            {
                // если в блоке try упало исключение (в основном ловим WebSocketException), то выводим Лог и закрываем вебсокет со статусом "1000"
                _logger.LogError("Error websocket: " + ex.ToString());
                if (webSocket != null)
                {
                    _logger.LogInformation("Current webSocket.State: " + webSocket.State);
                    Clients.Remove(webSocket);
                }
            }
        }
    }
}
