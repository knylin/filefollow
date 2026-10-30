using System.Reflection;

// The one place the version lives: the app, the installer and the update check all read it.
// major.minor.patch: new features raise minor, fixes raise patch. GitHub release tags are "v" + this.
[assembly: AssemblyVersion(FileFollow.AppInfo.Version)]
[assembly: AssemblyFileVersion(FileFollow.AppInfo.Version)]
[assembly: AssemblyProduct("FileFollow")]

namespace FileFollow
{
    static class AppInfo
    {
        public const string Version = "1.3.1";
    }
}
