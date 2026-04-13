# claude_tts

A lightweight Windows named-pipe TTS server that lets Claude Code speak responses
using **Piper TTS** (local neural voices) with automatic fallback to Windows
`System.Speech` (David Desktop) when the server is not running.

---

## Quick Start (new developer)

```powershell
git clone https://github.com/YOUR_USERNAME/claude_tts.git
cd claude_tts

# Downloads Piper + Ryan voice, builds the project, creates config.json
.\setup.ps1

# Option A — install to ~/.claude/tts/ and launch from Start Menu:
.\install.ps1
# Then: Start Menu > Claude TTS > Claude TTS Server

# Option B — run directly (development):
dotnet run
```

Then follow the [Claude Code Hook Scripts](#claude-code-hook-scripts) section to
wire Claude Code responses to the server.

---

## How It Works

```
Claude Code response
       │
       ▼
 tts-hook.ps1  ──pipe──▶  claude_tts.exe  ──piper.exe──▶  🔊 Ryan (neural voice)
  (Stop hook)               (this server)
       │
       └── System.Speech fallback (David Desktop) when server is not running
```

- **`tts-hook.ps1`** fires on every Claude response (Stop hook in `~/.claude/settings.json`)
- It tries to connect to the named pipe `\\.\pipe\ClaudeTTS` (500 ms timeout)
- If the server is running → text is forwarded to Piper TTS and spoken by Ryan
- If the server is not running → falls back to `System.Speech` (**David Desktop**)
- Markdown formatting (`**bold**`, `*italic*`, `# headings`) is stripped automatically
- Unicode symbols (`→`, `≈`, `€`, `°`, etc.) are mapped to spoken equivalents

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 21H1+ / Windows 11 | Required for named pipes + WinRT fallback |
| .NET 10 SDK | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Piper TTS | Downloaded automatically by `download-piper.ps1` |

---

## Install

### 1. Clone / open in VS Code

```
code "C:\Users\stryk\OneDrive\Documents\repo\claude_tts"
```

Install the recommended extensions when VS Code prompts (C# Dev Kit).

### 2. Setup (first time only)

Downloads Piper + Ryan voice, builds the project, and creates `config.json`:

```powershell
.\setup.ps1
```

To use a different voice:
```powershell
.\setup.ps1 -Voice en_US-joe-medium
# Other options: en_US-amy-low, en_GB-cori-high, en_US-hfc_male-medium
# Full list: https://rhasspy.github.io/piper-samples/
```

### 3. Install & run

**Option A — Install to `~/.claude/tts/` and launch from Start Menu (recommended):**
```powershell
.\install.ps1

# Add -AutoStart to also create a Windows Startup shortcut:
.\install.ps1 -AutoStart
```
Then launch via **Start Menu > Claude TTS > Claude TTS Server**.

**Option B — Run directly (development):**
```powershell
dotnet run
```

> **Important for future developers:** Three PowerShell hook scripts and a
> `settings.json` entry must exist in `~/.claude/` for full functionality.
> These files live **outside this repo** and are not tracked by git. See the
> [Claude Code Hook Scripts](#claude-code-hook-scripts) section below for
> required content.

---

## Claude Code Hook Scripts

These files must be created manually in `%USERPROFILE%\.claude\` (i.e. `~/.claude/`).
They are not part of this repo because they are machine-specific Claude Code
configuration files.

### Hook overview

| File | Hook type | Purpose |
|---|---|---|
| `tts-hook.ps1` | `Stop` | Speaks all assistant text after a turn completes |
| `pretool-hook.ps1` | `PreToolUse` | Speaks assistant text *before* a tool approval prompt so the user hears Claude's reasoning first |
| `notify-hook.ps1` | `Notification` | Sends `[BEEP]` to the server to play a short attention sound |

### `~/.claude/settings.json`

Add the following `hooks` block to your existing `settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%USERPROFILE%\\.claude\\pretool-hook.ps1\"",
            "async": false
          }
        ]
      }
    ],
    "Notification": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%USERPROFILE%\\.claude\\notify-hook.ps1\"",
            "async": true
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%USERPROFILE%\\.claude\\tts-hook.ps1\"",
            "async": true
          }
        ]
      }
    ]
  }
}
```

> `PreToolUse` must be **`async: false`** so speech completes before the
> approval prompt appears. The other two hooks are fire-and-forget.

---

### `~/.claude/tts-hook.ps1`

Fires on every `Stop` event. Collects all assistant text from the current turn
(skipping tool_use blocks), subtracts anything already spoken by `pretool-hook.ps1`,
and sends the remainder to the TTS server pipe.

```powershell
param()

$raw = [Console]::In.ReadToEnd()
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }

$transcriptPath = $data.transcript_path
if (-not $transcriptPath -or -not (Test-Path $transcriptPath)) { exit 0 }

# Collect all entries from the transcript
$entries = @()
foreach ($line in (Get-Content $transcriptPath -Encoding UTF8)) {
    $line = $line.Trim()
    if (-not $line) { continue }
    try {
        $entry = $line | ConvertFrom-Json
        $entries += $entry
    } catch {}
}

