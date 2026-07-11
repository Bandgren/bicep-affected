namespace BicepAffected.Tests;

internal static class FixturePaths
{
    public static string Root(string fixtureName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
    }
}
