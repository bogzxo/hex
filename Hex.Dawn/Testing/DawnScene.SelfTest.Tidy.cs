namespace Hex.Dawn;

// Between the parts of the self test, what one left behind that would get in the way of the next. A file of the test
// typed into and not saved asked about itself the moment anything closed it, and that question sat over every part that
// ran after it, every click landing on it, so a part passed on its own and failed after the wrong one.
internal sealed partial class DawnScene
{
    private void TidyAfterSuite()
    {
        chrome.Dialog?.Close();

        // The test's own files that were typed into, closed without asking, whatever was typed in them is the test's
        foreach (HexDocument open in documents.ToList())
        {
            if (open.Modified && open.Path is { } path && PlatformPaths.IsUnder(path, testFolder))
            {
                Show(open);
                CloseNow();
            }
        }
    }
}
