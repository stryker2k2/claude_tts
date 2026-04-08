using System.Diagnostics;
using System.Media;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace ClaudeTts;

/// <summary>
/// Wraps TTS synthesis. Uses Piper TTS (local neural model) when piperExe/piperModel
/// are configured and the files exist; falls back to WinRT SpeechSynthesizer otherwise.
/// </summary>
public sealed class TtsEngine : IDisposable
{
    // -------------------------------------------------------------------------
    // Piper backend fields
    private readonly bool   _usePiper;
    private readonly string _piperExe   = "";
    private readonly string _piperModel = "";
    private readonly double _piperLengthScale;   // inverse speed: lower = faster

    // -------------------------------------------------------------------------
    // WinRT backend fields
    private readonly SpeechSynthesizer _synth;

    // -------------------------------------------------------------------------
    // Shared
    public string ActiveVoiceName =>
        _usePiper
            ? $"Piper: {Path.GetFileNameWithoutExtension(_piperModel)}"
            : _synth.Voice.DisplayName;

    public TtsEngine(VoiceConfig config)
    {
        _synth = new SpeechSynthesizer();

        // Resolve paths relative to the config file's directory
        var baseDir = Path.GetDirectoryName(
            Path.Combine(AppContext.BaseDirectory, "config.json"))!;

        string ResolvePath(string? p) =>
            string.IsNullOrWhiteSpace(p) ? "" :
            Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));

        var piperExe   = ResolvePath(config.PiperExe);
        var piperModel = ResolvePath(config.PiperModel);

        if (!string.IsNullOrEmpty(piperExe)   && File.Exists(piperExe) &&
            !string.IsNullOrEmpty(piperModel)  && File.Exists(piperModel))
        {
            _usePiper         = true;
            _piperExe         = piperExe;
            _piperModel       = piperModel;
            // Map rate to Piper length-scale: WinRT rate → speed multiplier → inverse = length-scale
            _piperLengthScale = 1.0 / MapWinRtRate(config.Rate);
            // Console.WriteLine($"[TTS] Backend: Piper ({Path.GetFileNameWithoutExtension(piperModel)})");
        }
        else
        {
            // WinRT backend
            _synth.Options.SpeakingRate = MapWinRtRate(config.Rate);
            _synth.Options.AudioVolume  = Math.Clamp(config.Volume / 100.0, 0.0, 1.0);

            // Use system default voice
        }
    }

    /// <summary>Returns all voices visible to the WinRT SpeechSynthesizer (informational).</summary>
    public IReadOnlyList<VoiceInformation> GetAvailableVoices() =>
        SpeechSynthesizer.AllVoices;

    /// <summary>
    /// Synthesizes <paramref name="text"/> and plays it synchronously.
    /// Uses Piper when configured; falls back to WinRT SpeechSynthesizer otherwise.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_usePiper)
            await SpeakWithPiperAsync(text, ct);
        else
            await SpeakWithWinRtAsync(text, ct);
    }

    // -------------------------------------------------------------------------
    // Piper backend

    private async Task SpeakWithPiperAsync(string text, CancellationToken ct)
    {
        text = NormalizeForSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return;
        await SynthesizeAndPlayAsync(text, ct);
    }

    private async Task SynthesizeAndPlayAsync(string text, CancellationToken ct)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_tts_{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = _piperExe,
                Arguments              = $"--model \"{_piperModel}\" --output_file \"{tmpFile}\" --length-scale {_piperLengthScale:F4}",
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start piper.exe");

            await proc.StandardInput.WriteAsync(text);
            proc.StandardInput.Close();

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            await stderrTask;

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"piper.exe exited with code {proc.ExitCode}");

            var wavBytes    = await File.ReadAllBytesAsync(tmpFile, ct);
            var paddedBytes = PrependSilence(wavBytes, silenceMs: 200);
            await File.WriteAllBytesAsync(tmpFile, paddedBytes, ct);

            using var player = new SoundPlayer(tmpFile);
            player.PlaySync();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Prepends <paramref name="silenceMs"/> milliseconds of PCM silence to a WAV file's
    /// audio data. Handles the audio-device cold-start problem where Windows takes
    /// ~150-200 ms to initialise the endpoint, which otherwise clips the first syllable.
    /// Assumes a standard 44-byte PCM WAV header (the format Piper always produces).
    /// </summary>
    private static byte[] PrependSilence(byte[] wav, int silenceMs)
    {
        if (wav.Length < 44) return wav;

        ushort channels      = BitConverter.ToUInt16(wav, 22);
        uint   sampleRate    = BitConverter.ToUInt32(wav, 24);
        ushort bitsPerSample = BitConverter.ToUInt16(wav, 34);
        int    blockAlign    = channels * bitsPerSample / 8;

        int silenceBytes = (int)(sampleRate * silenceMs / 1000) * blockAlign;
        silenceBytes = (silenceBytes / blockAlign) * blockAlign; // align to block boundary

        uint newDataSize = BitConverter.ToUInt32(wav, 40) + (uint)silenceBytes;
        uint newRiffSize = (uint)(wav.Length - 8 + silenceBytes);

        var result = new byte[wav.Length + silenceBytes];
        Array.Copy(wav, result, 44);                                              // copy header
        BitConverter.GetBytes(newRiffSize).CopyTo(result, 4);                    // patch RIFF size
        BitConverter.GetBytes(newDataSize).CopyTo(result, 40);                   // patch data size
        // bytes 44..(44+silenceBytes-1) are already zero — silence
        Array.Copy(wav, 44, result, 44 + silenceBytes, wav.Length - 44);         // copy audio

        return result;
    }

    // -------------------------------------------------------------------------
    // WinRT backend

    private async Task SpeakWithWinRtAsync(string text, CancellationToken ct)
    {
        var stream = await _synth.SynthesizeTextToStreamAsync(text);

        var inputStream = stream.GetInputStreamAt(0);
        var dataReader  = new DataReader(inputStream);
        await dataReader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        dataReader.ReadBytes(bytes);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"claude_tts_{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(tmpFile, bytes, ct);
            using var player = new SoundPlayer(tmpFile);
            player.PlaySync();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    /// <summary>
    /// Maps -10..10 rate to WinRT SpeakingRate scale (0.5..6.0).
    /// Also used as a speed multiplier for computing Piper's length-scale.
    /// </summary>
    private static double MapWinRtRate(double rate)
    {
        rate = Math.Clamp(rate, -10.0, 10.0);
        return rate >= 0
            ? 1.0 + rate * 0.5   // 0→1.0 … 10→6.0
            : 1.0 + rate * 0.05; // 0→1.0 … -10→0.5
    }

    /// <summary>
    /// Replaces Unicode symbols with spoken equivalents and strips characters
    /// that Piper's phonemizer cannot handle (arrows, math, box-drawing, etc.).
    /// Keeps plain Latin/accented letters, digits, and standard punctuation.
    /// </summary>
    private static string NormalizeForSpeech(string text)
    {
        text = text.Normalize(System.Text.NormalizationForm.FormC);

        // Strip markdown formatting so Piper doesn't say "asterisk" or "underscore"
        const System.Text.RegularExpressions.RegexOptions ML =
            System.Text.RegularExpressions.RegexOptions.Multiline;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*{3}(.+?)\*{3}", "$1");      // ***bold italic***
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_{3}(.+?)_{3}",   "$1");      // ___bold italic___
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*{2}(.+?)\*{2}", "$1");      // **bold**
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_{2}(.+?)_{2}",   "$1");      // __bold__
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*",        "$1");     // *italic*
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_(.+?)_",          "$1");     // _italic_
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+",       "", ML);   // ## headings
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");     // [link](url) -> link
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^>\s*",            "", ML);   // > blockquotes
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[-*]{3,}\s*$",    "", ML);   // --- hr
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*[-*]\s+",  "", ML);   // - bullet markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`\r\n]+)`", "$1");        // `inline code` → keep content, strip backticks
        text = text.Replace("\"", "");                                                             // quotes add nothing in speech
        text = text.Replace("(", ", ").Replace(")", ", ");                                          // parens → comma pause (~100ms) before and after
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\.[/\\]", " ");              // .\ and ./ path prefixes → space (avoids rogue sentence-boundary dot)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\w)\.(\w)", "$1 $2");       // dots inside identifiers (System.Speech, config.json) → space
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\r?\n", ", , ");             // newlines → double comma pause (period is swallowed when followed by lowercase)

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            var replacement = c switch
            {
                // Arrows
                '→' or '➜' or '➡' or '⇒' or '⟶' => " to ",
                '←' or '⬅' or '⇐' or '⟵'       => " from ",
                '↑' or '⬆' or '⇑'               => " up ",
                '↓' or '⬇' or '⇓'               => " down ",
                '↔' or '⇔'                       => " to and from ",

                // Dashes / ellipsis
                '—' or '–'   => ", ",
                '\u2026'      => "...",   // …

                // Smart quotes → plain ASCII
                '\u2018' or '\u2019' => "'",
                '\u201C' or '\u201D' => "\"",

                // Bullets / list markers
                '•' or '◦' or '▪' or '▸' or '►' or '‣' => "-",

                // Math
                '×'  => " times ",
                '÷'  => " divided by ",
                '≈'  => " approximately ",
                '≠'  => " not equal to ",
                '≤'  => " less than or equal to ",
                '≥'  => " greater than or equal to ",
                '±'  => " plus or minus ",
                '√'  => " square root of ",
                '∞'  => " infinity ",
                '∑'  => " sum ",
                '∏'  => " product ",
                '∂'  => " delta ",
                'π'  => " pi ",
                'µ' or 'μ' => " micro ",

                // Units / symbols
                '°'  => " degrees",
                '©'  => " copyright ",
                '®'  => " registered ",
                '™'  => " trademark ",
                '£'  => " pounds ",
                '€'  => " euros ",
                '¥'  => " yen ",
                '¢'  => " cents ",
                '§'  => " section ",
                '¶'  => " paragraph ",

                // Zero-width / invisible
                '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF' => "",

                // Backtick — common in Claude code spans, just drop it
                '`' => "",
                // Colon — Piper reads it aloud; silence it
                ':' => "",
                // Backslash — appears in Windows paths like bin\publish\, read as space
                '\\' => " ",
                // Angle brackets — espeak-ng tries to parse these as SSML tags → garbage output
                '<' or '>' => " ",

                // Everything else: keep if ASCII-safe, otherwise strip
                _ => c <= 127 ? c.ToString() : (char.IsLetter(c) ? c.ToString() : " ")
            };
            sb.Append(replacement);
        }

        // Collapse runs of whitespace left by stripped symbols
        var result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();

        // Ensure the text ends with sentence-closing punctuation
        if (result.Length > 0 && !".!?".Contains(result[^1]))
            result += ".";

        return result;
    }

    public void Dispose() => _synth.Dispose();
}
