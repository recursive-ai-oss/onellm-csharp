using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Diagnostics;
using OneLlm;
using OneLlm.Providers.Anthropic;

// A bounded smoke fuzz of the Bedrock event stream reader: seeds are valid message sequences, mutations flip, insert, delete, truncate and
// splice bytes, and half of them re-seal the checksums so the parser is exercised past its integrity checks. An input may be refused with a
// typed OneLlm error; any other exception, a read that hangs, or a runaway allocation is a finding.
// Usage: OneLlm.Fuzz <seconds> [seed]

int seconds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 60;
int seed = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : Environment.TickCount;
var rng = new Random(seed);
byte[][] seeds = Seeds();
var clock = Stopwatch.StartNew();
long runs = 0, accepted = 0, refused = 0, findings = 0;
var kinds = new Dictionary<string, long>();
long peakAlloc = 0;
Directory.CreateDirectory("findings");

while (clock.Elapsed.TotalSeconds < seconds)
{
    byte[] input = Mutate(seeds[rng.Next(seeds.Length)], rng);
    runs++;
    long before = GC.GetAllocatedBytesForCurrentThread();
    var one = Stopwatch.StartNew();
    try
    {
        await Run(input, rng.Next(2) == 0 ? 0 : rng.Next(1, 40));
        accepted++;
    }
    catch (ProviderException e)
    {
        refused++;
        string k = e.GetType().Name;
        kinds[k] = kinds.GetValueOrDefault(k) + 1;
    }
    catch (Exception e)
    {
        findings++;
        File.WriteAllBytes($"findings/{seed}-{runs}.bin", input);
        Console.WriteLine($"FINDING run {runs}: {e.GetType().Name}: {e.Message} at {string.Join(" <- ", (e.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim()))}");
    }

    peakAlloc = Math.Max(peakAlloc, GC.GetAllocatedBytesForCurrentThread() - before);
    if (one.ElapsedMilliseconds > 2000)
    {
        findings++;
        File.WriteAllBytes($"findings/{seed}-{runs}-slow.bin", input);
        Console.WriteLine($"FINDING run {runs}: took {one.ElapsedMilliseconds} ms");
    }
}

Console.WriteLine($"seed {seed}: {runs} inputs in {clock.Elapsed.TotalSeconds:F0}s, {accepted} read to the end, {refused} refused ({string.Join(", ", kinds.Select(p => p.Key + " " + p.Value))}), peak allocation per input {peakAlloc / 1024} KiB, findings {findings}");
return findings == 0 ? 0 : 1;

// Reads the input through the adapter the provider uses (the same bytes the server-sent events reader sees), in reads of a given size.
static async Task Run(byte[] input, int readSize)
{
    using var adapter = new BedrockEventStream(new ChoppedStream(input, readSize));
    var buffer = new byte[readSize == 0 ? 4096 : readSize];
    while (await adapter.ReadAsync(buffer.AsMemory()) > 0)
    {
    }
}

