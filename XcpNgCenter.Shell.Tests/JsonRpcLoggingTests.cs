using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using XenAPI;
using Task = System.Threading.Tasks.Task;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[CollectionDefinition("JSON-RPC logging", DisableParallelization = true)]
public sealed class JsonRpcLoggingCollection;

[Collection("JSON-RPC logging")]
public sealed class JsonRpcLoggingTests
{
    [Fact]
    public async Task RequestDiagnostics_OmitParametersEvenWhenDebugLoggingIsEnabled()
    {
        const string sessionToken = "synthetic-session-credential";
        const string taskToken = "synthetic-task-reference";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var length = 0;
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(header.Split(':', 2)[1]);
            var request = new char[length];
            var read = 0;
            while (read < length)
            {
                var count = await reader.ReadAsync(request.AsMemory(read), timeout.Token);
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
            const string body = "{\"id\":1,\"result\":\"success\",\"error\":null}";
            var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(reply, timeout.Token);
            return new string(request);
        }, timeout.Token);

        var url = $"http://127.0.0.1:{port}/";
        var rpc = new JsonRpcClient(url) { Timeout = 5000 };
        var session = new Session(url);
        var logRequest = typeof(Session).GetMethod("LogJsonRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        rpc.RequestEvent += (Action<string>)logRequest.CreateDelegate(typeof(Action<string>), session);
        var events = new List<string>();
        rpc.RequestEvent += events.Add;
        var logger = (Logger)LogManager.GetLogger(typeof(Session)).Logger;
        var previousLevel = logger.Level;
        var appender = new MemoryAppender();
        var repository = LogManager.GetRepository(typeof(Session).Assembly);
        var wasConfigured = repository.Configured;
        repository.Configured = true;
        logger.Level = Level.Debug;
        logger.AddAppender(appender);
        try
        {
            await Task.Run(() => rpc.task_get_status(sessionToken, taskToken), timeout.Token);
            var wire = await responseTask;
            Assert.Contains(sessionToken, wire);
            Assert.Contains(taskToken, wire);
            Assert.Equal(new[] { "task.get_status" }, events);
            var messages = appender.GetEvents().Select(e => e.RenderedMessage ?? string.Empty).ToArray();
            Assert.Contains(messages, m => m.Contains("task.get_status"));
            Assert.DoesNotContain(messages, m => m.Contains(sessionToken) || m.Contains(taskToken));
        }
        finally
        {
            logger.RemoveAppender(appender);
            logger.Level = previousLevel;
            repository.Configured = wasConfigured;
            appender.Close();
        }
    }
}
