# MobiFlight on macOS and Linux

This document describes what part of MobiFlight can run on macOS and Linux today, what cannot, and
why. It also covers how to build and release the portable tools.

## Summary

The MobiFlight **Connector** is a WinForms/WPF desktop application targeting `net10.0-windows` on
`win-x86`. It cannot be ported to macOS or Linux as-is, and no compiler flag changes that.

What *can* run everywhere is the part that talks to a flight simulator and to MobiFlight boards.
That code now lives in `src/crossplatform` and builds into a single self-contained binary for
macOS (Intel and Apple Silicon) and Linux (x64 and arm64).

This matters most for X-Plane users: X-Plane 12 runs natively on macOS and Linux, so before this
change a Mac-based X-Plane setup required a second Windows machine purely to run MobiFlight.

## What blocks a full port

Each dependency of `MobiFlightConnector.csproj`, and whether it can leave Windows:

| Dependency | Used for | Portable? | Notes |
| --- | --- | --- | --- |
| `UseWindowsForms` / `UseWPF` | The entire UI | **No** | WinForms and WPF are Windows-only in .NET. This is the hard blocker. |
| `Microsoft.FlightSimulator.SimConnect` | MSFS / FSX | **No** | SimConnect ships only as a Windows library, and MSFS itself is Windows-only. |
| `FSUIPCClientDLL` | FSUIPC sims | **No** | FSUIPC is a Windows DLL injected into the sim process. |
| `Microsoft.Web.WebView2` | Hosting the React frontend | **No** | Windows-only. A browser or WebKit host would be needed instead. |
| `SharpDX.DirectInput` | Joysticks | **No** | DirectInput is Windows-only, and SharpDX is abandoned. |
| `System.Management` | Serial port hot-plug via WMI | **No** | WMI is Windows-only. Replaced here by `SerialPortScanner`. |
| `vJoyInterface`, `ArcazeHid` | vJoy, Arcaze boards | **No** | Native Windows x86 DLLs. |
| `Midi.dll` | MIDI boards | **No** | Wraps the Win32 `winmm` API. |
| `XPlaneConnector` | X-Plane UDP | **No** (see below) | Compiles, but throws at runtime on POSIX. |
| `System.IO.Ports` | Serial boards | **Yes** | Works on all three platforms. |
| `HidSharp` | HID devices | **Yes** | Has Linux and macOS backends. |
| `CommandMessenger*` | Board protocol | **Yes** | Project-local, no Windows API use at all. |
| `Newtonsoft.Json`, `System.Reactive`, GraphQL | Data handling | **Yes** | Fully portable. |
| `frontend/` (React + Vite) | New UI | **Yes** | Plain web app; only its *host* is Windows-specific. |

### The XPlaneConnector runtime problem

`XPlaneConnector` 1.3.0 is `netstandard2.0` so it restores and compiles fine on any platform, but
its `Start()` does this:

```csharp
client = new UdpClient();
client.Connect(XPlaneEP.Address, XPlaneEP.Port);   // binds an ephemeral local port
server = new UdpClient(LocalEP);                   // binds a SECOND socket to that same port
```

Binding two UDP sockets to one address/port is permitted on Windows by default. On macOS and Linux
it fails:

```
$ python3 - <<'EOF'
import socket
c = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
c.connect(("192.0.2.10", 49000))
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(c.getsockname())
EOF
OSError: [Errno 48/98] Address already in use
```

`SO_REUSEADDR` does not help, because the first socket is already connected for unicast.

The fix is also the simpler design, and it is what `MobiFlight.Core` implements: use **one** socket
for both directions. X-Plane replies to the source address and port of the request, so the socket
that sends the subscription is exactly the socket that should receive the values.

### Build system note

The repository file was named `Directory.Build.Props`. MSBuild looks for `Directory.Build.props`,
and on case-sensitive filesystems (macOS can be either; Linux always is) the file was silently
ignored, leaving `TargetFramework` empty and the build failing with `NETSDK1013`. It has been
renamed to the canonical casing.

## The portable tools

`src/crossplatform` contains:

| Project | Contents |
| --- | --- |
| `MobiFlight.Core` | X-Plane UDP client and protocol, serial port discovery, board control, project file parsing, the execution engine, and the frontend host. |
| `MobiFlight.Cli` | The `mobiflight` command line tool. |
| `MobiFlight.Core.Tests` | Test suite, including a fake X-Plane that exercises the client end to end. |

Inside `MobiFlight.Core`:

