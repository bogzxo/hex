using System.Text;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // Native project readers can briefly deny replacement on Windows. Fixture edits must either finish or fail,
    // without truncating XML or letting a previous SDK task stand in for a run whose setup failed.
    private static void WriteSelfTestProject(string path, string text)
    {
        for (int retry = 0; ; retry++)
        {
            try { DocumentFile.Write(path, text, new UTF8Encoding(false)); return; }
            catch (IOException) when (retry < 50) { Thread.Sleep(20); }
        }
    }
}