# Find the last user message so we only read the current turn
$lastUserIdx = -1
for ($i = 0; $i -lt $entries.Count; $i++) {
    $msg = if ($entries[$i].PSObject.Properties['message']) { $entries[$i].message } else { $entries[$i] }
    if ($msg.role -eq "user") { $lastUserIdx = $i }
}

# Extract plain text from all assistant messages after the last user message
$text = ""
for ($i = $lastUserIdx + 1; $i -lt $entries.Count; $i++) {
    $msg = if ($entries[$i].PSObject.Properties['message']) { $entries[$i].message } else { $entries[$i] }
    if ($msg.role -ne "assistant" -or $null -eq $msg.content) { continue }
    if ($msg.content -is [string]) {
        $text += $msg.content
    } else {
        foreach ($block in $msg.content) {
            if ($block.PSObject.Properties['type'] -and $block.type -eq "text" -and $block.PSObject.Properties['text']) {
                $text += $block.text
            }
        }
    }
}

$text = $text.Trim()
if (-not $text) { exit 0 }

# If PreToolUse already spoke part of this turn, only speak what's new
$stateFile = "$env:TEMP\claude_tts_spoken.tmp"
if (Test-Path $stateFile) {
    try {
        $alreadySpoken = (Get-Content $stateFile -Raw -Encoding UTF8).Trim()
        if ($text.StartsWith($alreadySpoken) -and $alreadySpoken.Length -lt $text.Length) {
            $text = $text.Substring($alreadySpoken.Length).Trim()
        } elseif ($text -eq $alreadySpoken) {
            Remove-Item $stateFile -Force -ErrorAction SilentlyContinue
            exit 0
        }
    } catch {}
    Remove-Item $stateFile -Force -ErrorAction SilentlyContinue
}

$pipeName    = "ClaudeTTS"
$pipeTimeout = 500

$sent = $false
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect($pipeTimeout)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $writer.Write($text)
    $writer.Dispose()
    $pipe.Dispose()
    $sent = $true
} catch {}

if ($sent) { exit 0 }

# Fallback: System.Speech (SAPI5) when server is not running
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.Rate   = 1
$synth.Volume = 100
$synth.SelectVoice("Microsoft David Desktop")
$synth.Speak($text)
```

---

### `~/.claude/pretool-hook.ps1`

Fires before every tool call (`PreToolUse`). Reads the transcript, collects
assistant text written so far in the current turn, and speaks only what hasn't
been spoken yet. Saves progress to a temp file so the `Stop` hook doesn't
re-read the same text.

```powershell
param()

$raw = [Console]::In.ReadToEnd()
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }

$transcriptPath = $data.transcript_path
if (-not $transcriptPath -or -not (Test-Path $transcriptPath)) { exit 0 }

$stateFile = "$env:TEMP\claude_tts_spoken.tmp"

$alreadySpoken = ""
if (Test-Path $stateFile) {
    try { $alreadySpoken = Get-Content $stateFile -Raw -Encoding UTF8 } catch {}
}

$entries = @()
foreach ($line in (Get-Content $transcriptPath -Encoding UTF8)) {
    $line = $line.Trim()
    if (-not $line) { continue }
    try { $entries += ($line | ConvertFrom-Json) } catch {}
}

$lastUserIdx = -1
for ($i = 0; $i -lt $entries.Count; $i++) {
    $msg = if ($entries[$i].PSObject.Properties['message']) { $entries[$i].message } else { $entries[$i] }
    if ($msg.role -eq "user") { $lastUserIdx = $i }
}

$fullText = ""
for ($i = $lastUserIdx + 1; $i -lt $entries.Count; $i++) {
    $msg = if ($entries[$i].PSObject.Properties['message']) { $entries[$i].message } else { $entries[$i] }
    if ($msg.role -ne "assistant" -or $null -eq $msg.content) { continue }
    if ($msg.content -is [string]) {
        $fullText += $msg.content
    } else {
        foreach ($block in $msg.content) {
            if ($block.PSObject.Properties['type'] -and $block.type -eq "text" -and $block.PSObject.Properties['text']) {
                $fullText += $block.text
            }
        }
    }
}

$newText = ""
if ($fullText.StartsWith($alreadySpoken)) {
    $newText = $fullText.Substring($alreadySpoken.Length).Trim()
} else {
    $newText = $fullText.Trim()
}

if (-not $newText) { exit 0 }

$fullText | Set-Content $stateFile -Encoding UTF8 -NoNewline

$pipeName    = "ClaudeTTS"
$pipeTimeout = 500
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect($pipeTimeout)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $writer.Write($newText)
    $writer.Dispose()
    $pipe.Dispose()
} catch {}
```

---

### `~/.claude/notify-hook.ps1`

Fires on `Notification` events (e.g. when Claude Code needs attention). Sends
`[BEEP]` to the TTS server, which plays a short Windows system sound. Falls
back to a direct system sound if the server is not running.

```powershell
param()

