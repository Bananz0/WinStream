# Virtual Audio Device Driver for Windows

## Overview

To make WinStream appear as a native audio output device in the Windows audio switcher (system tray volume control), you need a **virtual audio driver**. This allows users to select "WinStream" or your AirPlay device name as an output device, just like they would with speakers or headphones.

## Options for Virtual Audio Drivers

### Option 1: Use an Existing Virtual Audio Cable (Recommended for Development)

For testing and initial development, you can use existing virtual audio cable software:

1. **VB-Audio Virtual Cable** (Free)
   - Download from: https://vb-audio.com/Cable/
   - Creates a virtual audio device that you can capture from
   - Users select "VB-Cable" as output, WinStream captures from it

2. **Virtual Audio Cable (VAC)** by Eugene Muzychenko
   - Commercial product with trial
   - More features and lower latency

3. **VoiceMeeter** (Free)
   - Full virtual audio mixer
   - Can route audio to multiple outputs

**Integration approach:**
```csharp
// Capture from the virtual cable device instead of loopback
var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
var virtualCable = devices.FirstOrDefault(d => d.Name.Contains("VB-Cable") || d.Name.Contains("Virtual"));
```

### Option 2: Windows Audio Processing Object (APO) - Lightweight

An APO is a lighter-weight solution that processes audio in the Windows audio pipeline:

- Doesn't create a new device, but can intercept audio
- Requires signing for Windows 10/11
- Good for effects/processing but not ideal for routing

### Option 3: Create a Custom Virtual Audio Driver (Full Native Experience)

For a fully native experience where WinStream devices appear in the Windows audio switcher:

#### Technology Options:

1. **Windows Driver Kit (WDK) - Kernel Mode Audio Driver**
   - Most complex but most capable
   - Requires kernel-mode driver development
   - Must be signed (EV certificate + Microsoft WHQL for Windows 10/11)
   - Can dynamically create device names (e.g., "Living Room Speakers")

2. **MSMF Audio Endpoint (User Mode)**
   - Newer approach using Media Foundation
   - Less complex than kernel drivers
   - Still requires signing

3. **Virtual Audio Device based on PortCls**
   - Microsoft's port class driver framework
   - Template: Microsoft's "Simple" audio sample driver

#### Sample Driver Structure:

```
WinStreamAudioDriver/
├── WinStreamAudio.inf          # Driver installation info
├── WinStreamAudio.sys          # Kernel mode driver
├── WinStreamAudioAPO.dll       # Audio Processing Object (optional)
└── WinStreamAudioService.exe   # User-mode service for communication
```

#### Key Driver Components:

```cpp
// Simplified driver structure (WDK/C++)

// Miniport class for WaveRT
class CWinStreamMiniport : public IMiniportWaveRT {
public:
    // Called when audio data is available
    NTSTATUS ProcessAudioData(BYTE* buffer, ULONG size) {
        // Send to user-mode service via shared memory or IOCTL
        // Service then streams to AirPlay device
    }
};

// Device creation for each AirPlay device discovered
NTSTATUS CreateAirPlayEndpoint(PCWSTR deviceName, PCWSTR ipAddress) {
    // Dynamically create audio endpoint with device name
    // "Harman Kardon SoundSticks III" appears in Windows
}
```

### Option 4: Use Windows.Devices.Audio APIs (UWP/WinRT)

For packaged apps, you can use:

```csharp
// Create a custom AudioDeviceModule
// This is limited but doesn't require driver signing
```

## Recommended Approach for WinStream

### Phase 1: Development (Current)
Use system loopback capture (already implemented) or VB-Cable for testing.

### Phase 2: Beta Release
Package with VB-Cable installer or similar, with clear setup instructions.

### Phase 3: Production Release
Develop a signed virtual audio driver that:
1. Creates virtual audio endpoints for each discovered AirPlay device
2. Shows friendly device names in Windows audio settings
3. Handles audio routing to the WinStream streaming engine

## Creating a Simple Virtual Audio Driver

If you want to create your own driver, here's a starting point:

### Prerequisites:
- Visual Studio with WDK (Windows Driver Kit)
- EV Code Signing Certificate (for production)
- Test signing enabled for development

### Steps:

1. **Install WDK**: Download from Microsoft
2. **Use the Microsoft Sample**: Start with "sysvad" sample driver
3. **Modify for virtual audio**: Remove hardware references, add shared memory
4. **Create user-mode component**: For communication with WinStream
5. **Sign the driver**: Required for Windows 10/11

### Alternative: Community Projects

Consider forking or adapting these open-source projects:

1. **VirtualAudioWire** - GitHub project for virtual audio
2. **AudioRouter** - Routes audio between devices
3. **EarTrumpet** - Not a driver but shows integration patterns

## Communication Between Driver and WinStream

```
┌─────────────────────────┐     ┌──────────────────────┐
│  Windows Audio Stack    │     │     WinStream        │
│                         │     │                      │
│  ┌─────────────────┐   │     │  ┌────────────────┐  │
│  │ Application     │   │     │  │ Audio Session  │  │
│  │ (Spotify, etc)  │   │     │  │ Manager        │  │
│  └────────┬────────┘   │     │  └───────┬────────┘  │
│           │            │     │          │           │
│  ┌────────▼────────┐   │     │  ┌───────▼────────┐  │
│  │ WinStream       │   │ IPC │  │ RTP Audio      │  │
│  │ Virtual Driver  │───┼─────┼──► Streamer       │  │
│  └─────────────────┘   │     │  └───────┬────────┘  │
│                         │     │          │           │
└─────────────────────────┘     │  ┌───────▼────────┐  │
                                │  │ AirPlay Device │  │
                                │  └────────────────┘  │
                                └──────────────────────┘
```

### IPC Options:
1. **Shared Memory** - Fastest, best for real-time audio
2. **Named Pipes** - Easier to implement
3. **Memory-Mapped Files** - Good balance
4. **Local Sockets** - Most flexible

## Quick Start: Using VB-Cable for Now

1. Install VB-Cable from https://vb-audio.com/Cable/
2. In Windows Sound Settings, set "CABLE Input" as default output
3. Modify WinStream to capture from "CABLE Output"

```csharp
// In AudioCaptureService.cs, modify CreateLoopbackCaptureAsync:
var devices = await DeviceInformation.FindAllAsync(
    MediaDevice.GetAudioRenderSelector());

// Find VB-Cable
var vbCable = devices.FirstOrDefault(d => 
    d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

if (vbCable != null)
{
    // Use VB-Cable as capture source
    var inputResult = await _audioGraph.CreateDeviceInputNodeAsync(
        MediaCategory.Media,
        _audioGraph.EncodingProperties,
        vbCable);
}
```

## Summary

| Approach | Complexity | Native Experience | Signing Required |
|----------|-----------|-------------------|------------------|
| VB-Cable | Low | Medium | No |
| APO | Medium | Low | Yes |
| Custom Driver | High | Best | Yes (EV + WHQL) |

For the best user experience, a custom virtual audio driver is ideal, but for development and initial release, using an existing virtual audio cable is practical and functional.
