# MindVoice

**Brain-Controlled Communication System**  
*For individuals with locked-in syndrome*

---

## Overview

MindVoice is a communication aid that allows users to select words and generate speech using eye blinks detected by a NeuroSky MindWave headset. The system requires no hand movement, making it suitable for individuals with severe paralysis.

---

## How It Works

The user wears a NeuroSky headset that detects EEG signals. When the user blinks, the system detects the blink and selects the currently highlighted word. A second blink on the same word triggers text-to-speech output.

```
Headset → USB Dongle → Serial COM Port → Blink Detection → Word Selection → Speech Output
```

---

## System Requirements

### Hardware
- NeuroSky MindWave or MindWave Mobile 2 headset
- USB dongle (included with headset)
- Windows PC with USB port
- AAA battery for headset

### Software
- Windows 10 or 11
- .NET 8.0 Runtime
- Python 3.8 or higher (for middleware)
- pyserial package

---

## Installation

### Step 1: Install Dependencies

```bash
pip install pyserial
```

### Step 2: Clone the Repository

```bash
git clone https://github.com/yourusername/MindVoice.git
cd MindVoice
```

### Step 3: Build the Application

```bash
dotnet build
```

### Step 4: Run the Middleware

Open a terminal in the Middleware folder:

```bash
python blink_middleware.py
```

### Step 5: Run the Application

```bash
dotnet run
```

---

## Quick Start Guide

1. Put on the headset
   - Place sensor on forehead
   - Clip over ear
   - Ensure LED turns solid blue

2. Launch the application
   - Click CONNECT button
   - Wait for "GOOD SIGNAL" message

3. Select a word
   - Watch words highlight automatically
   - Blink when your word is highlighted
   - Word locks (turns orange)

4. Speak the word
   - Blink again on the same word
   - System speaks the word aloud

---

## Word Grid

The default word grid contains 16 common phrases:

| Row 1 | Row 2 | Row 3 | Row 4 |
|-------|-------|-------|-------|
| YES | THIRSTY | TOO HOT | THANK YOU |
| NO | HUNGRY | TOO COLD | LOVE YOU |
| HELP | IN PAIN | BATHROOM | YES!! |
| STOP | UNWELL | REPOSITION | NO!! |

---

## Signal Quality Indicators

| Display | Meaning | Action |
|---------|---------|--------|
| GOOD SIGNAL | Optimal contact | Continue use |
| FAIR SIGNAL | Weak contact | Adjust headset |
| POOR SIGNAL | Very weak | Clean sensor |
| NO SIGNAL | No connection | Check dongle and headset |

---

## Configuration

The blink detection threshold can be adjusted via the slider in the application interface. The value is saved to `config.json` and persists between sessions.

Default threshold: 200  
Range: 50 to 500  
Lower values = more sensitive  
Higher values = less sensitive

---

## Troubleshooting

### Headset not detected
- LED must be solid blue (not blinking)
- Unplug and replug USB dongle
- Turn headset off then on
- Close other NeuroSky software

### Blinks not detected
- Blink more deliberately
- Ensure signal quality is GOOD
- Lower the threshold slider

### Wrong word selected
- Wait for correct word to highlight
- Blink only when your word is highlighted

### No sound output
- Check computer volume
- Check speaker connections
- Restart the application

---

## Project Structure

```
MindVoice/
├── Form1.cs                 # Main Windows Forms UI
├── Program.cs               # Application entry point
├── MindVoice.csproj         # Project configuration
├── Middleware/
│   ├── blink_middleware.py  # Python blink detector
│   ├── requirements.txt     # Python dependencies
│   └── run_middleware.bat   # Launcher script
└── config.json              # User configuration
```

---

## Architecture

The system uses a two-part architecture:

1. **Python Middleware** - Reads raw EEG data from COM port, detects blinks, and sends events via socket
2. **C# Windows Forms** - Displays word grid, receives blink events, and outputs speech

Communication between middleware and application occurs over TCP/IP socket on port 9999.

---

## Technical Specifications

| Parameter | Value |
|-----------|-------|
| COM port baud rate | 57600 |
| Blink threshold | 200 (adjustable) |
| Scan speed range | 400-1000 ms |
| Word grid size | 4x4 (16 words) |
| Socket port | 9999 |
| Framework | .NET 8.0 Windows Forms |

---

## Safety Notes

- Clean forehead sensor with dry cloth before each use
- Replace battery when LED blinks red
- Do not use while driving or operating machinery
- Take a 5-minute break every 30 minutes

---

## License

MIT License

---

## Acknowledgments

- NeuroSky for the MindWave headset and ThinkGear protocol
- System.Speech for Windows text-to-speech capabilities

---

## Contact

For issues or questions, please open a GitHub issue.