$pipeName    = "ClaudeTTS"
$pipeTimeout = 500

try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect($pipeTimeout)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $writer.Write("[BEEP]")
    $writer.Dispose()
    $pipe.Dispose()
} catch {
    [System.Media.SystemSounds]::Beep.Play()
    Start-Sleep -Milliseconds 500
}
```

---

## Usage

### Start the server

**Installed (recommended):** Start Menu > Claude TTS > Claude TTS Server

**Development:**
```powershell
dotnet run
```

### CLI flags

```powershell
# List available WinRT voices (informational; Piper is used when configured)
.\claude_tts.exe --list

# Speak a test string immediately (no pipe server needed)
.\claude_tts.exe --speak "Hello, this is a test."
```

### Auto-start with Windows

```powershell
.\install.ps1 -AutoStart
```

This creates a shortcut in your Windows Startup folder so the server launches
minimized at login. Re-run without `-AutoStart` to install an update without
changing the Startup shortcut.

---

## Configuration

Edit **`config.json`** next to the exe (no recompile needed; changes are picked up
automatically without restarting the server):

```json
{
  "piperExe": "piper\\piper.exe",
  "piperModel": "piper\\en_US-ryan-high.onnx",
  "rate": 0.2,
  "volume": 100,
  "pipeName": "ClaudeTTS",
  "voice": "Microsoft Zira",
  "fallbackVoice": "Microsoft David Desktop"
}
```

| Field | Description | Default |
|---|---|---|
| `piperExe` | Path to `piper.exe` (relative to exe or absolute). When set, Piper is used. | — |
| `piperModel` | Path to the `.onnx` voice model (relative or absolute) | — |
| `rate` | Speaking speed: -10 (slowest) … 0 (natural) … 10 (fastest). Decimals OK. | `0` |
| `volume` | Volume percentage | `100` |
| `pipeName` | Named pipe name — must match `tts-hook.ps1` | `ClaudeTTS` |
| `voice` | WinRT voice (used only when `piperExe` is not set or Piper files are missing) | Microsoft Zira |
| `fallbackVoice` | WinRT fallback if primary WinRT voice not found | Microsoft David Desktop |

**Piper is used automatically** when `piperExe` and `piperModel` are set and both
files exist. Remove or clear those fields to revert to the WinRT backend.

> **Note:** Always edit the `config.json` next to the running exe (e.g.
> `bin\publish\config.json`), not the one in the repo root. The repo root copy
> is the build-time template.

### Text normalization (Piper backend)

Before sending text to Piper, the server automatically:

- Strips markdown formatting (`**bold**` → `bold`, `*italic*` → `italic`, `# Heading` → `Heading`, `` `code` `` → `code`, etc.)
- Maps Unicode symbols to spoken words (`→` → "to", `€` → "euros", `°` → "degrees", `≈` → "approximately", etc.)
- Converts backslashes and parentheses to natural pauses (`bin\publish` → `bin publish`, `(System Speech)` → `, System Speech,`)
- Replaces newlines with double-comma pauses so list items flow naturally
- Strips invisible/zero-width characters and ensures text ends with closing punctuation

---

## Troubleshooting

### Pipe connection refused / fallback always used

The server is not running. Start `claude_tts.exe` and check its console for errors.

### Piper not speaking / crashes

- Confirm `bin\publish\piper\piper.exe` and the `.onnx` model file both exist
- Run `download-piper.ps1` again if files are missing
- Check `piperExe` / `piperModel` paths in `config.json` match what's on disk
- Paths can be relative to the exe or absolute

### No sound at all

- Confirm `~/.claude/settings.json` has the Stop hook with `"async": true`
- Run `/hooks` in Claude Code once to reload config
- Test the pipe manually:
  ```powershell
  $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ClaudeTTS', 'Out')
  $pipe.Connect(2000)
  $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
  $w.Write("Hello from PowerShell"); $w.Dispose(); $pipe.Dispose()
  ```

### Build fails: Windows TFM not supported

```powershell
dotnet workload install windows
```

### dotnet not found when running build.ps1

`build.ps1` auto-resolves from `C:\Program Files\dotnet\dotnet.exe`. If installed
elsewhere, add it to your PATH.

---

## Project Structure

```
claude_tts/
├── Program.cs            Entry point; named-pipe server loop + CLI flags
├── TtsEngine.cs          TTS backend (Piper or WinRT); text normalization
├── VoiceConfig.cs        config.json loader/saver
├── claude_tts.csproj     .NET 10 Windows project file
├── app.manifest          Declares Windows 10/11 compatibility
├── config.json           Runtime settings template (copied next to exe on build)
├── setup.ps1             First-time setup: download Piper + build
├── install.ps1           Build + install to ~/.claude/tts/ + Start Menu shortcut
└── .vscode/
    ├── launch.json       Debug configurations
    ├── tasks.json        Build tasks
    └── extensions.json   Recommended extensions (C# Dev Kit)
```

---

## License

MIT — do whatever you want with it.
