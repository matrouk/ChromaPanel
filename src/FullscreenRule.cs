using System;

namespace ChromaPanel
{
    /// <summary>
    /// Which fullscreen apps desktop sharpening keeps working over. Protected (DRM) video is blanked
    /// in screen capture, so sharpening over it shows black - and in practice DRM video plays in
    /// browsers and Store apps. Everything else, games and local video players included, is safe.
    /// </summary>
    internal static class FullscreenRule
    {
        // Process names without ".exe", compared case-insensitively.
        static readonly string[] DrmHosts =
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen",
            "waterfox", "librewolf", "floorp", "iexplore",
            "msedgewebview2",                       // apps built on Edge WebView2
            "applicationframehost", "wwahost"       // Store apps: Netflix, Disney+, Prime Video, Films & TV
        };

        /// <summary>Should sharpening step aside for this fullscreen window?</summary>
        public static bool PausesFor(string processName, string windowClass)
        {
            if (windowClass == "ApplicationFrameWindow" || windowClass == "Windows.UI.Core.CoreWindow") return true;
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (string h in DrmHosts)
                if (string.Equals(processName, h, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
