#:property TargetFramework=net10.0-windows
#:property AssemblyName=ProjectHub.Edge.AccessChecks
#:property PublishAot=false
#:project ../src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProjectHub.Edge.Desktop;
using Tomlyn;
using Tomlyn.Model;

internal static class WindowsManualCodex
{
    [STAThread]
    private static void Main(string[] args)
    {
        var scratch = Directory.CreateTempSubdirectory("projecthub-codex-manual-");
        try
        {
            var home = scratch.CreateSubdirectory("profile").FullName;
            foreach (var key in new[] { "USERPROFILE", "HOME" })
                Environment.SetEnvironmentVariable(key, home);
            foreach (var key in new[] { "CODEX_HOME", "LOCALAPPDATA", "APPDATA", "XDG_CONFIG_HOME", "PI_CODING_AGENT_DIR", "DSH_HOME" })
                Environment.SetEnvironmentVariable(key, Path.Combine(home, key));
            foreach (var key in new[] { "CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG", "OPENCODE_CONFIG_DIR", "OPENCODE_CONFIG_CONTENT" })
                Environment.SetEnvironmentVariable(key, null);
            var commands = scratch.CreateSubdirectory("bin").FullName;
            Environment.SetEnvironmentVariable("PATH", commands);
            File.WriteAllText(Path.Combine(commands, "claude.cmd"), "@exit /b 1");
            AppBuilder.Configure<App>().UseWin32().With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .UseSkia().SetupWithoutStarting();
            var client = new EdgeDesktopClient(Path.Combine(scratch.FullName, "state"));
            var harness = new HarnessAccess(client, () => false);
            var file = Path.Combine(home, "CODEX_HOME", "config.toml");
            var window = new Window { Width = 690, Height = 600, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
            try
            {
                window.Content = new AccessWindow(client, null, [], harness);
                window.Show();
                Wait(() => ActionButton() is { IsEffectivelyEnabled: true }, "首次手动接入按钮可用");
                Check(Status().Contains("手动接入") && Equals(ActionButton()!.Content, "接入"), "未检测到程序时说明和按钮");
                Save("manual-before");

                Invoke(Control<Button>("InstallDetectedHarnesses")!);
                Wait(() => Outcome().Contains("已就绪"), "一键接入完成");
                Check(File.Exists(Path.Combine(home, ".claude.json")) && !File.Exists(file), "批量写入已检测到的 Claude 并跳过 Codex");
                Invoke(ActionButton()!);
                Wait(() => Outcome().Contains("已接入当前用户的 Codex"), "单项接入成功");
                var config = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file))!;
                var entry = (TomlTable)((TomlTable)config["mcp_servers"])["projecthub"];
                Check(Equals(entry["command"], GlobalCodexAccess.CurrentCommand) && entry["args"] is TomlArray argsList &&
                    argsList.Cast<object>().SequenceEqual(new[] { "mcp-stdio-global", "--state", client.StateDirectory }), "写入当前设备的 MCP 配置");
                Check(UserSkills.Names.All(n => File.Exists(Path.Combine(UserSkills.Target(home, n), "SKILL.md"))), "安装全部 Skills");
                VerifyConnected();
                Save("manual-after");

                window.Content = new AccessWindow(client, null, [], harness);
                Wait(() => ActionButton()?.Content as string == "断开接入", "重开接入页读取已保存配置");
                VerifyConnected();
                Console.WriteLine("未定位 Codex 时，单项接入可用；批量跳过；手动接入写入当前设备配置和全部 Skills；重开状态正确。");
            }
            finally { window.Close(); }

            T? Control<T>(string id) where T : Control => window.GetVisualDescendants().OfType<T>()
                .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id);
            Button? ActionButton() => Control<Button>("HarnessInstall_codex");
            string Status() => Control<TextBlock>("HarnessStatus_codex")?.Text ?? "";
            string Outcome() => Control<TextBlock>("AccessResult")?.Text ?? "";
            void VerifyConnected() => Check(ActionButton() is { IsEffectivelyEnabled: true, Content: "断开接入" } &&
                Status().Contains("已接入") && Status().Contains("未检测到安装"), "没有启动程序时仍可断开已有接入，不误报已安装");
            void Wait(Func<bool> ready, string state)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    if (ready()) return;
                    Thread.Sleep(10);
                }
                throw new TimeoutException(state + "：" + Outcome());
            }
            void Save(string name)
            {
                if (args.Length == 0) return;
                var folder = Directory.CreateDirectory(args[0]);
                var scale = window.RenderScaling;
                using var capture = new RenderTargetBitmap(PixelSize.FromSize(window.ClientSize, scale), new Vector(96, 96) * scale);
                capture.Render(window);
                capture.Save(Path.Combine(folder.FullName, name + ".png"));
            }
        }
        finally { scratch.Delete(true); }
    }

    private static void Invoke(Button button)
    {
        Check(button.IsEffectivelyEnabled, "不能调用禁用状态的按钮");
        ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
    }

    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
    }
}
