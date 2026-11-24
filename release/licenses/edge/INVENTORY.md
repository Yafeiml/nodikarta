# Edge x64 distribution notices

The [package list](package-versions.txt) records the resolved .NET packages in the Windows x64 desktop and MCP distribution. These packages provide the runtime code linked into the Native AOT applications. Linux also uses the X11 and FreeDesktop backends, the Linux SkiaSharp and HarfBuzzSharp native assets, and Tmds.DBus.Protocol. The installer, native launcher and embedded font are listed separately below.

Windows 和 Linux x64 发行版的依赖、原生启动器、安装程序与内嵌字体分别遵循各自的上游许可。各项随附声明可离线阅读。

| Distributed component | License and notices supplied with this distribution |
| --- | --- |
| .NET and ASP.NET Core 10.0.15, Microsoft.Data.Sqlite, ProtectedData and ServiceController | [MIT](dotnet-LICENSE.txt), [.NET runtime notices](dotnet-runtime-NOTICES.txt) and [ASP.NET Core notices](aspnetcore-NOTICES.txt). These texts are taken from the matching Windows runtime packages. |
| Microsoft.Extensions.AI.Abstractions 10.11.0 | [MIT](dotnet-extensions-LICENSE.txt) and [third-party notices](dotnet-extensions-NOTICES.txt). [Upstream revision](https://github.com/dotnet/extensions/tree/25f07afa51034e37b334210642d7dc5f191c142d). |
| Avalonia, Win32, X11, FreeDesktop, Remote.Protocol, Fluent theme and Skia integration 11.3.13 | [MIT](Avalonia-LICENSE.txt) and [Avalonia notices](Avalonia-NOTICE.md). [Upstream revision](https://github.com/AvaloniaUI/Avalonia/tree/2825c063ea68020d4881234b6566ccd54b457d5d). |
| Roboto Light 2.137 embedded in Avalonia.Dialogs | Copyright 2011 Google Inc. All Rights Reserved. [Apache License 2.0](Apache-2.0.txt). The [font](https://github.com/AvaloniaUI/Avalonia/blob/2825c063ea68020d4881234b6566ccd54b457d5d/src/Avalonia.Dialogs/Assets/Roboto-Light.ttf) carries this notice in its name table. The application's Segoe UI and Microsoft YaHei UI selections use fonts installed on Windows. |
| ANGLE 2.1.25547.20250602 | [ANGLE redistribution notice](ANGLE-LICENSE.txt) supplied with the native Windows package. |
| SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1, including Windows and Linux native assets | [Bindings license](SkiaSharp-HarfBuzzSharp-LICENSE.txt) and [third-party notices](SkiaSharp-HarfBuzzSharp-NOTICES.txt) from the NuGet distributions. The Win32 packages supply identical copies of these notices. |
| Svg.Controls.Avalonia 11.3.9.5, Svg.Model and ShimSkiaSharp 3.7.0 | [MIT](Svg-Skia-LICENSE.txt). The separately packaged Svg.Custom 3.7.0 uses the [Microsoft Public License](Svg-Custom-LICENSE.txt). [Upstream revision](https://github.com/wieslawsoltes/Svg.Skia/tree/fafe7e30be5f341d56c95f666bed8413f4fa76d6). |
| ExCSS 4.3.1 and MicroCom.Runtime 0.11.0 | [ExCSS MIT](ExCSS-LICENSE.txt) and [MicroCom MIT](MicroCom-LICENSE.txt). [ExCSS source](https://github.com/TylerBrinks/ExCSS/tree/c97e84d6126bb2e42658cf5af627d52e697c8779) · [MicroCom license](https://github.com/kekekeks/MicroCom/blob/cd2fa3c89525fbe7b46bd13f9ec8c77bd829e815/LICENSE). |
| Model Context Protocol C# SDK 2.4.0 | [Upstream license including its MIT/Apache-2.0 transition terms](MCP-LICENSE.txt) and [third-party notices](MCP-NOTICES.txt). [Upstream revision](https://github.com/modelcontextprotocol/csharp-sdk/tree/4cbf42ef107763f0bc75c358af8cb2dfe47a769a). |
| SQLitePCLRaw 2.1.13 and e_sqlite3 | Copyright 2014–2024 SourceGear, LLC. The package metadata specifies [Apache-2.0](Apache-2.0.txt). This distribution also includes the [upstream 2.x notices](SQLitePCLRaw-NOTICE.txt). The bundled e_sqlite3 contains [public-domain SQLite](https://sqlite.org/copyright.html). |
| Tomlyn 2.11.1 | [BSD-2-Clause](Tomlyn-LICENSE.txt). [Upstream revision](https://github.com/xoofx/Tomlyn/tree/aad5eb369cf6790d7dbf493aa7dd191b4dbd876b). |
| YamlDotNet 18.1.0 | [MIT](YamlDotNet-LICENSE.txt) and [libyaml license](libyaml-LICENSE.txt). [Upstream revision](https://github.com/aaubry/YamlDotNet/tree/748334a8fa7c227740018b284b71ad95cc6b7fc7). |
| Tmds.DBus.Protocol 0.21.3 (Linux desktop) | [MIT](Tmds.DBus-COPYING.txt). |
| Windows native launchers | [MinGW-w64 runtime notices](MinGW-w64-runtime-COPYING.txt), [GCC Runtime Library Exception](GCC-runtime-exception.txt) and [referenced GPL-3.0 text](GCC-GPL-3.txt). |
| Windows installer and uninstaller | Built with NSIS 3.11, Modern UI 2 and zlib compression. [NSIS notices](NSIS-COPYING.txt) and [Modern UI 2 license](NSIS-ModernUI-LICENSE.txt) are supplied from the build tool distribution. |
