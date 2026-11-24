# Third-party notices

Nodicarta's original source and Skills are distributed under the [MIT License](LICENSE). Dependencies retain their upstream terms. Product names identifying compatible AI clients belong to their respective owners.

The [Edge distribution inventory](release/licenses/edge/INVENTORY.md) identifies bundled runtime packages, native libraries, the embedded Roboto Light font, the launcher and the installer. It links to the license texts and upstream notices included with Edge. [Exact Windows package versions](release/licenses/edge/package-versions.txt).

| Component | Where it is used | Upstream licensing information |
| --- | --- | --- |
| .NET and ASP.NET Core | Core and native Edge applications | [Runtime license and third-party notices](https://github.com/dotnet/runtime), also distributed with the runtime |
| Npgsql | Core PostgreSQL driver | [PostgreSQL License](release/licenses/Npgsql-LICENSE.txt) |
| Avalonia | Windows and Linux desktop interfaces | [Avalonia license](release/licenses/edge/Avalonia-LICENSE.txt) and [notices](release/licenses/edge/Avalonia-NOTICE.md) |
| SkiaSharp and SVG rendering | Desktop graphics and previews | [SkiaSharp and HarfBuzzSharp notices](release/licenses/edge/SkiaSharp-HarfBuzzSharp-NOTICES.txt), [Svg.Skia license](release/licenses/edge/Svg-Skia-LICENSE.txt), [Svg.Custom license](release/licenses/edge/Svg-Custom-LICENSE.txt) |
| Model Context Protocol C# SDK | Edge MCP server | [Upstream licensing transition and license texts](release/licenses/edge/MCP-LICENSE.txt) |
| Microsoft.Data.Sqlite and SQLitePCLRaw | Edge local storage | [EF Core](https://github.com/dotnet/efcore), [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) |
| Tomlyn and YamlDotNet | Agent configuration formats | [Tomlyn](https://github.com/xoofx/Tomlyn), [YamlDotNet](https://github.com/aaubry/YamlDotNet) |

PostgreSQL runs as a separate database service and uses its [own license](https://www.postgresql.org/about/licence/). Runtime packages and operating-system components inside container base images retain their respective license and notice files.
