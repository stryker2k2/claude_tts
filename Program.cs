using System.IO.Pipes;
using System.Threading.Channels;
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

var serverConfig = VoiceConfig.Load(serverConfigPath);
var currentEngine = new TtsEngine(serverConfig);
var engineLock = new object();
bool ansi = !Console.IsOutputRedirected;

// Prints the full banner + config block, then pins those rows as a fixed header
// by setting an ANSI scroll region that starts immediately below the separator.
// Re-calling this (e.g. on config reload) clears the screen and re-establishes the region.
void PrintHeader(VoiceConfig cfg, TtsEngine eng)
{
    if (ansi) Console.Write("\x1b[r"); // reset any existing scroll region before clearing
    Console.Clear();
    Console.WriteLine("+===========================================+");
    Console.WriteLine("|          Claude TTS Server               |");
    Console.WriteLine("+===========================================+");
    Console.WriteLine();
    PrintEngineInfo(cfg, eng);
    Console.WriteLine();
    Console.WriteLine("Listening for text... (Ctrl+C to stop, Enter to stop speech)");
    Console.WriteLine(new string('-', 44));
    if (ansi)
    {
        int logRow = Console.CursorTop;   // first row available for log output (0-based)
        int height = Math.Max(Console.WindowHeight, logRow + 2);
        Console.Write($"\x1b[{logRow + 1};{height}r"); // set scroll region (1-based rows)
        Console.Write($"\x1b[{logRow + 1};1H");         // park cursor at top of scroll region
    }
}

PrintHeader(serverConfig, currentEngine);

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
            PrintHeader(newConfig, newEngine);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Config reloaded.");
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
// Speech queue — pipe loop enqueues instantly; consumer speaks one at a time
// ---------------------------------------------------------------------------
var speechQueue = Channel.CreateUnbounded<string>(
    new UnboundedChannelOptions { SingleReader = true });

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (ansi) Console.Write("\x1b[r"); // restore full-screen scrolling before shutdown output
    Console.Clear();
    Console.WriteLine("Shutting down...");
    cts.Cancel();
};

// Separate token for stopping the current utterance without shutting down the server.
// Swapped out each time Enter is pressed.
var stopSpeechLock = new object();
var stopSpeechCts  = new CancellationTokenSource();

// Consumer: dequeues and speaks sequentially so responses never overlap
var consumerTask = Task.Run(async () =>
{
    try
    {
        await foreach (var item in speechQueue.Reader.ReadAllAsync(cts.Token))
        {
            try
            {
                if (item.Equals("[BEEP]", StringComparison.OrdinalIgnoreCase))
                {
                    System.Media.SystemSounds.Beep.Play();
                    continue;
                }
                TtsEngine engineSnapshot;
                lock (engineLock) { engineSnapshot = currentEngine; }

                // Combine global shutdown token with the per-utterance stop token
                CancellationToken stopToken;
                lock (stopSpeechLock) { stopToken = stopSpeechCts.Token; }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stopToken);

                await engineSnapshot.SpeakAsync(item, linked.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* Enter pressed — speech stopped, keep running */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException) { /* cts cancelled while awaiting next queue item — clean exit */ }
});

// Keyboard listener: pressing Enter stops the current utterance and clears the queue
var keyboardTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    CancellationTokenSource oldCts;
                    lock (stopSpeechLock)
                    {
                        oldCts       = stopSpeechCts;
                        stopSpeechCts = new CancellationTokenSource();
                    }
                    oldCts.Cancel();
                    oldCts.Dispose();
                    // Drain any queued utterances
                    while (speechQueue.Reader.TryRead(out _)) { }
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Speech stopped.");
                }
            }
            else
            {
                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (InvalidOperationException) { break; } // Console input redirected — skip keyboard handling
    }
});

// ---------------------------------------------------------------------------
// Pipe server loop — accepts connections immediately, never blocks on speech
// ---------------------------------------------------------------------------
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
        await speechQueue.Writer.WriteAsync(text, cts.Token);
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

speechQueue.Writer.Complete();
await Task.WhenAll(consumerTask, keyboardTask);

lock (stopSpeechLock) { stopSpeechCts.Dispose(); }
lock (engineLock) { currentEngine.Dispose(); }
Console.WriteLine("Server stopped.");
