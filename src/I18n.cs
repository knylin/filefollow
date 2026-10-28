// UI language: lang=zh/en in settings wins; otherwise Chinese on Chinese Windows, English elsewhere
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileFollow
{
    static class L
    {
        public static readonly bool En = Detect();

        public static string T(string zh, string en) { return En ? en : zh; }

        static bool Detect()
        {
            string env = Environment.GetEnvironmentVariable("FILEFOLLOW_LANG");
            if (env == "en") return true;
            if (env == "zh") return false;
            try
            {
                string p =Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileFollow", "settings.txt");
                if (File.Exists(p))
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        if (line == "lang=en") return true;
                        if (line == "lang=zh") return false;
                    }
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh";
        }
    }
}
