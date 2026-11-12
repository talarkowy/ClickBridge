using ClickBridge.Hosting;
using System.Text;

namespace ClickBridge.App;

internal static class ConsoleZwiftSignIn
{
    public static async Task SignInAsync(ZwiftAccount account)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("--zwift-login requires an interactive console.");
        }

        Console.WriteLine("Sign in to Zwift to enable Click v2 unlocks. Credentials are sent to secure.zwift.com.");
        Console.Write("Zwift email: ");
        var email = Console.ReadLine()?.Trim();
        Console.Write("Zwift password (hidden, Esc cancels): ");
        var password = ReadPassword();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Zwift sign-in cancelled. Both email and password are required.");
        }

        await account.SignInAsync(email, password, CancellationToken.None).ConfigureAwait(false);
    }

    private static string? ReadPassword()
    {
        var password = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return password.ToString();
                    case ConsoleKey.Escape:
                        return null;
                    case ConsoleKey.Backspace when password.Length > 0:
                        password.Length--;
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            password.Append(key.KeyChar);
                        }
                        break;
                }
            }
        }
        finally
        {
            Console.WriteLine();
        }
    }
}
