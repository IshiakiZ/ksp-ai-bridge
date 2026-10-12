using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AIBridge
{
    /// <summary>
    /// Tiny loopback-only HTTP server. One endpoint matters: POST /rpc with a JSON body
    /// {"method": "...", "params": {...}, "timeout": seconds}. Requests are handed to the
    /// Unity main thread through <see cref="BridgeHost.Submit"/> and answered when it completes them.
    /// </summary>
    public sealed class HttpServer
    {
        const int MaxBodyBytes = 8 * 1024 * 1024;

        readonly BridgeHost host;
        readonly string token;
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;

        public int Port { get; private set; }

        public HttpServer(BridgeHost host, string token)
        {
            this.host = host;
            this.token = token;
        }

        public void Start(int firstPort, int attempts)
        {
            Exception last = null;
            for (int n = 0; n < attempts; n++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, firstPort + n);
                    l.Start(16);
                    listener = l;
                    Port = firstPort + n;
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (listener == null) throw new IOException("AIBridge could not open a local port starting at " + firstPort, last);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AIBridge-accept" };
            acceptThread.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            listener = null;
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(50); continue; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    NetworkStream stream = client.GetStream();
                    if (!ReadRequest(stream, out string verb, out string path, out Dictionary<string, string> headers, out byte[] body, out string problem))
                    {
                        if (problem != null) Respond(stream, 400, Error(problem));
                        return;
                    }
                    Handle(stream, verb, path, headers, body);
                }
            }
            catch (Exception ex)
            {
                if (running) BridgeHost.LogWarn("request failed: " + ex.Message);
            }
        }

        void Handle(NetworkStream stream, string verb, string path, Dictionary<string, string> headers, byte[] body)
        {
            // A web page in the player's browser must never be able to drive the game: browsers always attach
            // Origin to cross-site POSTs, and a DNS-rebinding page shows up with a foreign Host header.
            if (headers.ContainsKey("origin")) { Respond(stream, 403, Error("Browser requests are not accepted")); return; }
            headers.TryGetValue("host", out string hostHeader);
            if (!IsLocalHost(hostHeader)) { Respond(stream, 403, Error("Invalid Host header")); return; }

            if (verb == "GET" && (path == "/" || path == "/health"))
            {
                Respond(stream, 200, Json.Write(new JObj().Add("ok", true).Add("name", "AIBridge").Add("version", BridgeHost.Version)));
                return;
            }
            if (verb != "POST" || path != "/rpc") { Respond(stream, 404, Error("Unknown endpoint. Use POST /rpc")); return; }

            headers.TryGetValue("x-aibridge-token", out string given);
            if (!TokenMatches(given)) { Respond(stream, 401, Error("Missing or wrong X-AIBridge-Token header")); return; }

            Args request;
            try { request = new Args(Json.Parse(Encoding.UTF8.GetString(body))); }
            catch (Exception ex) { Respond(stream, 400, Error("Bad JSON: " + ex.Message)); return; }

            string method = request.Str("method");
            if (string.IsNullOrEmpty(method)) { Respond(stream, 400, Error("Request needs a 'method'")); return; }
            double timeout = Math.Max(1, Math.Min(900, request.Num("timeout", 30)));

            var call = new Call { Method = method, Args = request.Obj("params") ?? new Args(null) };
            host.Submit(call);
            if (!call.Done.WaitOne(TimeSpan.FromSeconds(timeout)))
            {
                call.Abandoned = true;
                Respond(stream, 200, Error("Still working after " + timeout + " s. KSP is probably loading a scene, or this operation takes longer than one call can wait. It carries on inside the game: check status in a few seconds before repeating the command."));
                return;
            }
            string payload;
            try
            {
                payload = call.Error != null
                    ? Error(call.Error)
                    : Json.Write(new JObj().Add("ok", true).Add("result", call.Result));
            }
            catch (Exception ex) { payload = Error("Could not serialise result: " + ex.Message); }
            Respond(stream, 200, payload);
        }

        static string Error(string message) => Json.Write(new JObj().Add("ok", false).Add("error", message));

        bool TokenMatches(string given)
        {
            if (given == null || given.Length != token.Length) return false;
            int diff = 0;
            for (int n = 0; n < token.Length; n++) diff |= given[n] ^ token[n];
            return diff == 0;
        }

        bool IsLocalHost(string hostHeader)
        {
            if (string.IsNullOrEmpty(hostHeader)) return false;
            string h = hostHeader.Trim().ToLowerInvariant();
            return h == "127.0.0.1:" + Port || h == "localhost:" + Port || h == "127.0.0.1" || h == "localhost";
        }

        static bool ReadRequest(NetworkStream stream, out string verb, out string path, out Dictionary<string, string> headers, out byte[] body, out string problem)
        {
            verb = path = problem = null;
            headers = new Dictionary<string, string>();
            body = new byte[0];

            var head = new MemoryStream();
            var buffer = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return false;
                head.Write(buffer, 0, n);
                if (head.Length > 64 * 1024) { problem = "Request headers too large"; return false; }
                headerEnd = FindHeaderEnd(head.GetBuffer(), (int)head.Length);
            }
            byte[] raw = head.ToArray();
            string[] lines = Encoding.ASCII.GetString(raw, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) { problem = "Malformed request line"; return false; }
            verb = first[0].ToUpperInvariant();
            path = first[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            for (int n = 1; n < lines.Length; n++)
            {
                int colon = lines[n].IndexOf(':');
                if (colon > 0) headers[lines[n].Substring(0, colon).Trim().ToLowerInvariant()] = lines[n].Substring(colon + 1).Trim();
            }

            int length = 0;
            if (headers.TryGetValue("content-length", out string cl) && !int.TryParse(cl, out length)) { problem = "Bad Content-Length"; return false; }
            if (length < 0 || length > MaxBodyBytes) { problem = "Request body too large"; return false; }
            if (headers.TryGetValue("expect", out string expect) && expect.ToLowerInvariant().Contains("100-continue"))
            {
                byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(cont, 0, cont.Length);
            }

            body = new byte[length];
            int have = Math.Min(length, raw.Length - (headerEnd + 4));
            if (have > 0) Buffer.BlockCopy(raw, headerEnd + 4, body, 0, have);
            while (have < length)
            {
                int n = stream.Read(body, have, length - have);
                if (n <= 0) { problem = "Connection closed before the body arrived"; return false; }
                have += n;
            }
            return true;
        }

        static int FindHeaderEnd(byte[] data, int length)
        {
            for (int n = 0; n + 3 < length; n++)
                if (data[n] == 13 && data[n + 1] == 10 && data[n + 2] == 13 && data[n + 3] == 10) return n;
            return -1;
        }

        static void Respond(NetworkStream stream, int status, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : "Not Found";
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
