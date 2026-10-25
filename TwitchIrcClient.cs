using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScamWYF.TwitchControl
{
    internal sealed class TwitchChatMessage
    {
        public string UserId;
        public string Login;
        public string DisplayName;
        public string Text;
    }

    // Twitch IRC (read-only). Official authentication: user access token with chat:read.
    // All callbacks run on a background thread; caller must enqueue before accessing Unity.
    internal sealed class TwitchIrcClient : IDisposable
    {
        private CancellationTokenSource cancellation;
        private TcpClient socket;
        private StreamWriter writer;
        private readonly object writerLock = new object();
        private volatile string status = "Disconnected";
        public string Status { get { return status; } }
        public Action<TwitchChatMessage> Message;

        public void Connect(string channel, string login, string token)
        {
            Disconnect();
            string safeChannel = (channel ?? "").Trim().TrimStart('#').ToLowerInvariant();
            string safeLogin = (login ?? "").Trim().ToLowerInvariant();
            token = (token ?? "").Trim();
            if (token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)) token = token.Substring(6);
            if (!ValidName(safeChannel) || !ValidName(safeLogin) || token.Length < 10 || token.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0)
            {
                UpdateStatus("Invalid channel, login, or access token");
                return;
            }
            var cts = new CancellationTokenSource();
            cancellation = cts;
            Task.Run(() =>
            {
                try { Run(safeChannel, safeLogin, token, cts); }
                finally { cts.Dispose(); }
            });
        }

        private static bool ValidName(string text)
        {
            if (text.Length < 3 || text.Length > 25) return false;
            foreach (char c in text)
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }

        private void Run(string channel, string login, string token, CancellationTokenSource owner)
        {
            CancellationToken cancel = owner.Token;
            int retry = 0;
            while (!cancel.IsCancellationRequested)
            {
                TcpClient tcp = null;
                try
                {
                    StatusFor(owner, "Connecting to Twitch chat...");
                    tcp = new TcpClient();
                    tcp.Connect("irc.chat.twitch.tv", 6697);
                    tcp.ReceiveTimeout = 720000; // Twitch PING can be several minutes apart
                    tcp.SendTimeout = 10000;
                    lock (writerLock)
                    {
                        if (!ReferenceEquals(cancellation, owner) || cancel.IsCancellationRequested) return;
                        socket = tcp;
                    }
                    using (var tls = new SslStream(tcp.GetStream(), false))
                    {
                        tls.AuthenticateAsClient("irc.chat.twitch.tv", null, SslProtocols.Tls12, true);
                        using (var read = new StreamReader(tls, Encoding.UTF8, false, 4096, true))
                        using (var write = new StreamWriter(tls, new UTF8Encoding(false), 4096, true))
                        {
                            write.NewLine = "\r\n";
                            write.AutoFlush = true;
                            lock (writerLock)
                            {
                                if (!ReferenceEquals(cancellation, owner) || cancel.IsCancellationRequested) return;
                                writer = write;
                            }
                            Send("PASS oauth:" + token);
                            Send("NICK " + login);
                            Send("CAP REQ :twitch.tv/tags twitch.tv/commands");
                            Send("JOIN #" + channel);
                            string line;
                            while (!cancel.IsCancellationRequested && (line = read.ReadLine()) != null)
                            {
                                if (line.StartsWith("PING", StringComparison.Ordinal))
                                {
                                    Send("PONG :tmi.twitch.tv");
                                    continue;
                                }
                                if (line.IndexOf("Login authentication failed", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    StatusFor(owner, "Twitch rejected the OAuth token; reconnect with a chat:read token");
                                    return;
                                }
                                if (line.Contains(" 001 ") || line.Contains(" JOIN #" + channel))
                                {
                                    retry = 0;
                                    StatusFor(owner, "Connected to #" + channel);
                                }
                                TwitchChatMessage parsed = Parse(line);
                                if (parsed != null && Message != null) Message(parsed);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!cancel.IsCancellationRequested) StatusFor(owner, "Twitch connection interrupted: " + e.GetType().Name);
                }
                finally
                {
                    lock (writerLock)
                    {
                        // Older connections must never clear a newer connection's I/O.
                        if (ReferenceEquals(socket, tcp)) { writer = null; socket = null; }
                    }
                    try { tcp?.Close(); } catch { }
                }
                if (cancel.IsCancellationRequested) break;
                retry++;
                int waitMs = Math.Min(30000, 1000 * (1 << Math.Min(retry, 5)));
                if (cancel.WaitHandle.WaitOne(waitMs)) break;
            }
            if (cancel.IsCancellationRequested) StatusFor(owner, "Disconnected");
        }

        private void Send(string command)
        {
            lock (writerLock)
            {
                try { writer?.WriteLine(command); }
                catch { }
            }
        }

        private void UpdateStatus(string value)
        {
            status = value;
        }

        private void StatusFor(CancellationTokenSource owner, string value)
        {
            if (ReferenceEquals(cancellation, owner)) status = value;
        }

        internal static TwitchChatMessage Parse(string line)
        {
            // @badge-info=...;display-name=Viewer;user-id=123 :viewer!viewer@... PRIVMSG #channel :message
            if (string.IsNullOrWhiteSpace(line)) return null;
            int command = line.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
            if (command < 0) return null;
            int body = line.IndexOf(" :", command + 1, StringComparison.Ordinal);
            if (body < 0) return null;
            int bang = line.IndexOf('!');
            int prefix = line.IndexOf(' ');
            string login = bang > prefix + 2 ? line.Substring(prefix + 2, bang - prefix - 2) : "";
            if (string.IsNullOrWhiteSpace(login)) return null;
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (line[0] == '@')
            {
                int endTags = line.IndexOf(' ');
                if (endTags > 1)
                {
                    string[] pairs = line.Substring(1, endTags - 1).Split(';');
                    foreach (string pair in pairs)
                    {
                        int equals = pair.IndexOf('=');
                        if (equals > 0) tags[pair.Substring(0, equals)] = pair.Substring(equals + 1);
                    }
                }
            }
            string userId;
            if (!tags.TryGetValue("user-id", out userId) || string.IsNullOrWhiteSpace(userId)) return null;
            string name;
            if (!tags.TryGetValue("display-name", out name) || string.IsNullOrWhiteSpace(name)) name = login;
            return new TwitchChatMessage
            {
                UserId = userId,
                Login = login.ToLowerInvariant(),
                DisplayName = name,
                Text = line.Substring(body + 2)
            };
        }

        public void Disconnect()
        {
            var previous = cancellation;
            cancellation = null;
            if (previous != null) { try { previous.Cancel(); } catch { } }
            lock (writerLock) { try { socket?.Close(); } catch { } }
            UpdateStatus("Disconnected");
        }

        public void Dispose() { Disconnect(); }
    }
}
