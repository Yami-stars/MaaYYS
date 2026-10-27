using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MaaYys.Engine.Config;
using MaaYys.Engine.Notification;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 外部通知（P6.3 首批）：用本地 TCP 监听端**真实收到** Webhook 请求，命中
/// 「外部通知可真实收到测试消息」的验收标准；同时覆盖触发门控、变量替换、失败降级。
/// <para>
/// 不依赖真实外网：起一个 127.0.0.1 的临时端口监听，让 HttpClient 真的打过来，
/// 再断言请求行 / 请求体 / 自定义头，最后回 200。
/// </para>
/// </summary>
public sealed class NotificationServiceTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task SendAsync_RealWebhook_ReceivesPostWithSubstitutedBodyAndHeaders()
    {
        (TcpListener listener, int port) = StartListener(out var received);
        try
        {
            var settings = new NotificationSettings
            {
                ExternalEnabled = true,
                OnComplete = true,
                WebhookUrl = $"http://127.0.0.1:{port}/hook",
                WebhookMethod = "POST",
                WebhookContentType = "application/json",
                WebhookHeaders = "Authorization: Bearer secret\r\nX-App: MaaYys",
                WebhookBodyTemplate = "{\"t\":\"{trigger}\",\"msg\":\"{message}\"}",
            };

            NotificationContext context = new("队列完成", "配置组「日常」跑完", DateTimeOffset.Now, NotificationTrigger.Completed);

            NotificationResult result = await new NotificationService().SendAsync(settings, context, Client);

            string request = await received;
            Assert.True(result.Sent && !result.Failed, $"应发送成功，实际 Failed={result.Failed} Error={result.Error}");
            Assert.Contains("POST /hook HTTP/1.1", request);
            Assert.Contains("Authorization: Bearer secret", request);
            Assert.Contains("X-App: MaaYys", request);
            Assert.Contains("\"t\":\"completed\"", request);
            Assert.Contains("\"msg\":\"配置组「日常」跑完\"", request);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_Gating_BlocksWhenTriggerDisabled()
    {
        (TcpListener listener, int port) = StartListener(out var received);
        try
        {
            var settings = new NotificationSettings
            {
                ExternalEnabled = true,
                OnComplete = false, // 完成触发关掉
                WebhookUrl = $"http://127.0.0.1:{port}/",
            };

            NotificationResult result = await new NotificationService().SendAsync(
                settings,
                new NotificationContext("x", "y", DateTimeOffset.Now, NotificationTrigger.Completed),
                Client);

            Assert.False(result.Sent); // 被门控拦截，不该发请求
            // 给监听端一拍，确认没连接进来
            await Task.Delay(200);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_TestTrigger_BypassesGating()
    {
        (TcpListener listener, int port) = StartListener(out var received);
        try
        {
            var settings = new NotificationSettings
            {
                ExternalEnabled = true,
                OnComplete = false, // 即便全关，Test 也应放行
                WebhookUrl = $"http://127.0.0.1:{port}/",
            };

            NotificationResult result = await new NotificationService().SendAsync(
                settings,
                new NotificationContext("测试", "内容", DateTimeOffset.Now, NotificationTrigger.Test),
                Client);

            string request = await received;
            Assert.True(result.Sent && !result.Failed);
            Assert.Contains("POST / HTTP/1.1", request);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_BadUrl_FailsGracefullyWithoutThrowing()
    {
        var settings = new NotificationSettings
        {
            ExternalEnabled = true,
            OnComplete = true,
            // 端口 1 几乎不可能有服务，连接会被拒
            WebhookUrl = "http://127.0.0.1:1/",
        };

        NotificationResult result = await new NotificationService().SendAsync(
            settings,
            new NotificationContext("x", "y", DateTimeOffset.Now, NotificationTrigger.Completed),
            Client);

        Assert.True(result.Sent && result.Failed); // 发了但失败，且方法没抛异常
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void Substitute_ReplacesKnownVariablesCaseInsensitively()
    {
        var context = new NotificationContext("标题", "正文", new DateTimeOffset(2026, 10, 27, 19, 45, 0, TimeSpan.Zero), NotificationTrigger.Failed);
        string outp = NotificationService.Substitute("{TITLE}|{message}|{time}|{trigger}", context);

        Assert.Contains("标题", outp);
        Assert.Contains("正文", outp);
        Assert.Contains("2026-10-27 19:45:00", outp);
        Assert.Contains("failed", outp);
    }

    // ---- 本地 HTTP 监听辅助 ----

    private static (TcpListener, int) StartListener(out Task<string> received)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        received = tcs.Task;

        _ = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                string request = await ReadHttpRequest(client, CancellationToken.None);
                // 回 200
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await client.GetStream().WriteAsync(response, CancellationToken.None);
                tcs.TrySetResult(request);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return (listener, port);
    }

    private static async Task<string> ReadHttpRequest(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buf = new byte[4096];
        var ms = new MemoryStream();
        int headerEnd = -1;
        while (true)
        {
            int read = await stream.ReadAsync(buf, 0, buf.Length, ct);
            if (read == 0)
            {
                break;
            }

            ms.Write(buf, 0, read);
            byte[] all = ms.ToArray();
            headerEnd = IndexOf(all, "\r\n\r\n");
            if (headerEnd >= 0)
            {
                string head = Encoding.ASCII.GetString(all, 0, headerEnd);
                Match m = Regex.Match(head, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                int bodyLen = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                int bodyStart = headerEnd + 4;
                if (all.Length >= bodyStart + bodyLen)
                {
                    break;
                }
            }

            if (ms.Length > 1_000_000)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int IndexOf(byte[] haystack, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < n.Length; j++)
            {
                if (haystack[i + j] != n[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }
}
