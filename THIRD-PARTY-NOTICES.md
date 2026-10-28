# Third-party notices

HTTP FreeKit includes or depends on the following third-party components. Their
licenses apply to those components, not to the HTTP FreeKit source code.

## Runtime dependencies

| Component | Version | License / notice |
|---|---:|---|
| AvalonEdit | 6.3.1.120 | MIT — [license](https://github.com/icsharpcode/AvalonEdit/blob/master/LICENSE) |
| ModernWpfUI | 0.9.6 | MIT — [license](https://github.com/Kinnara/ModernWpf/blob/master/LICENSE) |
| Titanium.Web.Proxy | 3.2.0 | The NuGet package does not declare license metadata. The upstream repository currently identifies the NuGet library as MIT: [upstream license](https://github.com/justcoding121/Titanium-Web-Proxy/blob/master/LICENSE). |
| BrotliSharpLib | 0.3.3 | MIT — [license](https://github.com/master131/BrotliSharpLib/blob/master/LICENSE) |
| Portable.BouncyCastle | 1.8.8 | License information — [Bouncy Castle C# license](https://www.bouncycastle.org/csharp/licence.html). Preserve the applicable notice when redistributing. |
| Microsoft.NETCore.Platforms | 5.0.0 | MIT — [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Microsoft.Win32.Registry | 5.0.0 | MIT — [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| System.Runtime.CompilerServices.Unsafe | 4.5.2 | MIT — [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| System.Security.AccessControl | 5.0.0 | MIT — [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| System.Security.Principal.Windows | 5.0.0 | MIT — [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

The self-contained Windows publish also bundles the .NET runtime. The runtime
is distributed under the MIT license; see the [.NET license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).

## Build tooling

`Microsoft.NET.ILLink.Tasks` 8.0.32 is used by the .NET publishing toolchain and
is not an application runtime dependency. It is MIT-licensed under the
[.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).

This list reflects the package versions restored by the project at the time it
was prepared. Recheck package metadata and notices whenever dependencies or
versions change. The upstream Titanium license note is a release-readiness item,
not a legal determination.
