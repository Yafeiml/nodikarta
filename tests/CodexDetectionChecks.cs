#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using ProjectHub.Edge.Desktop;

var temp = Directory.CreateTempSubdirectory("edge-codex-detection-");
try
{
    var home = temp.FullName;
    Dictionary<string, string[]> env = new(StringComparer.OrdinalIgnoreCase);
    bool Installed() => HarnessDetection.CodexFilesInstalled(home, key => env.GetValueOrDefault(key) ?? []);
    string Write(string relative, string text = "")
    {
        var file = Path.Combine(home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }
    void Native(string relative)
    {
        var file = Write(relative);
        Check(Installed(), relative);
        File.Delete(file);
        Check(!Installed(), "移除后 " + relative);
    }

    Write(".codex/config.toml", "[mcp_servers.projecthub]");
    Check(!Installed(), "单独留下用户配置");
    Native(@"AppData\Local\Programs\OpenAI\Codex\bin\codex.exe");
    env["CODEX_INSTALL_DIR"] = [Path.Combine(home, "custom native")];
    Native(@"custom native\codex.exe");
    env["CODEX_HOME"] = [Path.Combine(home, "custom profile")];
    Native(@"custom profile\packages\standalone\current\bin\codex.exe");
    Native(@"custom profile\packages\standalone\current\codex.exe");
    env["PATH"] = ["relative-path", $"\"{Path.Combine(home, "later PATH")}\""];
    Native(@"later PATH\codex.exe");
    foreach (var suffix in new[] { ".bat", ".cmd", ".ps1" }) Native(@"later PATH\codex" + suffix);

    env["npm_config_prefix"] = [Path.Combine(home, "shared npm")];
    env["PATH"] = [..env["PATH"], Path.Combine(home, "shared npm")];
    foreach (var prefix in new[] { "shared npm", @"AppData\Roaming\npm" })
    {
        var shim = Write(Path.Combine(prefix, "codex.cmd"), "@exit /b 1");
        Check(!Installed(), "孤立 npm 启动器");
        Write(Path.Combine(prefix, @"node_modules\@openai\codex\package.json"), """{"name":"@openai/codex"}""");
        var cli = Write(Path.Combine(prefix, @"node_modules\@openai\codex\bin\codex.js"));
        Check(Installed(), prefix);
        File.Delete(cli);
        File.Delete(shim);
    }

    const string extensions = ".vscode/extensions";
    const string folder = "openai.chatgpt-26.51117.41037-win32-x64";
    var binary = Write($"{extensions}/{folder}/bin/windows-x86_64/codex.exe");
    Check(!Installed(), "未注册的扩展残留");
    var entry = $$"""[{"identifier":{"id":"openai.chatgpt"},"relativeLocation":"{{folder}}"}]""";
    Write($"{extensions}/extensions.json", entry.Replace("openai.chatgpt\"", "example.other\""));
    Check(!Installed(), "其他作者的扩展记录");
    Write($"{extensions}/extensions.json", entry);
    Check(Installed(), "带本地 Windows 后端的已注册扩展");
    File.Delete(binary);
    Write($"{extensions}/{folder}/bin/linux-x86_64/codex");
    Check(!Installed(), "只有 Linux 程序的扩展");
    Write($"{extensions}/extensions.json", "{");
    Check(!Installed(), "临时不可读的扩展列表");

    Check(!HarnessDetection.PackageRegistered("ProjectHub.TestNonexistent_2p2nqsd0c76g0"), "不存在的 MSIX 包");
    Console.WriteLine($"Codex 安装来源检查通过。真实当前用户的桌面包登记：{HarnessDetection.PackageRegistered("OpenAI.Codex_2p2nqsd0c76g0")}。");
}
finally { temp.Delete(true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
