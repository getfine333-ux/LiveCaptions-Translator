// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
namespace LiveCaptionsTranslator.utils
{
    // Modified fork: upstream attribution is not this build's update channel.
    // Configure a maintained fork release channel before re-enabling checks.
    public static class UpdateUtil
    {
        public const string GitHubRepoUrl = "https://github.com/SakiRinn/LiveCaptions-Translator";
        public const string GitHubReleasesUrl = "https://github.com/SakiRinn/LiveCaptions-Translator/releases";
        public static Task<string> GetLatestVersion() => Task.FromResult(string.Empty);
    }
}
