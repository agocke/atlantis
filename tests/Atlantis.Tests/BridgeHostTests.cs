using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Atlantis.Bridge;

namespace Atlantis.Tests;

/// <summary>
/// Exercises both sides of the bridge wire protocol end to end: a fake transport
/// feeds request JSON to <see cref="BridgeHost"/> and captures the JSON it sends back,
/// asserting on the exact envelope shapes the generated atlantis.ts client expects.
/// </summary>
public class BridgeHostTests
{
    // In-memory stand-in for a webview: Post() simulates JS -> host messages,
    // and everything the host sends back lands in Sent for inspection. Messages
    // cross as UTF-8, matching the real transport.
    private sealed class FakeTransport : IBridgeTransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();

        public void Post(string message) => _inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(message));

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
            => _inbound.Reader.ReadAsync(cancellationToken).AsTask();

        public Task Send(ReadOnlyMemory<byte> message)
        {
            Sent.Writer.TryWrite(message.ToArray());
            return Task.CompletedTask;
        }
    }

    // Spin up a host with the pump running, returning the host, the transport, and a
    // disposable that stops the pump.
    private static (BridgeHost Host, FakeTransport Transport, IDisposable Pump) Start()
    {
        var transport = new FakeTransport();
        var host = new BridgeHost(transport);
        var cts = new CancellationTokenSource();
        var task = host.RunAsync(cts.Token);
        return (host, transport, new Pump(cts, task));
    }

    private sealed class Pump(CancellationTokenSource cts, Task task) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            try { task.Wait(TimeSpan.FromSeconds(5)); } catch { }
            cts.Dispose();
        }
    }

    private static async Task<JsonElement> NextMessage(FakeTransport transport)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] json = await transport.Sent.Reader.ReadAsync(cts.Token);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // Handlers now receive the args array as raw UTF-8 JSON. Decode it for assertions;
    // the bridge itself is serializer-agnostic, so a test picks its own decoder here.
    private static JsonElement A(ReadOnlyMemory<byte> args) => JsonDocument.Parse(args).RootElement;

    [Fact]
    public async Task Request_invokes_handler_and_returns_result()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", async (args, _) =>
            {
                int sum = A(args)[0].GetInt32() + A(args)[1].GetInt32();
                return (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes(sum.ToString());
            });

            transport.Post("""{"callId":1,"method":"Calc.Add","args":[2,3]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(1, reply.GetProperty("callId").GetInt32());
            Assert.Equal(5, reply.GetProperty("result").GetInt32());
            Assert.False(reply.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task Handler_receives_the_raw_args_array()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Echo.Concat", async (args, _) =>
            {
                string joined = A(args)[0].GetString() + A(args)[1].GetString();
                return (ReadOnlyMemory<byte>?)JsonSerializer.SerializeToUtf8Bytes(joined);
            });

            transport.Post("""{"callId":7,"method":"Echo.Concat","args":["foo","bar"]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(7, reply.GetProperty("callId").GetInt32());
            Assert.Equal("foobar", reply.GetProperty("result").GetString());
        }
    }

    [Fact]
    public async Task Void_handler_returns_null_result()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Logger.Log", async (_, _) => (ReadOnlyMemory<byte>?)null);

            transport.Post("""{"callId":2,"method":"Logger.Log","args":["hi"]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(2, reply.GetProperty("callId").GetInt32());
            Assert.Equal(JsonValueKind.Null, reply.GetProperty("result").ValueKind);
        }
    }

    [Fact]
    public async Task Unknown_handler_returns_error()
    {
        var (_, transport, pump) = Start();
        using (pump)
        {
            transport.Post("""{"callId":3,"method":"Nope.Missing","args":[]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(3, reply.GetProperty("callId").GetInt32());
            Assert.Contains("Nope.Missing", reply.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Throwing_handler_returns_error()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Boom.Go", (_, _) => throw new InvalidOperationException("kaboom"));

            transport.Post("""{"callId":4,"method":"Boom.Go","args":[]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(4, reply.GetProperty("callId").GetInt32());
            Assert.Equal("kaboom", reply.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Register_last_registration_wins()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", async (_, _) => (ReadOnlyMemory<byte>?)"1"u8.ToArray());
            host.Register("Calc.Add", async (_, _) => (ReadOnlyMemory<byte>?)"2"u8.ToArray());

            transport.Post("""{"callId":1,"method":"Calc.Add","args":[]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(2, reply.GetProperty("result").GetInt32());
        }
    }

    [Fact]
    public async Task Publish_sends_event_envelope()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            await host.Publish("ticks", """{"n":42}""");

            var evt = await NextMessage(transport);
            Assert.True(evt.GetProperty("event").GetBoolean());
            Assert.Equal("ticks", evt.GetProperty("channel").GetString());
            Assert.Equal(42, evt.GetProperty("payload").GetProperty("n").GetInt32());
        }
    }

    [Fact]
    public async Task Publish_rejects_a_missing_payload()
    {
        var (host, _, pump) = Start();
        using (pump)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => host.Publish("ticks", ""));
            await Assert.ThrowsAsync<ArgumentNullException>(() => host.Publish("ticks", null!));
        }
    }

    [Fact]
    public async Task Corrupt_request_still_rejects_the_originating_call_when_callId_is_recoverable()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", async (args, _) =>
                (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes((A(args)[0].GetInt32() + A(args)[1].GetInt32()).ToString()));

            // A valid leading callId but a malformed args tail: the structured parse
            // fails, yet the host recovers the callId so the exact caller is rejected
            // (correlated), not just told about a global error.
            transport.Post("""{"callId":11,"method":"Calc.Add","args":[1,2,}""");

            var reply = await NextMessage(transport);
            Assert.Equal(11, reply.GetProperty("callId").GetInt32());
            Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
        }
    }

    [Fact]
    public async Task Unroutable_message_without_callId_sends_a_global_error_frame()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", async (args, _) =>
                (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes((A(args)[0].GetInt32() + A(args)[1].GetInt32()).ToString()));

            // A frame with no callId has no caller to answer, so the host can't reject a
            // specific promise - but it still sends a callId-less error frame so the
            // client can surface it globally, and it must not take down the pump.
            transport.Post("""{"event":true,"channel":"x"}""");

            var err = await NextMessage(transport);
            Assert.False(err.TryGetProperty("callId", out _));
            Assert.Contains("no callId", err.GetProperty("error").GetString());

            transport.Post("""{"callId":9,"method":"Calc.Add","args":[1,1]}""");
            var reply = await NextMessage(transport);
            Assert.Equal(9, reply.GetProperty("callId").GetInt32());
            Assert.Equal(2, reply.GetProperty("result").GetInt32());
        }
    }

    [Fact]
    public async Task Malformed_message_sends_a_global_error_frame_without_killing_the_pump()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", async (args, _) =>
                (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes((A(args)[0].GetInt32() + A(args)[1].GetInt32()).ToString()));

            // Unparseable JSON can't be tied to a callId, but the host still reports the
            // parse error back to the client instead of swallowing it silently.
            transport.Post("this is not json");

            var err = await NextMessage(transport);
            Assert.False(err.TryGetProperty("callId", out _));
            Assert.Contains("could not parse", err.GetProperty("error").GetString());

            transport.Post("""{"callId":10,"method":"Calc.Add","args":[3,4]}""");
            var reply = await NextMessage(transport);
            Assert.Equal(10, reply.GetProperty("callId").GetInt32());
            Assert.Equal(7, reply.GetProperty("result").GetInt32());
        }
    }
}
