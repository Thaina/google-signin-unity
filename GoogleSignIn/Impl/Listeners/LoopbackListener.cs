using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Google.Impl {
    internal class LoopbackListener : IListener {
        private HttpListenerResponse response;
            
        private readonly HttpListener listener;
        
        public string RedirectUri { get; }

        public LoopbackListener() {
            int maxRetries = 10;
            while (maxRetries-- > 0) {
                try {
                    int port;
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
                        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                        port = ((IPEndPoint)socket.LocalEndPoint).Port;
                    }

                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://{IPAddress.Loopback}:{port}/");
                    listener.Start();

                    RedirectUri = listener.Prefixes.First();

                    break;
                } catch (Exception e) {
                    Debug.LogWarning($"Failed to get port, {maxRetries} retries remaining... Detail: {e.Message}");
                }
            }

            if (RedirectUri == null) throw new Exception($"Fail to initialize {nameof(LoopbackListener)} because failed to get port!");
        }

        public async Task<string> ListenAsync() {
            var context = await listener.GetContextAsync();
            response = context.Response;
            return context.Request.Url.Query;
        }

        public void OnGetCodeFailed() {
            SendHtmlResponse(false);
        }

        public void OnGetCodeSuccess() {
            SendHtmlResponse(true);
            
            DesktopExt.TryBringGameToFront();
        }

        public void Dispose() {
            if (listener == null) return;
            
            listener.Stop();
            listener.Close();
        }

        private void SendHtmlResponse(bool isSuccess) {
            if (response == null) return;
            
            var titleColor   = isSuccess ? "#4CAF50" : "#F44336";
            var titleContent = isSuccess ? "Authentication Successful!" : "Authentication Failed!";
            var detail       = isSuccess ? "You can now close this window and return to the game." : "Cannot get code.";
            var statusCode   = isSuccess ? 200 : 404;

            byte[] buffer = Encoding.UTF8.GetBytes($@"
                <html>
                <head>
                    <style>
                        :root {{
                            color-scheme: light dark;
                        }}
                        body {{
                            background-color: Canvas;
                            color: CanvasText;
                            font-family: system-ui, -apple-system, sans-serif;
                            text-align: center;
                            padding-top: 50px;
                            transition: background-color 0.3s, color 0.3s;
                        }}
                        h1 {{
                            color: {titleColor};
                        }}
                    </style>
                </head>
                <body>
                    <h1>{titleContent}</h1>
                    <p>{detail}</p>
                </body>
                </html>");

            try {
                response.StatusCode      = statusCode;
                response.ContentType     = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            } catch {
                // Browser might close early
            }
        }
    }
}