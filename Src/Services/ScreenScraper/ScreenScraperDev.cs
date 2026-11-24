using System.Text;

namespace RetroGcCreator.Services.ScreenScraper;

/// <summary>
/// The ScreenScraper developer login built into the app. The data comes from <c>ScreenScraperDev.g.cs</c>,
/// generated in <c>obj/</c> at build time from the <c>SCREENSCRAPER_DEVID</c> / <c>SCREENSCRAPER_DEVPASSWORD</c>
/// environment variables (or the <c>SsDevId</c> / <c>SsDevPassword</c> MSBuild properties): it is never committed.
/// The bytes are XOR-ed so they don't show up as plain text in the executable. That is only obfuscation,
/// not protection: anything shipped in an app can be read back.
/// </summary>
internal static partial class ScreenScraperDev
{
    private static string? _id;
    private static string? _password;

    // Decoded on first use: by then every static field of the generated part is initialised.
    public static string Id => _id ??= Decode(IdData);
    public static string Password => _password ??= Decode(PasswordData);

    private static string Decode(byte[] data)
    {
        var bytes = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            bytes[i] = (byte)(data[i] ^ Key[i % Key.Length]);
        return Encoding.UTF8.GetString(bytes);
    }
}
