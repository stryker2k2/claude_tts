TODO:
- Turn Console into a C# GUI Program (XAML?)
- Place a PTT Button (click & hold to talk)
- Top Half is Claude Text
- Bottom Half is User Voice Transcribed and editable before clicking "Send"
- Communication with Claude is done via Claude API

---

## Architecture Notes

The vision is a self-contained voice-first chat UI — you talk to Claude and hear it talk back, all within one app.

**Layout:**
- Top half: Claude's responses appear as text (and get spoken via Piper TTS as they arrive)
- Bottom half: Speech transcribed in real-time (or on PTT release), editable before sending
- PTT button: Hold to record → release to transcribe → review/edit → Send

**What changes from today:**
- The named pipe model goes away entirely
- The app owns its own Claude session via the Claude API directly
- TtsEngine (Piper) stays as-is, just called from within the GUI instead of a pipe server

**Technology candidates:**
- UI framework: WinUI 3 or WPF (both XAML-based, both native Windows — WinUI 3 is the modern choice; WPF is more mature/stable)
- Speech-to-text: Windows Speech Recognition (built-in, no dependency) or OpenAI Whisper (higher accuracy, requires model download or API call)
- TTS: Piper (already working)
- Claude: Anthropic Claude API (via HTTP/SDK)