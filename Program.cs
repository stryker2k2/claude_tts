using System.IO.Pipes;
using ClaudeTts;

// ---------------------------------------------------------------------------
// Claude TTS Server
// Receives text over a named pipe and speaks it using the WinRT
// SpeechSynthesizer, which has access to Natural HD voices (Andrew, Ava).
//
// Usage:
//   claude_tts.exe              -- starts server using config.json
//   claude_tts.exe --list       -- lists available voices and exits
//   claude_tts.exe --speak "…"  -- speaks a single string and exits (for testing)
// ---------------------------------------------------------------------------

// Handle simple CLI flags before starting the server
if (args.Length > 0)
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    var config     = VoiceConfig.Load(configPath);
    using var engine = new TtsEngine(config);

    switch (args[0].ToLowerInvariant())
    {
        case "--list":
            Console.WriteLine("Available voices:");
            foreach (var v in engine.GetAvailableVoices())
            {
                var active = v.DisplayName == engine.ActiveVoiceName ? " *" : "  ";
                Console.WriteLine($"{active} {v.DisplayName} ({v.Gender}, {v.Language})");
            }
            Console.WriteLine("\n* = active voice");
            return;

        case "--speak":
            var text = string.Join(" ", args[1..]);
            if (!string.IsNullOrWhiteSpace(text))
                await engine.SpeakAsync(text);
            return;

        default:
            Console.WriteLine("Unknown argument. Valid flags: --list, --speak \"text\"");
            return;
    }
}

// ---------------------------------------------------------------------------
// Server mode
// ---------------------------------------------------------------------------

var serverConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

void PrintEngineInfo(VoiceConfig cfg, TtsEngine eng)
{
    Console.WriteLine($"Active voice : {eng.ActiveVoiceName}");
    Console.WriteLine($"Rate         : {cfg.Rate:G}  (-10 slowest / 10 fastest, decimals ok)");
    Console.WriteLine($"Volume       : {cfg.Volume}%");
    Console.WriteLine($"Pipe         : \\\\.\\pipe\\{cfg.PipeName}");
    if (!string.IsNullOrEmpty(cfg.PiperExe))
        Console.WriteLine($"Backend      : Piper TTS");
}

Console.WriteLine("+===========================================+");
Console.WriteLine("|          Claude TTS Server               |");
Console.WriteLine("+===========================================+");
Console.WriteLine();

var serverConfig = VoiceConfig.Load(serverConfigPath);
var currentEngine = new TtsEngine(serverConfig);
var engineLock = new object();

Console.WriteLine();
PrintEngineInfo(serverConfig, currentEngine);
Console.WriteLine();
Console.WriteLine("Listening for text... (Ctrl+C to stop)");
Console.WriteLine(new string('-', 44));

// ---------------------------------------------------------------------------
// File watcher -- reloads config.json on change without restarting
// Many editors (VS Code, Notepad++) do atomic saves: write temp file -> rename,
// so we listen for Changed, Created, AND Renamed to cover all save patterns.
// A debounce flag prevents double-firing within the same save operation.
// ---------------------------------------------------------------------------
var reloadPending = 0; // used as bool via Interlocked

void OnConfigFileEvent(object _, FileSystemEventArgs e)
{
    // Only care about config.json (Renamed sends the new name in e.Name)
    if (!e.Name!.Equals("config.json", StringComparison.OrdinalIgnoreCase)) return;

    // Debounce: ignore if a reload is already queued
    if (Interlocked.CompareExchange(ref reloadPending, 1, 0) != 0) return;

    Task.Run(() =>
    {
        Thread.Sleep(300); // let the editor finish writing
        Interlocked.Exchange(ref reloadPending, 0);
        try
        {
            var newConfig = VoiceConfig.Load(serverConfigPath);
            var newEngine = new TtsEngine(newConfig);
            TtsEngine? oldEngine;
            lock (engineLock)
            {
                oldEngine     = currentEngine;
                currentEngine = newEngine;
                serverConfig  = newConfig;
            }
            oldEngine.Dispose();
            Console.WriteLine();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] config.json reloaded:");
            PrintEngineInfo(newConfig, newEngine);
            Console.WriteLine(new string('-', 44));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to reload config: {ex.Message}");
        }
    });
}

using var watcher = new FileSystemWatcher(Path.GetDirectoryName(serverConfigPath)!)
{
    NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
    EnableRaisingEvents = true
};

watcher.Changed += OnConfigFileEvent;
watcher.Created += OnConfigFileEvent;
watcher.Renamed += (s, e) => OnConfigFileEvent(s, e);

// ---------------------------------------------------------------------------
// Pipe server loop
// ---------------------------------------------------------------------------
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    string pipeName;
    lock (engineLock) { pipeName = serverConfig.PipeName; }

    var server = new NamedPipeServerStream(
        pipeName,
        PipeDirection.In,
        maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
        transmissionMode: PipeTransmissionMode.Byte,
        options: PipeOptions.Asynchronous);

    try
    {
        await server.WaitForConnectionAsync(cts.Token);

        using var reader = new StreamReader(server, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cts.Token);

        if (string.IsNullOrWhiteSpace(text))
            continue;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

        // Special commands
        if (text.Equals("[BEEP]", StringComparison.OrdinalIgnoreCase))
        {
            System.Media.SystemSounds.Beep.Play();
            continue;
        }

        TtsEngine engineSnapshot;
        lock (engineLock) { engineSnapshot = currentEngine; }
        await engineSnapshot.SpeakAsync(text, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
    }
    finally
    {
        await server.DisposeAsync();
    }
}

lock (engineLock) { currentEngine.Dispose(); }
Console.WriteLine("Server stopped.");
