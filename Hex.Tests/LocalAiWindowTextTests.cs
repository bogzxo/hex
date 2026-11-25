using System.Text.RegularExpressions;
using Hex.Core;

namespace Hex.Tests;

/// <summary>The Local AI window's texts: every key its sources use is in en-gb, and a setup's changes read as words, never as keys.</summary>
public sealed partial class LocalAiWindowTextTests : IDisposable
{
    private const long GIB = 1L << 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("hex-aitext-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string HexRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // section name -> keys, read from en-gb.hor's top-level objects
    private static Dictionary<string, HashSet<string>> Sections()
    {
        string text = File.ReadAllText(Path.Combine(HexRoot, "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor")).ReplaceLineEndings("\n");
        var sections = new Dictionary<string, HashSet<string>>();
        foreach (Match section in SectionRegex().Matches(text))
        {
            int end = text.IndexOf("\n},", section.Index, StringComparison.Ordinal);
            var keys = new HashSet<string>();
            foreach (Match key in KeyRegex().Matches(text[section.Index..end]))
                keys.Add(key.Groups[1].Value);
            sections[section.Groups[1].Value] = keys;
        }
        return sections;
    }

    [GeneratedRegex(@"^(\w+): \{", RegexOptions.Multiline)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\s*(\w+):\s*""", RegexOptions.Multiline)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"""((?:local_ai|local_ai_setups|local_ai_profiles|local_ai_help|local_ai_picker)\.[a-z0-9_]*)""")]
    private static partial Regex LiteralRegex();

    private static bool Exists(Dictionary<string, HashSet<string>> sections, string key)
    {
        int dot = key.IndexOf('.');
        string name = key[(dot + 1)..];
        if (!sections.TryGetValue(key[..dot], out HashSet<string>? keys))
            return false;
        // A literal ending in _ or . is the start of a key built at run time
        return name.Length == 0 || name.EndsWith('_') ? keys.Any(each => each.StartsWith(name, StringComparison.Ordinal)) : keys.Contains(name);
    }

    [Fact]
    public void Every_text_key_the_local_ai_window_uses_is_in_en_gb()
    {
        Dictionary<string, HashSet<string>> sections = Sections();
        string[] files = Directory.GetFiles(Path.Combine(HexRoot, "Hex.Dawn", "LocalAi"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var missing = new List<string>();
        foreach (string file in files)
            foreach (Match literal in LiteralRegex().Matches(File.ReadAllText(file)))
                if (!Exists(sections, literal.Groups[1].Value))
                    missing.Add(Path.GetFileName(file) + ": " + literal.Groups[1].Value);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_the_setups_name_is_in_en_gb()
    {
        Dictionary<string, HashSet<string>> sections = Sections();
        var missing = new List<string>();
        foreach (LocalAiSetup setup in LocalAiSetups.All)
            foreach (string key in new[] { setup.TitleKey, setup.PitchKey, setup.TradeOffKey }.Concat(setup.GetKeys))
                if (!Exists(sections, key))
                    missing.Add(key);
        Assert.Empty(missing);
    }

    [Fact]
    public void Changes_read_as_words_in_every_kind()
    {
        Dictionary<string, HashSet<string>> sections = Sections();
        var library = new LocalModelLibrary(() => [_dir], Path.Combine(_dir, "cache.json"), null, () => 12 * GIB, () => null) { OllamaEnabled = false };
        library.RefreshNow();
        var asked = new List<string>();
        string Said(string key) { asked.Add(key); return "<" + key + ">"; }

        var changes = new List<LocalAiSetupChange>();
        foreach (LocalAiSetup setup in LocalAiSetups.All)
        {
            // Nothing set up, then every kind of difference by hand: models, profiles, behaviour, engine
            var fresh = new HexSettings(null);
            changes.AddRange(LocalAiSetups.ChangesOf(setup, fresh, library));
            LocalAiSetups.Apply(setup, fresh, library);
            LocalModelPresets.UseProfile(fresh, "catalog:" + setup.Predict.CatalogId, setup.Predict.ProfileId == "precise" ? "recommended" : "precise");
            fresh.EditPredictionsInvocation = fresh.EditPredictionsInvocation == "shortcut" ? "after_typing" : "shortcut";
            fresh.EditPredictionsMode = fresh.EditPredictionsMode == "subtle" ? "eager" : "subtle";
            fresh.EditPredictionsRenames = !fresh.EditPredictionsRenames;
            fresh.EditPredictionsFar = !fresh.EditPredictionsFar;
            fresh.LlamaCppAutostart = !fresh.LlamaCppAutostart;
            fresh.LocalAiEngine = HexSettings.LOCAL_AI_OLLAMA;
            changes.AddRange(LocalAiSetups.ChangesOf(setup, fresh, library));
        }
        Assert.Contains(changes, change => change.WhatKey == "local_ai_setups.change_engine");
        Assert.Contains(changes, change => change.WhatKey.EndsWith("_profile", StringComparison.Ordinal));

        foreach (LocalAiSetupChange change in changes)
        {
            string line = LocalAiChangeText.Line(change, Said) + LocalAiChangeText.ReversedLine(change, Said);
            // Nothing raw is shown: the values went through the texts, or they are model names
            Assert.DoesNotContain("local_ai_profiles.", line.Replace("<local_ai_profiles.", ""));
            Assert.DoesNotContain("local_ai_setups.", line.Replace("<local_ai_setups.", ""));
        }
        foreach (string key in asked.Distinct())
            Assert.True(Exists(sections, key), key + " is in en-gb");
    }

    [Fact]
    public void Profile_values_become_their_titles()
    {
        string Said(string key) => "<" + key + ">";
        var change = new LocalAiSetupChange("local_ai_setups.change_predict_profile", "local_ai_setups.profile_changed", "local_ai_profiles.recommended_title");
        Assert.Equal("<local_ai_setups.change_predict_profile>: <local_ai_setups.profile_changed> → <local_ai_profiles.recommended_title>", LocalAiChangeText.Line(change, Said));
        var behaviour = new LocalAiSetupChange("local_ai_setups.change_renames", "off", "on");
        Assert.Equal("<local_ai_setups.change_renames>: <local_ai.off> → <local_ai.on>", LocalAiChangeText.Line(behaviour, Said));
        var invocation = new LocalAiSetupChange("local_ai_setups.change_invocation", "shortcut", "after_typing");
        Assert.Equal("<local_ai_setups.change_invocation>: <local_ai.invocation_shortcut> → <local_ai.invocation_after_typing>", LocalAiChangeText.Line(invocation, Said));
    }
}
