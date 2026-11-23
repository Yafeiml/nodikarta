using System.Text;

namespace ProjectHub.Core;

internal static class PasswordReset
{
    public static async Task<int> RunAsync(CoreStore store)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("请在 Core 容器的交互式终端中重设密码。");
            return 2;
        }
        if (!await store.IsInstalledAsync())
        {
            Console.Error.WriteLine("尚未创建管理员，请先完成网页首次设置。");
            return 1;
        }
        var password = ReadHidden("新密码（12–128 个字符）：");
        if (!CoreStore.IsValidPassword(password))
        {
            Console.Error.WriteLine("密码长度无效或输入已取消。");
            return 2;
        }
        if (password != ReadHidden("确认新密码："))
        {
            Console.Error.WriteLine("两次输入不匹配，原密码仍然有效。");
            return 2;
        }
        if (!await store.ResetPasswordAsync(password!))
        {
            Console.Error.WriteLine("管理员密码未能重设。");
            return 1;
        }
        Console.WriteLine("管理员密码已重设，旧管理会话已失效。");
        return 0;
    }

    private static string? ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.Escape || key.KeyChar is '\u0003' or '\u0004')
            {
                Console.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
