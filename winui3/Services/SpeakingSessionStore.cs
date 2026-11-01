using System.Text.Json;
using System.Text.Json.Serialization;

namespace ETSAssistant.WinUI3.Services;

public sealed record SpeakingSessionItem(int Number, string Type, string Prompt, string Answer, string WavPath)
{
    [JsonIgnore] public string Display => $"{Number}. [{Type}] {Prompt}";
    [JsonIgnore] public string WavName => Path.GetFileName(WavPath);
}

public sealed record SpeakingSession(string PackId, string Mode, DateTimeOffset CreatedAt,
    IReadOnlyList<SpeakingSessionItem> Items);

/// <summary>
/// Persists the pre-generated TTS list for a whole speaking paper. The file
/// lives beside the generated WAVs under the program folder so a portable
/// (possibly low-integrity) build can still save it.
/// </summary>
public sealed class SpeakingSessionStore
{
    private readonly string _path;
    private readonly string _legacyPath;

    public SpeakingSessionStore(string programRoot)
    {
        _path = Path.Combine(Path.GetFullPath(programRoot), "temp", "speaking_session.json");
        _legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ETS", "_assistant", "speaking_session.json");
    }

    public string FilePath => _path;

    public static bool IsUsableWavFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length <= 44) return false;
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != header.Length) return false;
            return header[0] == (byte)'R' && header[1] == (byte)'I' &&
                   header[2] == (byte)'F' && header[3] == (byte)'F' &&
                   header[8] == (byte)'W' && header[9] == (byte)'A' &&
                   header[10] == (byte)'V' && header[11] == (byte)'E';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    public SpeakingSession? Load()
    {
        foreach (string path in new[] { _path, _legacyPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                return JsonSerializer.Deserialize<SpeakingSession>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { }
        }
        return null;
    }

    public void Save(SpeakingSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session), new System.Text.UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    public void Clear()
    {
        foreach (string path in new[] { _path, _legacyPath })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
