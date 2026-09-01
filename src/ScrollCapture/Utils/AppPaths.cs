using System.IO;

namespace ScrollCapture.Utils;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScrollCapture");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultSaveDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "ScrollCapture");
            return dir;
        }
    }
}
