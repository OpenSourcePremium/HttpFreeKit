<h1><img src="Assets/app.png" alt="" width="40" height="40" align="top" style="vertical-align: -5px;"> HTTP FreeKit</h1>

HTTP FreeKit is a Windows application for capturing, inspecting, and testing HTTP and HTTPS traffic. It brings together a local proxy, traffic viewer, rule editor, HTTP client, and connection monitor in one desktop interface.

The official website is [httpfreekit.com](https://httpfreekit.com). Project documentation will be published there when the site is live.

## Features

- **HTTP(S) capture** through the Windows system proxy, with requests, responses, headers, timings, and available bodies shown in a live traffic log.
- **Exchange inspection** with text and JSON previews, plus a hexadecimal view for available binary data.
- **Search filters** that can be combined, for example `statuscode:200 host:example.com path:/login` or `process:example.exe remote:127.0.0.1 port:443`.
- **HAR 1.2 import and export** for the traffic log. During export, known sensitive header values can be redacted. This does not redact sensitive data that may appear in URLs or bodies.
- **Traffic rules** to block, redirect, rewrite, modify, delay, or mock exchanges, and pause an exchange at a breakpoint.
- **HTTP testing** to send requests, inspect responses, and replay captured requests.
- **Connection monitoring** for TCP and UDP, including process identification and Windows Firewall rule management.
- **Customizable interface** with multiple languages and themes.

Capture coverage depends on the application being observed: only applications that use the Windows system proxy can be inspected through HTTP FreeKit's traffic capture. HTTPS connections that cannot be decrypted remain tunnels.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 SDK or later to build the project.
- To inspect HTTPS traffic, approve installation of the HTTP FreeKit root certificate in the current user's certificate store. Some applications use certificate pinning and cannot be decrypted.

Capture changes the Windows proxy configuration. Depending on the requested operation, the application may also require elevated privileges to manage the proxy, certificates, processes, or Windows Firewall.

## Getting started

Download and run a published release when one is available.

1. Enable capture in the application.
2. Approve certificate installation if you want to inspect HTTPS traffic.
3. Use an application that respects the Windows system proxy, then inspect its exchanges in **Traffic**.

To avoid inspecting a host used by an application with certificate pinning, add it to the list of hosts excluded from HTTPS decryption. Its HTTPS traffic will then pass through as a tunnel.

## Build

Run these commands from a PowerShell prompt in the project directory:

```powershell
dotnet restore .\HttpFreeKit.csproj
dotnet build .\HttpFreeKit.csproj -c Release
```

To publish a self-contained Windows x64 executable:

```powershell
dotnet publish .\HttpFreeKit.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o .\dist
```

The `publish.cmd` script also provides the project's publish command.

## Data and security

Settings, rules, and local application data are stored in `%USERPROFILE%\.httpfreekit` by default. The data directory can be changed in the application settings.

Capture logs and HAR files may contain credentials, cookies, tokens, or other confidential data. Protect these files before sharing them. HAR export redaction applies to selected header values only; it does not sanitize URLs or bodies.

To restore the Windows proxy configuration and clean up the installation, use the application's uninstall option. Run `HTTP FreeKit.exe --cleanup` to perform system cleanup from the command line; add `--purge` to also delete the application's local data.

## Documentation and support

Documentation and support information will be available at [httpfreekit.com](https://httpfreekit.com) when the website is live.

## License

HTTP FreeKit is distributed under the MIT License. See [LICENSE](./LICENSE) for the full text and [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) for third-party dependency information.

Before redistributing a compiled release, verify the license that applies to Titanium.Web.Proxy 3.2.0, as noted in the third-party notices.
