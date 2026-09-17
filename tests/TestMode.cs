// Local recordings and model weights are never prerequisites for public CI.
internal static class TestMode
{
    public static bool IncludeLocalFixtures { get; set; }
}
