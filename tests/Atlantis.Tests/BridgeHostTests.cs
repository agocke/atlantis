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
    // and everything the host sends back lands in Sent for inspection.
    private sealed class FakeTransport : IBridgeTransport
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();

        public void Post(string message) => _inbound.Writer.TryWrite(message);

        public Task<string> ReceiveAsync(CancellationToken cancellationToken = default)
            => _inbound.Reader.ReadAsync(cancellationToken).AsTask();

        public Task Send(string message)
        {
            Sent.Writer.TryWrite(message);
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
        string json = await transport.Sent.Reader.ReadAsync(cts.Token);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Request_invokes_handler_and_returns_result()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", args =>
            {
                int sum = args[0].GetInt32() + args[1].GetInt32();
                return Task.FromResult<string?>(sum.ToString());
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
            host.Register("Echo.Concat", args =>
            {
                string joined = args[0].GetString() + args[1].GetString();
                return Task.FromResult<string?>(JsonSerializer.Serialize(joined));
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
            host.Register("Logger.Log", _ => Task.FromResult<string?>(null));

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
            host.Register("Boom.Go", _ => throw new InvalidOperationException("kaboom"));

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
            host.Register("Calc.Add", _ => Task.FromResult<string?>("1"));
            host.Register("Calc.Add", _ => Task.FromResult<string?>("2"));

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
    public async Task Message_without_callId_is_dropped_without_killing_the_pump()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", args =>
                Task.FromResult<string?>((args[0].GetInt32() + args[1].GetInt32()).ToString()));

            // A frame with no callId has no caller to answer, so it's dropped - but it
            // must not take down the pump or the other calls in flight.
            transport.Post("""{"event":true,"channel":"x"}""");
            transport.Post("""{"callId":9,"method":"Calc.Add","args":[1,1]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(9, reply.GetProperty("callId").GetInt32());
            Assert.Equal(2, reply.GetProperty("result").GetInt32());
        }
    }

    [Fact]
    public async Task Malformed_message_is_dropped_without_killing_the_pump()
    {
        var (host, transport, pump) = Start();
        using (pump)
        {
            host.Register("Calc.Add", args =>
                Task.FromResult<string?>((args[0].GetInt32() + args[1].GetInt32()).ToString()));

            transport.Post("this is not json");
            transport.Post("""{"callId":10,"method":"Calc.Add","args":[3,4]}""");

            var reply = await NextMessage(transport);
            Assert.Equal(10, reply.GetProperty("callId").GetInt32());
            Assert.Equal(7, reply.GetProperty("result").GetInt32());
        }
    }
}
