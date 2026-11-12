using System;

namespace ValheimHelper
{
    // What this build is: its version (from the project file) and its edition (legal, helper or full).
    internal static class BuildInfo
    {
#if EDITION_FULL
        internal const string Edition = "full";
#elif EDITION_HELPER
        internal const string Edition = "helper";
#else
        internal const string Edition = "legal";
#endif

        internal static readonly System.Version Version = typeof(BuildInfo).Assembly.GetName().Version ?? new System.Version(0, 0, 0);

        internal static string VersionText => Version.ToString(3);

        // The edition's name as the menu shows it.
        internal static string EditionName =>
            Edition == "full" ? Lang.T("Повна") : Edition == "helper" ? Lang.T("Помічник") : Lang.T("Легальна");
    }
}
