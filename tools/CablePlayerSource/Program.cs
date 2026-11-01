using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const uint WaveHeaderDone = 0x00000001;
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatIeeeFloat = 0x0003;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const int WsolaSearchStep = 4;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private static int Main(string[] args)
    {
        string wavPath;
        double speed = 1.0;
        if (args.Length == 1)
        {
            wavPath = args[0];
        }
        else if (args.Length == 3 && args[0] == "--speed" &&
                 double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
        {
            wavPath = args[2];
        }
        else
        {
            Console.Error.WriteLine("usage: CablePlayer [--speed <1.0-1.5>] <wav>");
            return 2;
        }

        try
        {
            if (!double.IsFinite(speed) || speed < 1.0 || speed > 1.5)
                throw new ArgumentOutOfRangeException(nameof(speed), "Playback speed must be between 1.0 and 1.5.");
            Play(wavPath, speed);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("playback error: " + ex.Message);
            return 1;
        }
    }

    private static void Play(string wavPath, double speed)
    {
        wavPath = Path.GetFullPath(wavPath);
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("missing: " + wavPath);

        (WaveFormatEx format, byte[] audio) = ReadWave(wavPath);
        if (speed > 1.0)
            audio = TimeStretch(audio, format, speed);
        if (audio.Length == 0 || audio.Length % format.BlockAlign != 0)
            throw new InvalidDataException("Invalid WAV data length or block alignment.");

        WaveOutDevice device = FindCableDevice();
        IntPtr waveOut = IntPtr.Zero;
        IntPtr audioBuffer = IntPtr.Zero;
        IntPtr headerBuffer = IntPtr.Zero;
        bool prepared = false;
        bool completed = false;

        try
        {
            uint result = waveOutOpen(out waveOut, new UIntPtr((uint)device.Id), ref format,
                IntPtr.Zero, IntPtr.Zero, 0);
            ThrowMmResult(result, "waveOutOpen");

            audioBuffer = Marshal.AllocHGlobal(audio.Length);
            Marshal.Copy(audio, 0, audioBuffer, audio.Length);

            WaveHeader header = new()
            {
                Data = audioBuffer,
                BufferLength = checked((uint)audio.Length),
                BytesRecorded = 0,
                User = UIntPtr.Zero,
                Flags = 0,
                Loops = 0,
                Next = IntPtr.Zero,
                Reserved = UIntPtr.Zero,
            };
            int headerSize = Marshal.SizeOf<WaveHeader>();
            headerBuffer = Marshal.AllocHGlobal(headerSize);
            Marshal.StructureToPtr(header, headerBuffer, false);

            ThrowMmResult(waveOutPrepareHeader(waveOut, headerBuffer, (uint)headerSize), "waveOutPrepareHeader");
            prepared = true;
            ThrowMmResult(waveOutWrite(waveOut, headerBuffer, (uint)headerSize), "waveOutWrite");

            Console.WriteLine($"PLAY {device.Name} {Path.GetFileName(wavPath)} speed={speed.ToString("F2", CultureInfo.InvariantCulture)}x");
            Console.Out.Flush();

            double durationSeconds = audio.Length / (double)format.AverageBytesPerSecond;
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, durationSeconds + 8));
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                WaveHeader current = Marshal.PtrToStructure<WaveHeader>(headerBuffer);
                if ((current.Flags & WaveHeaderDone) != 0)
                {
                    completed = true;
                    break;
                }
                if (elapsed.Elapsed > timeout)
                    throw new TimeoutException($"Cable Input playback did not finish within {timeout.TotalSeconds:F0}s.");
                Thread.Sleep(10);
            }

            ThrowMmResult(waveOutUnprepareHeader(waveOut, headerBuffer, (uint)headerSize), "waveOutUnprepareHeader");
            prepared = false;
            Console.WriteLine("DONE");
            Console.Out.Flush();
        }
        finally
        {
            if (waveOut != IntPtr.Zero)
            {
                if (!completed)
                    waveOutReset(waveOut);
                if (prepared && headerBuffer != IntPtr.Zero)
                    waveOutUnprepareHeader(waveOut, headerBuffer, (uint)Marshal.SizeOf<WaveHeader>());
                waveOutClose(waveOut);
            }
            if (headerBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(headerBuffer);
            if (audioBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(audioBuffer);
        }
    }

    private static byte[] TimeStretch(byte[] audio, WaveFormatEx format, double speed)
    {
        if (format.FormatTag != WaveFormatPcm || format.Channels != 1 || format.BitsPerSample != 16)
            throw new InvalidDataException("Speed adjustment currently requires mono 16-bit PCM WAV audio.");

        short[] input = new short[audio.Length / sizeof(short)];
        Buffer.BlockCopy(audio, 0, input, 0, audio.Length);
        int targetLength = Math.Max(1, (int)Math.Round(input.Length / speed));
        int frameSize = Math.Max(80, (int)(format.SamplesPerSecond / 40));
        int overlap = frameSize / 2;
        int hop = frameSize - overlap;
        int search = Math.Max(16, (int)(format.SamplesPerSecond / 200));
        if (input.Length <= frameSize)
            return audio;

        var output = new List<short>(targetLength);
        for (int i = 0; i < frameSize; i++)
            output.Add(input[i]);

        int previousInputStart = 0;
        while (output.Count < targetLength)
        {
            int expectedStart = previousInputStart + (int)Math.Round(hop * speed);
            int minStart = Math.Max(previousInputStart + 1, expectedStart - search);
            int maxStart = Math.Min(input.Length - frameSize, expectedStart + search);
            if (maxStart < minStart)
                break;

            int outputOverlapStart = output.Count - overlap;
            int bestStart = minStart;
            double bestScore = double.NegativeInfinity;
            for (int candidate = minStart; candidate <= maxStart; candidate += WsolaSearchStep)
            {
                double dot = 0;
                double outputEnergy = 1;
                double inputEnergy = 1;
                for (int i = 0; i < overlap; i++)
                {
                    double left = output[outputOverlapStart + i];
                    double right = input[candidate + i];
                    dot += left * right;
                    outputEnergy += left * left;
                    inputEnergy += right * right;
                }
                double score = dot / Math.Sqrt(outputEnergy * inputEnergy);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = candidate;
                }
            }

            for (int i = 0; i < overlap; i++)
            {
                double blend = (i + 1.0) / (overlap + 1.0);
                double mixed = output[outputOverlapStart + i] * (1.0 - blend) + input[bestStart + i] * blend;
                output[outputOverlapStart + i] = (short)Math.Clamp((int)Math.Round(mixed), short.MinValue, short.MaxValue);
            }
            for (int i = overlap; i < frameSize; i++)
                output.Add(input[bestStart + i]);
            previousInputStart = bestStart;
        }

        if (output.Count > targetLength)
            output.RemoveRange(targetLength, output.Count - targetLength);
        else if (output.Count < targetLength)
        {
            short last = output.Count > 0 ? output[^1] : (short)0;
            output.AddRange(Enumerable.Repeat(last, targetLength - output.Count));
        }

        byte[] stretched = new byte[output.Count * sizeof(short)];
        Buffer.BlockCopy(output.ToArray(), 0, stretched, 0, stretched.Length);
        return stretched;
    }

    private static WaveOutDevice FindCableDevice()
    {
        List<WaveOutDevice> devices = [];
        uint count = waveOutGetNumDevs();
        int size = Marshal.SizeOf<WaveOutCaps>();
        for (uint id = 0; id < count; id++)
        {
            if (WaveOutGetDevCaps(new UIntPtr(id), out WaveOutCaps caps, (uint)size) != 0)
                continue;

            string name = caps.ProductName ?? string.Empty;
            int score = name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ? 0
                : name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("16 Ch", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("Output", StringComparison.OrdinalIgnoreCase) ? 1
                : int.MaxValue;
            if (score != int.MaxValue)
                devices.Add(new WaveOutDevice((int)id, name, score));
        }

        WaveOutDevice? selected = devices.OrderBy(device => device.Score).FirstOrDefault();
        if (selected is null)
            throw new InvalidOperationException("no VB-Audio render device (CABLE Input) was found through WinMM.");
        return selected;
    }

    private static (WaveFormatEx Format, byte[] Audio) ReadWave(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadFourCc(reader) != "RIFF")
            throw new InvalidDataException("Invalid WAV: RIFF header is missing.");
        _ = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE")
            throw new InvalidDataException("Invalid WAV: WAVE signature is missing.");

        byte[]? formatBytes = null;
        byte[]? audioBytes = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadFourCc(reader);
            uint chunkLength = reader.ReadUInt32();
            if (chunkLength > int.MaxValue || stream.Position + chunkLength > stream.Length)
                throw new InvalidDataException($"Invalid WAV chunk '{id}'.");

            if (id == "fmt ")
                formatBytes = ReadExact(reader, (int)chunkLength);
            else if (id == "data")
                audioBytes = ReadExact(reader, (int)chunkLength);
            else
                stream.Seek(chunkLength, SeekOrigin.Current);

            if ((chunkLength & 1) != 0 && stream.Position < stream.Length)
                stream.Seek(1, SeekOrigin.Current);
        }

        if (formatBytes is null || formatBytes.Length < 16 || audioBytes is null)
            throw new InvalidDataException("WAV must contain valid fmt and data chunks.");

        ushort tag = BitConverter.ToUInt16(formatBytes, 0);
        ushort channels = BitConverter.ToUInt16(formatBytes, 2);
        uint sampleRate = BitConverter.ToUInt32(formatBytes, 4);
        uint averageBytesPerSecond = BitConverter.ToUInt32(formatBytes, 8);
        ushort blockAlign = BitConverter.ToUInt16(formatBytes, 12);
        ushort bitsPerSample = BitConverter.ToUInt16(formatBytes, 14);

        if (tag == WaveFormatExtensible)
        {
            if (formatBytes.Length < 40)
                throw new InvalidDataException("Invalid WAVE_FORMAT_EXTENSIBLE fmt chunk.");
            Guid subFormat = new(formatBytes.AsSpan(24, 16));
            tag = subFormat == PcmSubFormat ? WaveFormatPcm
                : subFormat == FloatSubFormat ? WaveFormatIeeeFloat
                : (ushort)0;
        }

        if (tag is not (WaveFormatPcm or WaveFormatIeeeFloat))
            throw new InvalidDataException($"Unsupported WAV encoding (format tag 0x{tag:X4}).");
        if (channels == 0 || sampleRate == 0 || averageBytesPerSecond == 0 || blockAlign == 0 || bitsPerSample == 0)
            throw new InvalidDataException("WAV format values are invalid.");

        return (new WaveFormatEx
        {
            FormatTag = tag,
            Channels = channels,
            SamplesPerSecond = sampleRate,
            AverageBytesPerSecond = averageBytesPerSecond,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            ExtraSize = 0,
        }, audioBytes);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
            throw new InvalidDataException("Unexpected end of WAV file.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException("Unexpected end of WAV chunk.");
        return bytes;
    }

    private static void ThrowMmResult(uint result, string operation)
    {
        if (result == 0)
            return;
        StringBuilder message = new(256);
        _ = WaveOutGetErrorText(result, message, (uint)message.Capacity);
        throw new InvalidOperationException($"{operation} failed (MMRESULT {result}): {message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public UIntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    private sealed record WaveOutDevice(int Id, string Name, int Score);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint WaveOutGetDevCaps(UIntPtr deviceId, out WaveOutCaps caps, uint capsSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutOpen(out IntPtr waveOut, UIntPtr deviceId, ref WaveFormatEx format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutWrite(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutClose(IntPtr waveOut);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetErrorTextW", CharSet = CharSet.Unicode)]
    private static extern uint WaveOutGetErrorText(uint result, StringBuilder message, uint messageSize);
}
