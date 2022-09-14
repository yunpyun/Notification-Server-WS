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

namespace NotificationServerWS
{
    public class Startup
    {
        // Список всех клиентов
        private static readonly List<WebSocket> Clients = new List<WebSocket>();

        // Блокировка для обеспечения потокабезопасности
        private static readonly ReaderWriterLockSlim Locker = new ReaderWriterLockSlim();

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            var wsOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(120) };
            app.UseWebSockets(wsOptions);
            app.Use(async (context, next) =>
            {
                // принять запрос только по пути /send
                if (context.Request.Path == "/send")
                {
                    // если запрос является запросом веб сокета
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                        {
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
            // добавляем его в список клиентов
            Locker.EnterWriteLock();

            try
            {
                Clients.Add(webSocket);
            }
            finally
            {
                Locker.ExitWriteLock();
            }

            while (true)
            {
                var buffer = new ArraySegment<byte>(new byte[1024]);

                // получаем данные
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, System.Threading.CancellationToken.None);

                // передаём сообщение всем клиентам
                for (int i = 0; i < Clients.Count; i++)
                {
                    WebSocket client = Clients[i];

                    try
                    {
                        if (client.State == WebSocketState.Open)
                        {
                            await client.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Locker.EnterWriteLock();
                        try
                        {
                            Clients.Remove(client);
                            i--;
                        }
                        finally
                        {
                            Locker.ExitWriteLock();
                        }
                    }
                }
            }
        }
    }
}