static byte[][] Seeds()
{
    byte[] Chunk(string json) => Frame([Str(":event-type", "chunk"), Str(":content-type", "application/json"), Str(":message-type", "event")],
        System.Text.Encoding.UTF8.GetBytes("{\"bytes\":\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + "\"}"));
    byte[] first = Chunk("""{"type":"message_start","message":{"id":"m","type":"message","role":"assistant","content":[],"model":"x","usage":{"input_tokens":1,"output_tokens":1}}}""");
    byte[] delta = Chunk("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}""");
    byte[] stop = Chunk("""{"type":"message_stop"}""");
    byte[] exception = Frame([Str(":exception-type", "throttlingException"), Str(":content-type", "application/json"), Str(":message-type", "exception")], "{\"message\":\"slow down\"}"u8.ToArray());
    byte[] error = Frame([Str(":error-code", "InternalFailure"), Str(":error-message", "boom"), Str(":message-type", "error")], []);
    byte[] allTypes = Frame(
    [
        Str(":message-type", "event"), Str(":event-type", "other"),
        [1, (byte)'a', 0], [1, (byte)'b', 1], [1, (byte)'c', 2, 0xFE], [1, (byte)'d', 3, 0xFF, 0xFE], [1, (byte)'e', 4, 0, 0, 1, 0],
        [1, (byte)'f', 5, 0, 0, 0, 0, 0, 0, 1, 0], [1, (byte)'g', 6, 0, 2, 9, 8], [1, (byte)'h', 8, 0, 0, 1, 0x8F, 0xE0, 0xB4, 0x1C, 0, 0],
        [1, (byte)'i', 9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
    ], []);
    return [[.. first, .. delta, .. stop], first, delta, exception, error, allTypes, [.. delta, .. exception]];
}

static byte[] Str(string name, string value)
{
    byte[] n = System.Text.Encoding.UTF8.GetBytes(name);
    byte[] v = System.Text.Encoding.UTF8.GetBytes(value);
    return [(byte)n.Length, .. n, 7, (byte)(v.Length >> 8), (byte)v.Length, .. v];
}

static byte[] Frame(byte[][] headers, byte[] payload)
{
    byte[] hb = headers.SelectMany(h => h).ToArray();
    int total = 12 + hb.Length + payload.Length + 4;
    var bytes = new byte[total];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)total);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)hb.Length);
    hb.CopyTo(bytes, 12);
    payload.CopyTo(bytes, 12 + hb.Length);
    Seal(bytes, 0);
    return bytes;
}

// Rewrites both checksums of the message that starts at offset, if it fits in the array.
static void Seal(byte[] b, int offset)
{
    if (b.Length - offset < 16)
    {
        return;
    }

    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset + 8), EventStreamReader.Crc32(b.AsSpan(offset, 8)));
    uint total = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));
    if (total >= 16 && total <= (uint)(b.Length - offset))
    {
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset + (int)total - 4), EventStreamReader.Crc32(b.AsSpan(offset, (int)total - 4)));
    }
}

static byte[] Mutate(byte[] seed, Random rng)
{
    var b = seed.ToList();
    int n = 1 + rng.Next(4);
    for (int i = 0; i < n && b.Count > 0; i++)
    {
        switch (rng.Next(7))
        {
            case 0: b[rng.Next(b.Count)] ^= (byte)(1 << rng.Next(8)); break;
            case 1: b[rng.Next(b.Count)] = (byte)rng.Next(256); break;
            case 2: b.Insert(rng.Next(b.Count + 1), (byte)rng.Next(256)); break;
            case 3: b.RemoveAt(rng.Next(b.Count)); break;
            case 4:
                int from = rng.Next(b.Count);
                b.RemoveRange(from, Math.Min(rng.Next(1, 8), b.Count - from));
                break;
            case 5:
                // Repeat a region, so lengths and contents disagree in new ways.
                int start = rng.Next(b.Count);
                b.InsertRange(rng.Next(b.Count + 1), b.GetRange(start, Math.Min(rng.Next(1, 24), b.Count - start)));
                break;
            default:
                // A length field set to an interesting value.
                int at = rng.Next(2) * 4;
                if (b.Count >= 8)
                {
                    uint v = rng.Next(5) switch { 0 => 0u, 1 => 15u, 2 => 16u, 3 => 0xFFFFFFFFu, _ => (uint)rng.Next(0, 300) };
                    byte[] bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
                    for (int k = 0; k < 4; k++) { b[at + k] = bytes[k]; }
                }

                break;
        }
    }

    byte[] result = b.ToArray();
    if (rng.Next(3) == 0 && result.Length > 0)
    {
        result = result[..rng.Next(result.Length + 1)];
    }

    if (rng.Next(2) == 0)
    {
        // Re-seal each message found by following the length fields, so the checksums stay valid where the structure allows.
        int pos = 0;
        while (result.Length - pos >= 16)
        {
            Seal(result, pos);
            uint total = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(pos));
            if (total < 16 || total > (uint)(result.Length - pos))
            {
                break;
            }

            pos += (int)total;
        }
    }

    return result;
}

// Hands out the input in small reads, so a frame split at any byte is covered.
internal sealed class ChoppedStream(byte[] data, int max) : Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = Math.Min(Math.Min(buffer.Length, data.Length - _pos), max == 0 ? int.MaxValue : max);
        data.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return ValueTask.FromResult(n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