| Namespace | Contents |
| --- | --- |
| `Xplane` | `XplaneUdpClient` (subscribe, read, write, commands, heartbeat watchdog, automatic re-subscription), `XplaneProtocol` (packet layouts as pure functions), `XplaneEndpoint`. |
| `Devices` | `SerialPortScanner`, `CmdMessengerChannel` (the firmware's wire framing), `MobiFlightBoard` (pins, displays, servos, steppers, LCDs, and button/encoder/analog input), `MobiFlightBoardProbe`. |
| `Project` | `McConfigReader` (.mcc parsing), `ExpressionEvaluator` (the `$`/`@`/`if()` language), `ConfigRunner` (datarefs drive outputs, inputs drive the sim). |
| `Web` | `FrontendHost`, an HTTP + WebSocket server that serves the React frontend and speaks the same `{key, payload}` envelope the Connector uses. |

`src/crossplatform/Directory.Build.props` deliberately does **not** import the repository root one.
MSBuild stops at the nearest file, so these projects target plain `net10.0` with no runtime
identifier. Anything that pulls in a Windows-only dependency will fail to build on the macOS and
Linux CI runners, which keeps the tree honest.

## Building

Requires the .NET 10 SDK.

```bash
cd src/crossplatform

dotnet build MobiFlightCrossPlatform.slnx
dotnet test  MobiFlightCrossPlatform.slnx
```

### Producing a release binary

```bash
dotnet publish MobiFlight.Cli/MobiFlight.Cli.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  --output dist/osx-arm64
```

Supported runtime identifiers: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.

The result is one file that carries its own .NET runtime, so the target machine needs nothing
installed. The `Cross Platform Build` GitHub Actions workflow runs the tests on Linux, macOS and
Windows, then publishes all four binaries and attaches them to a release.

### macOS notes

Binaries downloaded from a release are quarantined and unsigned. Either clear the attribute:

```bash
xattr -d com.apple.quarantine ./mobiflight
```

or build from source locally. Signing and notarization would need an Apple Developer certificate,
which the project does not currently have.

## Using it with X-Plane

X-Plane 12 **ships with UDP networking disabled**. On the machine running the sim:

1. Settings → Network → enable **"Accept incoming connections"**.
2. Allow inbound UDP on port 49000 through the firewall.
   On macOS: System Settings → Network → Firewall → Options → allow X-Plane.

Then, from the machine running the tools:

```bash
# Is everything reachable?
mobiflight doctor --host 192.168.1.10

# Live dataref values
mobiflight xplane watch sim/cockpit2/radios/actuators/com1_frequency_hz_833 --host 192.168.1.10

# Write a dataref and trigger a command
mobiflight xplane write sim/cockpit/electrical/battery_on 1 --host 192.168.1.10
mobiflight xplane command sim/systems/avionics_on --host 192.168.1.10

# Find MobiFlight boards on the local USB ports
mobiflight serial probe
```

Use `--host 127.0.0.1` (the default) when the sim runs on the same machine as the tools.

## Running a project

`src/crossplatform/samples/xplane-c172-sample.mcc` is a small X-Plane project to start from.
Replace its board serial with the one `mobiflight serial probe` reports.

```bash
# What does this project contain, and what of it can run here?
mobiflight inspect samples/xplane-c172-sample.mcc

# Drive the boards from X-Plane and X-Plane from the boards
mobiflight run samples/xplane-c172-sample.mcc --host 192.168.1.10
```

`inspect` is worth running first on any project written for MSFS: configs whose source is FSUIPC or
SimConnect are listed as unrunnable rather than silently ignored, because those sims do not exist on
macOS or Linux.

Supported today: dataref sources, `Pin`, `Display Module`, `Servo`, `Stepper` and `LcdDisplay`
outputs, the transformation and comparison steps, and button, encoder and analog inputs bound to
X-Plane datarefs or commands. Not yet supported: preconditions, config references, MobiFlight
variables, and the newer custom device types.

## Serving the frontend

The React frontend is portable; only its WebView2 host is not. `serve` builds the missing half:

```bash
cd src/MobiFlightConnector/frontend && npm install && npm run build && cd -

mobiflight serve sim/cockpit2/radios/actuators/com1_frequency_hz_833 \
  --host 192.168.1.10 --web-root src/MobiFlightConnector/frontend/dist
```

Then open `http://localhost:8080`. Static files are served with SPA fallback, and a WebSocket at
`/ws` carries `{ "key": "<MessageType>", "payload": { ... } }` messages, the same envelope the
Connector sends through WebView2's postMessage.

This is a working host and message bridge, not a finished UI backend: the frontend expects many
message types (`ProjectStatus`, `ConnectedControllers`, `ControllerDefinitions` and others) that the
portable build does not produce yet. Sim connection state and dataref updates flow today.

### Serial port access

- **Linux**: the user must be in the `dialout` group.
  `sudo usermod -aG dialout $USER`, then log out and back in.
- **macOS**: boards using the built-in CDC class need no driver and appear as `/dev/cu.usbmodem*`.
  CH340 and CP210x adapters need their vendor driver. Only `/dev/cu.*` is usable;
  `/dev/tty.*` blocks waiting for carrier detect, which a board never asserts.

## What would a full port take

In rough order of effort, and only worth doing if the project wants to commit to it:

1. **Implement the remaining frontend messages.** `FrontendHost` already serves the React app and
   carries the right envelope. What is missing is the rest of the message vocabulary the UI expects,
   plus handling the commands it sends back. This is the shortest path to a usable GUI on macOS.
2. **Fill in the remaining config features.** Preconditions, config references and MobiFlight
   variables are parsed over but not executed. Each is self-contained.
3. **Converge on one execution engine.** `ConfigRunner` currently reimplements the parts of
   `ExecutionManager` that matter for X-Plane. The better long-term shape is to move
   `ExecutionManager`, the config model and the device caches into a portable project that both the
   WinForms app and the portable build consume. Mostly mechanical, but large.
4. **Replace the Windows-only device layers.** `HidSharp` already covers HID cross-platform;
   joysticks would need an SDL or evdev/IOKit backend instead of DirectInput.
5. **Accept that MSFS support stays Windows-only.** SimConnect and FSUIPC have no macOS or Linux
   equivalent. A cross-platform build is inherently an X-Plane (and ProSim) build.
