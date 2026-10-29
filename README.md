<h1 align="center">
  <img src="Assets/app.png" alt="" width="64" height="64"><br>
  HTTP FreeKit
</h1>

<p align="center">
  Capture, inspect, and test HTTP(S) traffic on Windows — local proxy, traffic viewer, rule editor, HTTP client, and connection monitor in one desktop app.
</p>

<p align="center">
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/OpenSourcePremium/HttpFreeKit?include_prereleases&sort=semver&logo=github"></a>
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/OpenSourcePremium/HttpFreeKit/total?logo=github"></a>
  <a href="./LICENSE"><img alt="License" src="https://img.shields.io/github/license/OpenSourcePremium/HttpFreeKit"></a>
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/OpenSourcePremium/HttpFreeKit?style=flat&logo=github"></a>
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/network/members"><img alt="Forks" src="https://img.shields.io/github/forks/OpenSourcePremium/HttpFreeKit?style=flat&logo=github"></a>
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/issues"><img alt="Open issues" src="https://img.shields.io/github/issues/OpenSourcePremium/HttpFreeKit"></a>
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/commits"><img alt="Last commit" src="https://img.shields.io/github/last-commit/OpenSourcePremium/HttpFreeKit"></a>
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8%2B-512BD4?logo=dotnet">
  <img alt="Top language" src="https://img.shields.io/github/languages/top/OpenSourcePremium/HttpFreeKit">
  <img alt="Repo size" src="https://img.shields.io/github/repo-size/OpenSourcePremium/HttpFreeKit">
</p>

<p align="center">
  <a href="https://httpfreekit.com">Website</a> ·
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/releases/latest">Download</a> ·
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/issues">Report a bug</a> ·
  <a href="https://github.com/OpenSourcePremium/HttpFreeKit/issues/new">Request a feature</a>
</p>

<!-- Add a screenshot or GIF here, e.g.:
<p align="center"><img src="docs/screenshot.png" alt="HTTP FreeKit traffic view" width="900"></p>
-->

## Table of contents

- [Overview](#overview)
- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Search filter examples](#search-filter-examples)
- [Build](#build)
- [Data and security](#data-and-security)
- [Project activity](#project-activity)
- [Contributing](#contributing)
- [Documentation and support](#documentation-and-support)
- [License](#license)

## Overview

HTTP FreeKit is a Windows application for capturing, inspecting, and testing HTTP and HTTPS traffic. It brings together a local proxy, traffic viewer, rule editor, HTTP client, and connection monitor in one desktop interface.

You can find a simple documentation on the official website : [httpfreekit.com](https://httpfreekit.com)

## Features

| Feature | Details |
|---|---|
| **HTTP(S) capture** | Through the Windows system proxy: requests, responses, headers, timings, and available bodies in a live traffic log. |
| **Exchange inspection** | Text and JSON previews, plus a hexadecimal view for available binary data. |
| **Search filters** | Combinable filters such as `statuscode:200 host:example.com path:/login`. |
| **HAR 1.2 import/export** | Known sensitive header values can be redacted on export (URLs and bodies are *not* redacted). |
| **Traffic rules** | Block, redirect, rewrite, modify, delay, or mock exchanges; pause an exchange at a breakpoint. |
| **HTTP testing** | Send requests, inspect responses, and replay captured requests. |
| **Connection monitoring** | TCP and UDP, with process identification and Windows Firewall rule management. |
| **Customizable UI** | Multiple languages and themes. |

> **Capture coverage:** only applications that use the Windows system proxy can be inspected. HTTPS connections that cannot be decrypted remain tunnels.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 SDK or later to build the project.
- To inspect HTTPS traffic, approve installation of the HTTP FreeKit root certificate in the current user's certificate store. Some applications use certificate pinning and cannot be decrypted.

Capture changes the Windows proxy configuration. Depending on the requested operation, the application may also require elevated privileges to manage the proxy, certificates, processes, or Windows Firewall.

## Getting started

Download and run a [published release](https://github.com/OpenSourcePremium/HttpFreeKit/releases/latest) when one is available.

1. Enable capture in the application.
2. Approve certificate installation if you want to inspect HTTPS traffic.
3. Use an application that respects the Windows system proxy, then inspect its exchanges in **Traffic**.

To avoid inspecting a host used by an application with certificate pinning, add it to the list of hosts excluded from HTTPS decryption. Its HTTPS traffic will then pass through as a tunnel.

## Search filter examples

```text
statuscode:200 host:example.com path:/login
process:example.exe remote:127.0.0.1 port:443
```

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

> **Warning:** Capture logs and HAR files may contain credentials, cookies, tokens, or other confidential data. Protect these files before sharing them. HAR export redaction applies to selected header values only; it does not sanitize URLs or bodies.

To restore the Windows proxy configuration and clean up the installation, use the application's uninstall option. Run `HTTP FreeKit.exe --cleanup` to perform system cleanup from the command line; add `--purge` to also delete the application's local data.

## Project activity

<p align="center">
  <img alt="Commit activity" src="https://img.shields.io/github/commit-activity/m/OpenSourcePremium/HttpFreeKit">
  <img alt="Contributors" src="https://img.shields.io/github/contributors/OpenSourcePremium/HttpFreeKit">
  <img alt="Closed issues" src="https://img.shields.io/github/issues-closed/OpenSourcePremium/HttpFreeKit">
  <img alt="Pull requests" src="https://img.shields.io/github/issues-pr/OpenSourcePremium/HttpFreeKit">
  <img alt="Watchers" src="https://img.shields.io/github/watchers/OpenSourcePremium/HttpFreeKit?style=flat">
</p>

### Star history

<a href="https://star-history.com/#OpenSourcePremium/HttpFreeKit&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=OpenSourcePremium/HttpFreeKit&type=Date&theme=dark">
    <img alt="Star history" src="https://api.star-history.com/svg?repos=OpenSourcePremium/HttpFreeKit&type=Date" width="600">
  </picture>
</a>

### Contributors

<a href="https://github.com/OpenSourcePremium/HttpFreeKit/graphs/contributors">
  <img alt="Contributors" src="https://contrib.rocks/image?repo=OpenSourcePremium/HttpFreeKit">
</a>

## Contributing

Contributions, bug reports, and ideas are welcome.

1. Fork the repository and create a branch (`git checkout -b feature/my-feature`).
2. Build and test locally (see [Build](#build)).
3. Open a pull request describing the change.

Please never attach raw captures or HAR files to issues without removing credentials, cookies, and tokens first.

## Documentation and support

Documentation and support information will be available at [httpfreekit.com](https://httpfreekit.com) when the website is live. In the meantime, use [GitHub Issues](https://github.com/OpenSourcePremium/HttpFreeKit/issues).

## License

HTTP FreeKit is distributed under the MIT License. See [LICENSE](./LICENSE) for the full text and [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) for third-party dependency information.

Before redistributing a compiled release, verify the license that applies to Titanium.Web.Proxy 3.2.0, as noted in the third-party notices.
