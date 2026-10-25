using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScamWYF.TwitchControl
{
    // Loopback-only, no admin HTTP URL reservation, no external hosting.
    internal sealed class OverlayServer : IDisposable
    {
        private TcpListener listener;
        private int activeClients;
        private CancellationTokenSource stop;
        private volatile string json = "{\"active\":false}";
        public string Status { get; private set; } = "Stopped";
        public int Port { get; private set; }
        public string Url { get { return "http://127.0.0.1:" + Port + "/overlay"; } }
        public void UpdateState(string value) { json = value ?? "{\"active\":false}"; }
        public void Start(int port)
        {
            Stop();
            try
            {
                Port = port;
                var newListener = new TcpListener(IPAddress.Loopback, port);
                newListener.Start(8);
                listener = newListener;
                stop = new CancellationTokenSource();
                var token = stop.Token;
                // Bind the accept loop to this server instance, not the mutable
                // listener field (which changes when the user selects a new port).
                Task.Run(() => Run(newListener, token));
                Status = "Listening on " + Url;
            }
            catch (Exception e)
            {
                Status = "Failed to listen: " + e.Message;
                StopSocket();
            }
        }
        private void Run(TcpListener server, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = server.AcceptTcpClient();
                    // OBS makes regular requests, but we must not create unlimited
                    // thread-pool work if another local application floods this port.
                    if (Interlocked.Increment(ref activeClients) > 8)
                    {
                        Interlocked.Decrement(ref activeClients);
                        client.Close();
                        continue;
                    }
                    Task.Run(() =>
                    {
                        try { Serve(client); }
                        finally { Interlocked.Decrement(ref activeClients); }
                    });
                }
                catch (SocketException) { if (cancellation.IsCancellationRequested) break; }
                catch (ObjectDisposedException) { break; }
                catch { if (cancellation.IsCancellationRequested) break; }
            }
        }
        private void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    // Bound the request line, ignore request bodies and never expose files.
                    string request;
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        request = reader.ReadLine() ?? "";
                        string header;
                        int count = 0;
                        while ((header = reader.ReadLine()) != null && header.Length != 0 && count++ < 50) { }
                    }
                    string[] parts = request.Split(' ');
                    string path = parts.Length >= 2 ? parts[1] : "";
                    bool valid = request.StartsWith("GET ", StringComparison.Ordinal);
                    string content;
                    string mime;
                    string status;
                    if (valid && (path == "/overlay" || path == "/"))
                    {
                        content = Page;
                        mime = "text/html; charset=utf-8";
                        status = "200 OK";
                    }
                    else if (valid && path == "/state")
                    {
                        content = json;
                        mime = "application/json; charset=utf-8";
                        status = "200 OK";
                    }
                    else
                    {
                        content = "Not found";
                        mime = "text/plain; charset=utf-8";
                        status = "404 Not Found";
                    }
                    byte[] body = Encoding.UTF8.GetBytes(content);
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" +
                        "Content-Type: " + mime + "\r\nCache-Control: no-store, max-age=0\r\n" +
                        "X-Content-Type-Options: nosniff\r\n" +
                        "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'\r\n" +
                        "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, body.Length);
                }
            }
            catch { try { client.Close(); } catch { } }
        }
        public void Stop()
        {
            try { stop?.Cancel(); } catch { }
            StopSocket();
            Status = "Stopped";
        }
        private void StopSocket()
        {
            try { listener?.Stop(); } catch { }
            listener = null;
        }
        public void Dispose() { Stop(); }

        internal static string EscapeJson(string s)
        {
            if (s == null) return "\"\"";
            var b = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': b.Append("\\\\"); break;
                    case '"': b.Append("\\\""); break;
                    case '\r': b.Append("\\r"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (ch < 32 || ch == '\u2028' || ch == '\u2029') b.Append("\\u" + ((int)ch).ToString("x4"));
                        else b.Append(ch);
                        break;
                }
            }
            b.Append('"');
            return b.ToString();
        }
        // Responsive OBS browser source: OBS handles positioning, this page handles
        // narrow/wide sources. All untrusted strings are inserted via textContent.
        private const string Page = @"<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
