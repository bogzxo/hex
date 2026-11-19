using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hex.Core;

/// <summary>Uses the installed Windows dictionaries without COM reflection or a managed package.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsSpelling
{
    private static readonly Guid FactoryClass = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private static readonly Guid FactoryInterface = new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    // COM vtable slots, including the three IUnknown slots. See spellcheck.h and objidl.h.
    private const int FactoryCreate = 5, CheckerCheck = 4, CheckerSuggest = 5;
    private const int ErrorNext = 3, ErrorStart = 3, ErrorLength = 4, ErrorAction = 5, StringNext = 3;
    private const int MaxIssues = 96, MaxSuggestions = 5;

    public static SpellingResult Check(string language, IReadOnlyList<SpellingLine> lines, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
            return new(null, []);
        // The worker's apartment and every native pointer are scoped to this synchronous call.
        int initialized = CoInitializeEx(0, 0);
        const int AlreadyInDifferentApartment = unchecked((int)0x80010106);
        if (initialized < 0 && initialized != AlreadyInDifferentApartment)
            return new(null, [], initialized);

        nint factory = 0, checker = 0;
        try
        {
            Guid clsid = FactoryClass, iid = FactoryInterface;
            int status = CoCreateInstance(&clsid, 0, 1, &iid, &factory);
            if (status != 0 || factory == 0)
                return new(null, [], status);

            string resolved = language.Length == 0 ? UserLanguage() : language;
            checker = CreateChecker(factory, resolved, out status);
            if (checker == 0 && language.Length == 0 && resolved != "en-US")
            {
                resolved = "en-US";
                checker = CreateChecker(factory, resolved, out status);
            }
            if (checker == 0)
                return new(null, [], status);

            var issues = new List<SpellingIssue>();
            var suggestions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (SpellingLine line in lines)
            {
                if (cancel.IsCancellationRequested || issues.Count >= MaxIssues)
                    break;
                if (!string.IsNullOrWhiteSpace(line.Text))
                    CheckLine(checker, line, issues, suggestions, cancel);
            }
            return new(resolved, issues);
        }
        finally
        {
            Release(checker);
            Release(factory);
            if (initialized >= 0)
                CoUninitialize();
        }
    }

    private static string UserLanguage()
    {
        const int capacity = 85; // LOCALE_NAME_MAX_LENGTH
        char* buffer = stackalloc char[capacity];
        int length = GetUserDefaultLocaleName(buffer, capacity);
        return length > 1 ? new string(buffer, 0, length - 1) : "en-US";
    }

    private static nint CreateChecker(nint factory, string language, out int status)
    {
        nint pointer = 0;
        fixed (char* text = language)
        {
            status = CallText(factory, FactoryCreate, text, &pointer);
            if (status == 0)
                return pointer;
        }
        Release(pointer);
        return 0;
    }

    private static void CheckLine(nint checker, SpellingLine line, List<SpellingIssue> issues,
        Dictionary<string, string[]> suggestions, CancellationToken cancel)
    {
        nint errors = 0;
        try
        {
            fixed (char* text = line.Text)
                if (CallText(checker, CheckerCheck, text, &errors) != 0 || errors == 0)
                    return;

            var next = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(errors, ErrorNext);
            while (issues.Count < MaxIssues && !cancel.IsCancellationRequested)
            {
                nint error = 0;
                try
                {
                    if (next(errors, &error) != 0 || error == 0)
                        break;
                    // Action 3 is a repeated-word deletion. This pass only marks misspellings.
                    if (!TryRead(error, ErrorAction, out uint action) || action is not (1 or 2)
                        || !TryRead(error, ErrorStart, out uint from) || !TryRead(error, ErrorLength, out uint count)
                        || from >= line.Text.Length || count == 0 || count > line.Text.Length - from)
                        continue;

                    string word = line.Text.Substring((int)from, (int)count);
                    if (string.IsNullOrWhiteSpace(word) || word.AsSpan().Trim().Length != word.Length)
                        continue;
                    if (!suggestions.TryGetValue(word, out string[]? replacements))
                        suggestions[word] = replacements = Suggest(checker, word);
                    issues.Add(new(line.Number, (int)from, (int)count, word, replacements));
                }
                finally { Release(error); }
            }
        }
        finally { Release(errors); }
    }

    private static string[] Suggest(nint checker, string word)
    {
        var found = new List<string>();
        nint enumerator = 0;
        try
        {
            fixed (char* text = word)
                if (CallText(checker, CheckerSuggest, text, &enumerator) != 0 || enumerator == 0)
                    return [];

            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(enumerator, StringNext);
            for (int i = 0; i < MaxSuggestions; i++)
            {
                nint pointer = 0;
                uint fetched = 0;
                try
                {
                    if (next(enumerator, 1, &pointer, &fetched) != 0 || fetched != 1 || pointer == 0)
                        break;
                    string suggestion = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                    if (suggestion.Length > 0 && suggestion != word && !found.Contains(suggestion))
                        found.Add(suggestion);
                }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        finally { Release(enumerator); }
        return [.. found];
    }

    private static bool TryRead(nint instance, int slot, out uint value)
    {
        uint result = 0;
        int status = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(instance, slot))(instance, &result);
        value = result;
        return status == 0;
    }

    private static int CallText(nint instance, int slot, char* text, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(instance, slot))(instance, text, result);

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* created);

    [LibraryImport("kernel32.dll")]
    private static partial int GetUserDefaultLocaleName(char* name, int capacity);
}
