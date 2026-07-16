using System.Media;

namespace Huaci.App.Services.Notebook;

public interface IAlarmFlybySound : IDisposable
{
    void Play();

    void Stop();
}

/// <summary>
/// Plays a short, deliberately quiet fly-by cue generated in memory. Keeping
/// the PCM source code-native avoids shipping an additional licensed asset.
/// </summary>
public sealed class AlarmFlybySound : IAlarmFlybySound
{
    private const int SampleRate = 22_050;
    private const double DurationSeconds = 1.15;

    private readonly MemoryStream _waveStream;
    private readonly SoundPlayer _player;
    private bool _disposed;

    public AlarmFlybySound()
    {
        _waveStream = new MemoryStream(CreateWave(), writable: false);
        _player = new SoundPlayer(_waveStream);
        try
        {
            _player.Load();
        }
        catch
        {
            // Missing or disabled audio devices must never block a reminder.
        }
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _waveStream.Position = 0;
            _player.Play();
        }
        catch
        {
            // The visual reminder remains fully functional without audio.
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _player.Stop();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _player.Stop();
        }
        catch
        {
        }

        _player.Dispose();
        _waveStream.Dispose();
    }

    private static byte[] CreateWave()
    {
        int sampleCount = checked((int)Math.Round(SampleRate * DurationSeconds));
        const short channelCount = 1;
        const short bitsPerSample = 16;
        int dataLength = checked(sampleCount * channelCount * (bitsPerSample / 8));

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channelCount * (bitsPerSample / 8));
        writer.Write((short)(channelCount * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);

        uint noiseState = 0x7F4A7C15;
        double smoothedNoise = 0;
        double phase = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            double progress = index / (double)Math.Max(1, sampleCount - 1);
            double attack = Math.Min(1, progress / 0.08);
            double release = Math.Min(1, (1 - progress) / 0.24);
            double envelope = Math.Sin(Math.PI * progress) * Math.Min(attack, release);

            // A gentle Doppler-like engine tone glides downward as the plane
            // passes, mixed with filtered air noise for a soft whoosh.
            double frequency = 155 - (70 * progress);
            phase += (Math.PI * 2 * frequency) / SampleRate;
            double engine = Math.Sin(phase) + (0.24 * Math.Sin(phase * 2.01));

            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            double whiteNoise = ((noiseState & 0xFFFF) / 32767.5) - 1;
            smoothedNoise = (0.91 * smoothedNoise) + (0.09 * whiteNoise);

            double flyby = ((0.14 * engine) + (0.30 * smoothedNoise)) * envelope;
            short sample = checked((short)Math.Clamp(
                Math.Round(flyby * short.MaxValue),
                short.MinValue,
                short.MaxValue));
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
