using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using NimPad.Core.Storage;
using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class SingleInstanceProtocolTests
{
    private static readonly string Folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nimpad files"));

    private static SingleInstanceMessage Sample() =>
        new([Path.Combine(Folder, "notes.txt"), Path.Combine(Folder, "ünïcödé “quoted”.md")], Activate: true);

    [Fact]
    public void EncodesAsVersionedJsonAndDecodesBack()
    {
        var message = Sample();

        var bytes = SingleInstanceProtocol.Encode(message);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("{\"version\":1,\"files\":[", json, StringComparison.Ordinal);
        Assert.EndsWith("],\"activate\":true}", json, StringComparison.Ordinal);
        Assert.Equal(message, SingleInstanceProtocol.Decode(bytes));
        Assert.Equal(new SingleInstanceMessage([], Activate: false), SingleInstanceProtocol.Decode(SingleInstanceProtocol.Encode(new([], false))));
    }

    [Fact]
    public void DecodeDefaultsMissingFieldsAndIgnoresUnknownOnes()
    {
        var decoded = SingleInstanceProtocol.Decode("{\"version\":1,\"cwd\":\"x\"}"u8);

        Assert.Empty(decoded.Files);
        Assert.True(decoded.Activate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"files\":[]}")]
    [InlineData("{\"version\":2,\"files\":[]}")]
    [InlineData("{\"version\":\"1\",\"files\":[]}")]
    [InlineData("{\"version\":1,\"files\":\"a.txt\"}")]
    [InlineData("{\"version\":1,\"files\":[1]}")]
    [InlineData("{\"version\":1,\"files\":[\"relative.txt\"]}")]
    [InlineData("{\"version\":1,\"files\":[\"\"]}")]
    [InlineData("{\"version\":1,\"activate\":\"yes\"}")]
    [InlineData("{\"version\":1,\"files\":[[[[[\"deep\"]]]]]}")]
    public void RejectsInvalidMessages(string json)
    {
        Assert.Throws<InvalidDataException>(() => SingleInstanceProtocol.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void EncodeRefusesWhatDecodeWouldReject()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.Encode(new(["relative.txt"])));
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.Encode(new([Path.Combine(Folder, "a\0b")])));
        var tooMany = Enumerable.Range(0, SingleInstanceProtocol.MaxFiles + 1).Select(i => Path.Combine(Folder, $"{i}.txt")).ToList();
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.Encode(new(tooMany)));
    }

    [Fact]
    public async Task FramesMessagesWithALittleEndianLength()
    {
        using var stream = new MemoryStream();
        await SingleInstanceProtocol.WriteAsync(stream, Sample());
        await SingleInstanceProtocol.WriteAsync(stream, new([], Activate: false));

        var bytes = stream.ToArray();
        var first = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.Equal(SingleInstanceProtocol.Encode(Sample()).Length, first);

        stream.Position = 0;
        Assert.Equal(Sample(), await SingleInstanceProtocol.ReadAsync(stream));
        Assert.Equal(new SingleInstanceMessage([], false), await SingleInstanceProtocol.ReadAsync(stream));
        await Assert.ThrowsAsync<InvalidDataException>(() => SingleInstanceProtocol.ReadAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SingleInstanceProtocol.MaxMessageBytes + 1)]
    public async Task ReadRejectsLengthsOutOfRangeWithoutAllocatingThem(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => SingleInstanceProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadRejectsATruncatedMessage()
    {
        var full = new MemoryStream();
        await SingleInstanceProtocol.WriteAsync(full, Sample());
        using var truncated = new MemoryStream(full.ToArray()[..^3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => SingleInstanceProtocol.ReadAsync(truncated));
    }

    [Fact]
    public async Task TwoEndsOfARealPipeExchangeAMessageAndTheAcknowledgement()
    {
        // The App adds the mutex and the current-user ACL (Windows-only tests); the framing is the same everywhere.
        var name = "nimpad-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var received = await SingleInstanceProtocol.ReadAsync(server);
            await server.WriteAsync(new[] { SingleInstanceProtocol.Acknowledgement });
            await server.FlushAsync();
            return received;
        });

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        await SingleInstanceProtocol.WriteAsync(client, Sample(), timeout.Token);
        var answer = new byte[1];
        var read = await client.ReadAsync(answer, timeout.Token);

        Assert.Equal(1, read);
        Assert.Equal(SingleInstanceProtocol.Acknowledgement, answer[0]);
        Assert.Equal(Sample(), await serving.WaitAsync(timeout.Token));
    }

    [Fact]
    public void InstanceNamesAreKeyedByUserAndBySeparateData()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "local"));
        var defaultProfile = new AppPaths(root, DataRootSource.LocalAppData);
        const string Sid = "S-1-5-21-1004336348-1177238915-682003330-512";

        Assert.Equal("NimPad-" + Sid, SingleInstanceProtocol.GetInstanceName(Sid, defaultProfile));
        Assert.Equal("NimPad-S-1-5-21_x", SingleInstanceProtocol.GetInstanceName("S-1-5-21\\x", defaultProfile));

        var portable = SingleInstanceProtocol.GetInstanceName(Sid, new AppPaths(Path.Combine(root, "portable"), DataRootSource.Portable));
        var custom = SingleInstanceProtocol.GetInstanceName(Sid, new AppPaths(Path.Combine(root, "custom"), DataRootSource.Override));
        var customAgain = SingleInstanceProtocol.GetInstanceName(Sid, new AppPaths(Path.Combine(root, "CUSTOM") + Path.DirectorySeparatorChar, DataRootSource.Override));

        Assert.Matches("^NimPad-S-1-5-21-1004336348-1177238915-682003330-512-[0-9a-f]{16}$", portable);
        Assert.NotEqual(portable, custom);
        Assert.Equal(custom, customAgain);
        Assert.NotEqual(custom, SingleInstanceProtocol.GetInstanceName("S-1-5-21-2", new AppPaths(Path.Combine(root, "custom"), DataRootSource.Override)));
    }
}
