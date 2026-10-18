namespace LicensePlatform.Web.Security;

public static class ConsoleSecretReader
{
    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var characters = new List<char>();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }

        Console.WriteLine();
        return new string(characters.ToArray());
    }
}
