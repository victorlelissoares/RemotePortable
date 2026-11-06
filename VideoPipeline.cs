using System.Diagnostics;
using System.Text;

namespace RemotePortable;

// FFmpeg ddagrab uses Windows Desktop Duplication (DXGI) through D3D11.
// H.264 packets are transported as MPEG-TS; the TLS protocol adds chunk framing.
internal sealed class VideoPipeline : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenRegistration cancellation;
    private readonly StringBuilder errors = new();
    public Stream Output => process.StandardOutput.BaseStream;
    public Stream? Input { get; }
    public string Diagnostics { get { lock (errors) return errors.ToString(); } }

    private VideoPipeline(Process p, bool input, CancellationToken token)
    {
        process = p;
        Input = input ? p.StandardInput.BaseStream : null;
        cancellation = token.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync() is { } line)
                {
                    lock (errors)
                    {
                        if (errors.Length > 8000) errors.Remove(0, 4000);
                        errors.AppendLine(line);
                    }
                }
            }
            catch { }
        });
    }

    private static Process Launch(string arguments, bool input)
    {
        string local = EmbeddedFfmpeg.GetExecutablePath();
        var psi = new ProcessStartInfo(local, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input
        };
        return Process.Start(psi) ?? throw new IOException("Não foi possível iniciar FFmpeg.");
    }

    public static VideoPipeline StartEncoder(int fps, CancellationToken token)
    {
        // DXGI hardware frames -> NV12 for Media Foundation's H.264 encoder.
        // MPEG-TS allows incremental decoding over a continuous byte stream.
        string args = $"-hide_banner -loglevel warning -nostdin -f lavfi -i \"ddagrab=output_idx=0:framerate={fps}\" " +
            "-vf \"hwdownload,format=bgra,format=nv12\" -an -c:v h264_mf -b:v 3500k -g 30 " +
            "-bf 0 -f mpegts -mpegts_flags +resend_headers -flush_packets 1 pipe:1";
        return new VideoPipeline(Launch(args, false), false, token);
    }

    public static VideoPipeline StartDecoder(int width, int height, CancellationToken token)
    {
        string args = "-hide_banner -loglevel warning -fflags nobuffer -flags low_delay " +
            "-probesize 32768 -analyzeduration 0 -f mpegts -i pipe:0 " +
            $"-an -f rawvideo -pix_fmt bgra -s {width}x{height} pipe:1";
        return new VideoPipeline(Launch(args, true), true, token);
    }

    public ValueTask DisposeAsync()
    {
        cancellation.Dispose();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
