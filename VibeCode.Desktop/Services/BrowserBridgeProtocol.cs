using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Length-prefixed JSON-RPC framing used by the bundled browser client on Windows.</summary>
public static class BrowserBridgeProtocol
{
    public const int MaximumFrameBytes = 64 * 1024 * 1024;

    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);

        var length = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(header)
            : BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrameBytes)
            throw new InvalidDataException($"Invalid browser bridge frame length: {length:N0} bytes.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonNode.Parse(payload) as JsonObject
               ?? throw new InvalidDataException("Browser bridge frame must contain a JSON object.");
    }

    public static async Task WriteAsync(Stream stream, JsonObject message, SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException($"Browser bridge response is too large: {payload.Length:N0} bytes.");

        var header = new byte[sizeof(int)];
        if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        else BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public static JsonObject Result(JsonNode? id, JsonNode? value) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = value,
    };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };
}