:root{font-family:Segoe UI,Arial,sans-serif;color:#fff;color-scheme:dark}
*{box-sizing:border-box}html,body{margin:0;min-height:100%;background:transparent}
body{display:flex;justify-content:center;align-items:flex-start;padding:12px}
#panel{width:100%;max-width:650px;min-width:0;padding:clamp(14px,3vw,24px);border-radius:16px;
background:rgba(17,16,30,.97);border:2px solid #bda8ff;
box-shadow:0 8px 30px rgba(0,0,0,.85),inset 0 1px rgba(255,255,255,.12);
overflow-wrap:anywhere;line-height:1.4}
#panel.off{display:none}
#heading{font-size:clamp(12px,2vw,15px);letter-spacing:1.7px;font-weight:800;color:#d1bbff}
#viewer{font-size:clamp(22px,4vw,36px);font-weight:800;margin:4px 0;color:#fff;line-height:1.15}
#phase{color:#f1e7ff;font-size:clamp(13px,2vw,16px);margin:6px 0 12px}
#codes{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,215px),1fr));gap:10px;margin:12px 0}
.code{background:#292538;border:1px solid #9d86cf;border-radius:10px;padding:12px;min-width:0;box-shadow:0 2px 9px #0006}
.tag{font-size:13px;font-weight:750;color:#dbc8ff;display:block;margin-bottom:4px}
.value{font-family:Consolas,'Courier New',monospace;display:block;font-weight:800;font-size:clamp(18px,3vw,26px);letter-spacing:.7px;color:#fff;overflow-wrap:anywhere}
.state{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:1px;color:#c9eecf;display:block;margin-top:7px}
.code.claimed .value{text-decoration:line-through;opacity:.7}
.code.claimed .state{color:#d2c9d9}
#spoken{background:#242232;border-radius:10px;padding:12px;margin-top:12px;font-size:clamp(13px,2vw,16px);color:#fff;border:1px solid #706385;white-space:pre-wrap}
#spoken:empty{display:none}
#footer{font-size:clamp(12px,2vw,14px);color:#f4eaff;margin-top:12px;border-top:1px solid #675d81;padding-top:10px}
@media(max-width:360px){body{padding:6px}#panel{border-radius:10px;padding:12px}#codes{grid-template-columns:1fr}}
</style></head><body>
<div id='panel' class='off'>
<div id='heading'>LIVE TWITCH VICTIM</div><div id='viewer'></div><div id='phase'></div>
<div id='codes'></div><div id='spoken'></div>
<div id='footer'>Selected viewer: type normally in Twitch chat to speak as the victim. Read your fictional codes when asked.</div>
</div>
<script>
const p=document.getElementById('panel'),v=document.getElementById('viewer'),phase=document.getElementById('phase'),codes=document.getElementById('codes'),spoken=document.getElementById('spoken');
let busy=false,missed=0;
async function refresh(){if(busy)return;busy=true;try{
 const ctrl=new AbortController();const timeout=setTimeout(()=>ctrl.abort(),2000);
 let r;
 try{r=await fetch('/state',{cache:'no-store',signal:ctrl.signal})}finally{clearTimeout(timeout)}
 if(!r.ok)throw Error('status');
 const s=await r.json();missed=0;p.classList.toggle('off',!s.active);if(!s.active)return;
 v.textContent=s.viewer||'Viewer';phase.textContent=s.phase||'';
 spoken.textContent=s.lastSpeech?'STREAMER SAID: '+s.lastSpeech:'';
 codes.replaceChildren();
 for(const entry of (s.codes||[])){
  const c=document.createElement('div');c.className='code';if(entry.claimed)c.classList.add('claimed');
  const tag=document.createElement('span');tag.className='tag';tag.textContent=entry.label||'Fictional code';
  const val=document.createElement('span');val.className='value';val.textContent=entry.value||'';
  const status=document.createElement('span');status.className='state';
  status.textContent=entry.claimed?'Redeemed':entry.revealed?'Spoken in call':'Not yet spoken';
  c.append(tag,val,status);codes.append(c);
 }
}catch(e){if(++missed>=3)p.classList.add('off')}finally{busy=false}}
refresh();setInterval(refresh,500);
</script></body></html>";
    }
}
