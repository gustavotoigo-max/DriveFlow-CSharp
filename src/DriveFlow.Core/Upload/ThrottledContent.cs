using System.Diagnostics;
using System.Net;

namespace DriveFlow.Core.Upload;

/// <summary>
/// Corpo do PUT entregue aos poucos para respeitar o limite de bytes/s.
/// O limite é lido a cada bloco, então ligar ou desligar vale no mesmo envio.
/// </summary>
public sealed class ThrottledContent : HttpContent
{
    const int Block = 64 * 1024;
    readonly byte[] data;
    readonly Func<double> limit;
    readonly CancellationToken stop;

    public ThrottledContent(byte[] data, Func<double> limit, CancellationToken stop)
    {
        this.data = data;
        this.limit = limit;
        this.stop = stop;
        Headers.ContentLength = data.Length;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var clock = Stopwatch.StartNew();
        long sent = 0;
        for (var position = 0; position < data.Length; position += Block)
        {
            var rate = limit();
            if (rate > 0 && !stop.IsCancellationRequested)
            {
                var ahead = sent / rate - clock.Elapsed.TotalSeconds;
                if (ahead > 0)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(ahead), stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            else if (rate <= 0)
            {
                clock.Restart();
                sent = 0;
            }
            var size = Math.Min(Block, data.Length - position);
            await stream.WriteAsync(data.AsMemory(position, size)).ConfigureAwait(false);
            sent += size;
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = data.Length;
        return true;
    }
}
