# Google Home Bridge Win Console Client

A .NET Framework 4.8 console client for discovering and controlling smart devices via a google home bridge service, with support for mDNS discovery, WebSocket communication, and device management.

## Features

- Device discovery using mDNS (Zeroconf)
- WebSocket communication with the bridge
- Device listing, status, and control (on/off/toggle/brightness)
- Command scheduling and scripting
- Configurable via command-line and JSON config file
- Logging with optional file output and log rotation

## Requirements

- .NET Framework 4.8
- Visual Studio 2019 or later (recommended)
- NuGet packages: `Newtonsoft.Json`, `Zeroconf`

## Getting Started

1. **Clone the repository:**
   ```sh
   git clone https://github.com/citsuresh/google-home-bridge-win-console-client.git
   ```

2. **Open the solution in Visual Studio.**

3. **Restore NuGet packages.**

4. **Build and run the project.**

## Usage

Run the executable with optional command-line arguments:

```sh
GhBridgeClientFx.exe [--interval N] [--cmd list|status|on|off|toggle|brightness|ping]
                     [--deviceId <id>] [--host <ip>] [--port <port>]
                     [--token <secret>] [--mdns-off] [--tls]
                     [--cmd-schedule "list;status:bulb1;ping;brightness:bulb1:42"]
                     [--log <path>] [--config <path>] [--dev-ignore-cert-errors]
```

### Example

```sh
GhBridgeClientFx.exe --cmd list --interval 30 --log logs/client.log
```

## Configuration

You can use a JSON config file (default: `GhBridgeClientFx.json`) to set options. Command-line arguments override config file values.

## Logging

Logs are written to the console and optionally to a file (with log rotation at 1MB).

## License

This project is licensed under the MIT License.

## Contributing

Pull requests are welcome! For major changes, please open an issue first to discuss what you would like to change.

---

**Author:** [citsuresh](https://github.com/citsuresh)
