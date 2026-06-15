using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Atlantis.Bridge;

namespace Atlantis.Tests;

/// <summary>
/// Covers the public <see cref="Dialog"/> API and the built-in
/// <c>Atlantis.Dialog.*</c> bridge handlers, driving them through a fake dialog
/// provider so no real native window is needed.
/// </summary>
public class DialogTests : IDisposable
{
    private sealed class FakeProvider : IFileDialogProvider
    {
        public OpenDialogOptions? Last { get; private set; }
        public string[] Result { get; set; } = [];

        public string[] ShowOpen(OpenDialogOptions options)
        {
            Last = options;
            return Result;
        }
    }

    private readonly FakeProvider _provider = new();

    public DialogTests() => Dialog.Provider = _provider;

    public void Dispose() => Dialog.Provider = null;

    [Fact]
    public void OpenFile_returns_first_path_and_requests_a_single_file()
    {
        _provider.Result = ["/tmp/a.txt", "/tmp/b.txt"];

        Assert.Equal("/tmp/a.txt", Dialog.OpenFile("Pick a file"));
        Assert.NotNull(_provider.Last);
        Assert.Equal("Pick a file", _provider.Last!.Title);
        Assert.False(_provider.Last.AllowMultiple);
        Assert.False(_provider.Last.Directories);
    }

    [Fact]
    public void OpenFile_returns_null_when_cancelled()
    {
        _provider.Result = [];
        Assert.Null(Dialog.OpenFile());
    }

    [Fact]
    public void OpenFiles_requests_multiple_and_returns_all()
    {
        _provider.Result = ["/x", "/y"];

        var result = Dialog.OpenFiles();

        Assert.Equal(["/x", "/y"], result);
        Assert.True(_provider.Last!.AllowMultiple);
        Assert.False(_provider.Last.Directories);
    }

    [Fact]
    public void OpenFolder_requests_directories_and_returns_first()
    {
        _provider.Result = ["/home/user"];

        Assert.Equal("/home/user", Dialog.OpenFolder());
        Assert.True(_provider.Last!.Directories);
        Assert.False(_provider.Last.AllowMultiple);
    }

    [Fact]
    public void ShowOpen_throws_when_no_window_is_active()
    {
        Dialog.Provider = null;
        Assert.Throws<InvalidOperationException>(() => Dialog.ShowOpen(new OpenDialogOptions()));
    }

    // ---- Built-in bridge handlers ----

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

    private static async Task<JsonElement> NextMessage(FakeTransport transport)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] json = await transport.Sent.Reader.ReadAsync(cts.Token);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Bridge_open_file_passes_title_and_returns_path()
    {
        _provider.Result = ["/tmp/chosen.txt"];

        var transport = new FakeTransport();
        var host = new BridgeHost(transport);
        Dialog.RegisterBridge(host);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(cts.Token);

        transport.Post("""{"callId":1,"method":"Atlantis.Dialog.OpenFile","args":["Choose"]}""");

        var reply = await NextMessage(transport);
        Assert.Equal(1, reply.GetProperty("callId").GetInt32());
        Assert.Equal("/tmp/chosen.txt", reply.GetProperty("result").GetString());
        Assert.Equal("Choose", _provider.Last!.Title);
        Assert.False(_provider.Last.Directories);
        cts.Cancel();
    }

    [Fact]
    public async Task Bridge_open_file_returns_null_when_cancelled()
    {
        _provider.Result = [];

        var transport = new FakeTransport();
        var host = new BridgeHost(transport);
        Dialog.RegisterBridge(host);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(cts.Token);

        transport.Post("""{"callId":2,"method":"Atlantis.Dialog.OpenFile","args":[]}""");

        var reply = await NextMessage(transport);
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("result").ValueKind);
        cts.Cancel();
    }

    [Fact]
    public async Task Bridge_open_files_returns_array()
    {
        _provider.Result = ["/a", "/b"];

        var transport = new FakeTransport();
        var host = new BridgeHost(transport);
        Dialog.RegisterBridge(host);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(cts.Token);

        transport.Post("""{"callId":3,"method":"Atlantis.Dialog.OpenFiles","args":[]}""");

        var reply = await NextMessage(transport);
        var paths = reply.GetProperty("result").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["/a", "/b"], paths);
        Assert.True(_provider.Last!.AllowMultiple);
        cts.Cancel();
    }

    [Fact]
    public async Task Bridge_open_folder_requests_directories()
    {
        _provider.Result = ["/some/dir"];

        var transport = new FakeTransport();
        var host = new BridgeHost(transport);
        Dialog.RegisterBridge(host);
        using var cts = new CancellationTokenSource();
        _ = host.RunAsync(cts.Token);

        transport.Post("""{"callId":4,"method":"Atlantis.Dialog.OpenFolder","args":[]}""");

        var reply = await NextMessage(transport);
        Assert.Equal("/some/dir", reply.GetProperty("result").GetString());
        Assert.True(_provider.Last!.Directories);
        cts.Cancel();
    }
}
