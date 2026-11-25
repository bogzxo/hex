using System.Globalization;
using System.Numerics;

using Horizon.Input;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window (docs/local-ai-window-plan.md): a movable, resizable WindowPanel like Settings. Its header has the
// Simple | Advanced switch, a status chip and (in Advanced) a search; Simple is one page, Advanced has a left nav of pages. Each
// page is drawn again whenever something it shows moves, never while a box in it is being typed into.
internal sealed partial class DawnScene
{
    private const float LOCAL_AI_NAV = 168.0f, LOCAL_AI_HEAD = 46.0f;

    private WindowPanel? localAiWindow;
    private StackPanel? localAiNav, localAiColumn, localAiHeader;
    private ScrollPanel? localAiScroll;
    private Button? localAiSimpleTab, localAiAdvancedTab;
    private Badge? localAiChip;
    private TextBox? localAiSearch;
    private LocalAiPage localAiPage;
    private string localAiQuery = string.Empty;
    private string? localAiOpenHelp;
    private bool localAiShowLog, localAiGallery, localAiDirty;

    // Where the window was and how big, for this run
    private (Vector2 Position, Vector2 Size)? localAiRemembered;

    // The mode the window shows: what the settings chose, else what the person's settings suggest
    private string LocalAiMode => LocalAiWindowState.ParseMode(settings.LocalAiUiModeInForce);

    private bool LocalAiAdvanced => LocalAiMode == LocalAiWindowState.ADVANCED;

    /// <summary>The Local AI window, at the page it was left on (the setup gallery when nothing is set up yet).</summary>
    internal void OpenLocalAiWindow() => ShowLocalAi(null);

    /// <summary>The Local AI window at a page (overview, setups, models, presets, predictions, engine, privacy, diagnostics or glossary).</summary>
    internal void OpenLocalAiWindow(string? page) => ShowLocalAi(page is null ? null : LocalAiWindowState.ParsePage(page));

    /// <summary>Set Up Local AI…: the window at the setup gallery.</summary>
    internal void OpenLocalAiSetup()
    {
        localAiGallery = true;
        ShowLocalAi(LocalAiPage.Setups);
    }

    /// <summary>The old name of Set Up llama.cpp's command; the window's gallery is the first run now.</summary>
    internal void OpenLlamaSetup() => OpenLocalAiWindow();

    private void ShowLocalAi(LocalAiPage? page)
    {
        if (HexAi.Off)
            return;
        if (page is { } wanted)
        {
            // An entry point that asks for a page starts it unfiltered
            localAiPage = wanted;
            localAiQuery = string.Empty;
            if (localAiSearch is not null)
                localAiSearch.Text = string.Empty;
        }
        else if (LocalAiSetupFlow.NeedsFirstRun(settings))
            localAiPage = LocalAiPage.Setups;
        // A window that Escape (or anything else) put away without its OnClosed running is closed all the same
        if (localAiWindow is { } stale && !LocalAiWindowState.IsOpen(true, stale.Visible, stale.Parent is not null))
            LocalAiWindowClosed(stale);
        if (localAiWindow is { } open)
        {
            open.BringToFront();
            open.TakeKeyboard();
            RefreshLlamaSetup();
            return;
        }

        Vector2 screen = chrome.Root.Bounds.Size;
        Vector2 most = Vector2.Max(screen - new Vector2(30, 60), new Vector2(360, 300));
        var window = new WindowPanel(Said("local_ai.title"))
        {
            Icon = "settings-gear",
            Size = Vector2.Min(localAiRemembered?.Size ?? new Vector2(960, 680), most),
            MinSize = Vector2.Min(new Vector2(720, 520), most)
        };
        if (localAiRemembered is { } before)
            window.Position = before.Position;
        DressWindow(window);
        window.Glass = null;
        window.BeforeEscape = () =>
        {
            // Escape lets go of a box being typed into first, and closes the window when nothing is edited
            if (compositor.Focus is TextBox && window.Holds(compositor.Focus))
            {
                window.TakeKeyboard();
                return true;
            }
            return false;
        };
        window.OnClosed = () => LocalAiWindowClosed(window);
        window.AddTitleAction("info", Said("local_ai.help_tip"), ShowLocalAiHelp);

        // The page is added first, the nav and the header after it, so they are above the page's hit area (which the padding only moves visually)
        localAiScroll = window.Add(new ScrollPanel { Fill = UIFill.Both, Padding = new UIEdges(18, LOCAL_AI_HEAD + 6, 12, 16) });
        localAiColumn = localAiScroll.Add(new StackPanel { Spacing = 8.0f, Fill = UIFill.Horizontal });
        localAiNav = window.Add(new StackPanel
        {
            Spacing = 4, Size = new Vector2(LOCAL_AI_NAV, 0), Fill = UIFill.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Padding = new UIEdges(10, LOCAL_AI_HEAD + 8, 6, 10)
        });
        BuildLocalAiHeader(window);

        localAiWindow = window;
        chrome.Root.Add(window);
        LocalModelLibrary.Shared.Changed += LocalAiLibraryChanged;
        _ = LocalModelLibrary.Shared.Refresh();
        AskLlamaMachine();
        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA || LocalAiSetupFlow.NeedsFirstRun(settings))
            AskOllama();
        RefreshLlamaSetup();
        window.TakeKeyboard();
    }

    // The one clean-up every way of closing the window ends in (its x, Escape, the code), safe to run twice
    private void LocalAiWindowClosed(WindowPanel window)
    {
        localAiRemembered = (window.Position, window.Size);
        window.Parent?.Remove(window);
        if (localAiWindow != window)
            return;
        LocalModelLibrary.Shared.Changed -= LocalAiLibraryChanged;
        localAiWindow = null;
        localAiNav = localAiColumn = localAiHeader = null;
        localAiScroll = null;
        localAiSimpleTab = localAiAdvancedTab = null;
        localAiChip = null;
        localAiSearch = null;
        llamaProgress.Clear();
        llamaServerText = llamaLogText = localAiVramText = localAiLoadedText = null;
        localAiShown = null;
        localAiOpenHelp = null;
        localAiQuery = string.Empty;
        localAiScrollByPage.Clear();
        localAiScrollKey = null;
        // What the polls and the tick kept for the window is let go with it, so a closed window holds nothing
        localAiRouter = null;
        localAiLoaded = null;
        localAiVram = null;
        localAiScrollTo = null;
        localAiScrollPending = false;
        (llamaLogTail, llamaStateWords, llamaLogTailOf) = (null, null, null);
        localAiGpuAt = localAiVramAt = 0;
    }

    // The scroll offset each page was left at, so a page keeps its place and a new page starts at the top
    private readonly Dictionary<string, float> localAiScrollByPage = [];
    private string? localAiScrollKey;

    // The mode switch, the status chip and the search, made once and updated in place so a box being typed into is never replaced
    private void BuildLocalAiHeader(WindowPanel window)
    {
        localAiHeader = window.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, LOCAL_AI_HEAD),
            Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Padding = new UIEdges(14, 10, 14, 4)
        });
        localAiSimpleTab = localAiHeader.Add(LinkButton(Said("local_ai.mode_simple"), string.Empty, () => SetLocalAiMode(LocalAiWindowState.SIMPLE)));
        localAiSimpleTab.Tooltip = Said("local_ai.mode_simple_help");
        localAiAdvancedTab = localAiHeader.Add(LinkButton(Said("local_ai.mode_advanced"), string.Empty, () => SetLocalAiMode(LocalAiWindowState.ADVANCED)));
        localAiAdvancedTab.Tooltip = Said("local_ai.mode_advanced_help");
        localAiChip = localAiHeader.Add(LocalAiBadge(string.Empty, UIBadgeTone.Neutral));
        localAiSearch = localAiHeader.Add(new TextBox { Placeholder = Said("local_ai.search"), Padding = new UIEdges(10.0f, 6.0f, 10.0f, 6.0f), Size = new Vector2(260, 34), Anchor = Origin.Left, Pivot = Origin.Left });
        localAiSearch.OnChanged = LocalAiSearchChanged;
        localAiSearch.Tooltip = Said("local_ai.search_help");
    }

    // The mode written to the settings, which the window and the Settings row both show
    private void SetLocalAiMode(string mode)
    {
        if (LocalAiMode == mode)
            return;
        settings.LocalAiUiMode = mode;
        SaveSettings();
        SettingsPage?.Refresh("local_ai_ui_mode");
        localAiOpenHelp = null;
        if (mode == LocalAiWindowState.SIMPLE)
        {
            localAiQuery = string.Empty;
            if (localAiSearch is not null)
                localAiSearch.Text = string.Empty;
        }
        RefreshLlamaSetup();
    }

    private void ShowLocalAiHelp() =>
        UIDialog.Show(chrome, Said("local_ai.help_title"), Said("local_ai.help_text"),
            new DialogChoice(Said("local_ai.help_glossary"), () =>
            {
                SetLocalAiMode(LocalAiWindowState.ADVANCED);
                localAiPage = LocalAiPage.Glossary;
                RefreshLlamaSetup();
            }),
            new DialogChoice(Said("local_ai.help_close")));

    // The status chip in the header: a word or two, coloured by how things stand
    private void UpdateLocalAiHeader()
    {
        bool advanced = LocalAiAdvanced;
        if (localAiSimpleTab is { } simple)
            simple.Selected = !advanced;
        if (localAiAdvancedTab is { } adv)
            adv.Selected = advanced;
        if (localAiSearch is { } search)
            search.Visible = advanced;
        if (localAiChip is { } chip)
        {
            (string text, UIBadgeTone tone) = LocalAiChipWords();
            if (chip.Text != text)
                chip.Text = text;
            chip.Tone = tone;
        }
    }

    private (string Text, UIBadgeTone Tone) LocalAiChipWords()
    {
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        string model = roles.Predict?.Name ?? roles.Chat?.Name ?? string.Empty;
        return LocalAiStatus() switch
        {
            LocalAiStatusKind.Downloading => (Said("local_ai.chip_downloading", ("percent", Math.Max(0, LocalAiDownloadPercent()).ToString(CultureInfo.InvariantCulture))), UIBadgeTone.Info),
            // Running with predictions switched off says so rather than naming a model that isn't being used
            LocalAiStatusKind.Running or LocalAiStatusKind.Outside => settings.EditPredictions || roles.Predict is null
                ? (Said("local_ai.chip_running", ("model", model)), UIBadgeTone.Success)
                : (Said("local_ai.chip_predictions_off"), UIBadgeTone.Neutral),
            LocalAiStatusKind.Starting => (Said("local_ai.chip_starting"), UIBadgeTone.Info),
            LocalAiStatusKind.Problem => (Said("local_ai.chip_problem"), UIBadgeTone.Danger),
            LocalAiStatusKind.Stopped => (Said("local_ai.chip_stopped"), UIBadgeTone.Neutral),
            _ => (Said("local_ai.chip_not_set_up"), UIBadgeTone.Warning),
        };
    }

    // Where the engine stands, as the status sentence's kind
    private LocalAiStatusKind LocalAiStatus()
    {
        bool ollama = settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA;
        LlamaCppServer? server = llamaServer;
        bool downloading = Context.Work.Find(LLAMA_BUILD_WORK) is not null || AnyModelWork();
        bool setUp = !LocalAiSetupFlow.NeedsFirstRun(settings);
        bool running = ollama ? localAiOllamaUp == true : server?.State == LlamaCppServerState.Ready;
        bool starting = !ollama && server?.State == LlamaCppServerState.Starting;
        bool problem = ollama ? localAiOllamaUp == false : server?.State == LlamaCppServerState.Failed;
        bool outside = !ollama && server is { State: LlamaCppServerState.Ready, Adopted: true };
        return LocalAiWindowState.StatusOf(setUp, downloading, running, starting, problem, outside);
    }

    // Whole percents of the first download going, -1 when none
    private int LocalAiDownloadPercent()
    {
        if (Context.Work.Find(LLAMA_BUILD_WORK) is { } build)
            return LocalAiWindowState.PercentBucket(build.Progress);
        foreach (LlamaCppModel model in LlamaCppModels.All)
        {
            if (Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is { } going)
                return LocalAiWindowState.PercentBucket(going.Progress);
        }
        return -1;
    }

    // The library says it changed from the thread that scanned
    private void LocalAiLibraryChanged() => PostLink(() => RefreshLlamaSetup(onlyIfChanged: true));

    // What the window shows now, as a key: events (the library scanned, a server answered) draw only when it differs from what's drawn
    private string? localAiShown;
    // The two roles' ids as drawn: compared as they are each frame (no key built), so one assigned from outside draws the window again
    private string? localAiPredictDrawn, localAiChatDrawn;
    private Label? localAiLoadedText;
    private List<string>? localAiLoaded;
    private List<LlamaCppRouterModel>? localAiRouter;

    private string LocalAiShownNow()
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        LlamaCppServer? server = llamaServer;
        return LocalAiWindowState.Signature(LocalAiMode, localAiPage, settings.LocalAiSetup, localAiQuery, localAiOpenHelp, LocalAiDownloadPercent(),
            localAiShowLog, localAiGallery, localAiSelected, LocalAiRunKey(),
            library.Folders, library.Models.Select(model => $"{model.Id}|{model.Fit}|{model.Source}|{model.Caps}|{model.Bytes}"),
            settings.LocalAiPredictModel, settings.LocalAiChatModel, settings.LocalAiEngine, settings.LlamaCppServer, settings.EditPredictionsApiUrl, settings.LocalAiPresets,
            settings.EditPredictions, settings.EditPredictionsInvocation, settings.EditPredictionsMode, settings.EditPredictionsLinkChat, settings.EditPredictionsShare, settings.Link,
            llamaPlatform is not null, llamaRelease?.Tag, llamaBackend, llamaReleaseProblem, llamaAsking, llamaMeasuring, llamaSpeeds.Count, llamaWorkProblem, localAiOllamaUp,
            localAiRouter is null ? "-" : string.Join(",", localAiRouter.Select(model => model.Id + ":" + model.State)),
            server?.State, server?.Adopted, server?.Config?.Alias, server?.Problem,
            Context.Work.Find(LLAMA_BUILD_WORK) is not null, LlamaCppModels.All.Where(model => Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is not null).Select(model => model.Id));
    }

    // Whether a box of the window's page has the keyboard: drawing the page again would take it away
    private bool LocalAiEditing() =>
        localAiWindow is { } window && compositor.Focus is TextBox box && window.Holds(box) && box != localAiSearch;

    /// <summary>Draws the window's page again, when the window is open; with <paramref name="onlyIfChanged"/> only when what it shows has moved, and never while a box is typed into.</summary>
    private void RefreshLlamaSetup(bool onlyIfChanged = false)
    {
        if (localAiWindow is null || localAiColumn is not { } body)
            return;
        string shown = LocalAiShownNow();
        if (onlyIfChanged && shown == localAiShown)
        {
            TickLlamaSetup();
            return;
        }
        if (onlyIfChanged && LocalAiEditing())
        {
            // Said again once the box lets go, so the page isn't left old
            localAiDirty = true;
            TickLlamaSetup();
            return;
        }
        localAiDirty = false;
        localAiShown = shown;
        (localAiPredictDrawn, localAiChatDrawn) = (settings.LocalAiPredictModel, settings.LocalAiChatModel);
        UpdateLocalAiHeader();
        DrawLocalAiNav();
        // A page keeps its own scroll place; arriving at another starts at its own (the top, the first time)
        string scrollKey = LocalAiAdvanced ? localAiPage.ToString() : "simple";
        if (localAiScrollKey != scrollKey && localAiScroll is { } scroller)
        {
            if (localAiScrollKey is not null)
                localAiScrollByPage[localAiScrollKey] = scroller.Offset;
            localAiScrollKey = scrollKey;
            scroller.Offset = localAiScrollByPage.GetValueOrDefault(scrollKey);
        }
        ClearRows(body);
        llamaProgress.Clear();
        llamaServerText = llamaLogText = localAiVramText = localAiLoadedText = null;

        if (!LocalAiAdvanced)
            DrawLocalAiSimple(body);
        else
        {
            if (LocalAiSearching)
                body.Add(LinkLine(Said("local_ai.searching", ("query", localAiQuery))));
            switch (localAiPage)
            {
                case LocalAiPage.Setups: DrawLocalAiSetups(body, detailed: true); break;
                case LocalAiPage.Models: DrawLocalAiModels(body); break;
                case LocalAiPage.Presets: DrawLocalAiPresets(body); break;
                case LocalAiPage.Predictions: DrawLocalAiPredictions(body); break;
                case LocalAiPage.Engine: DrawLocalAiEngine(body); break;
                case LocalAiPage.Privacy: DrawLocalAiPrivacy(body); break;
                case LocalAiPage.Diagnostics: DrawLocalAiDiagnostics(body); break;
                case LocalAiPage.Glossary: DrawLocalAiGlossary(body); break;
                default: DrawLocalAiOverview(body); break;
            }
        }
        TickLlamaSetup();
    }

    private void GoToLocalAiPage(LocalAiPage page)
    {
        localAiPage = page;
        localAiOpenHelp = null;
        localAiApplied = null;
        if (!LocalAiAdvanced)
            SetLocalAiMode(LocalAiWindowState.ADVANCED);
        RefreshLlamaSetup();
    }

    private void DrawLocalAiNav()
    {
        StackPanel nav = localAiNav!;
        bool advanced = LocalAiAdvanced;
        ClearRows(nav);
        nav.Visible = advanced;
        localAiScroll!.Padding = new UIEdges(advanced ? LOCAL_AI_NAV + 12 : 18, LOCAL_AI_HEAD + 6, 12, 16);
        if (!advanced)
            return;

        string[] icons = ["dashboard", "play", "files", "settings-gear", "check", "plug", "folder-opened", "refresh", "info"];
        foreach (LocalAiPage page in Enum.GetValues<LocalAiPage>())
        {
            Button tab = nav.Add(LinkButton(Said("local_ai.page_" + page.ToString().ToLowerInvariant()), icons[(int)page], () =>
            {
                localAiPage = page;
                localAiOpenHelp = null;
                localAiApplied = null;
                RefreshLlamaSetup();
            }));
            tab.Style = "button_flat";
            tab.Selected = page == localAiPage;
            // The open page has the Settings sections' pill and the bar down its left; the Alt+number shortcut is in the page's own tooltip text, not over the next item
            tab.Indicator = UIResize.Left;
            tab.Align = Origin.Left;
            tab.Size = new Vector2(LOCAL_AI_NAV - 18, LINK_ROW);
        }
    }

    // Ctrl+F and Alt+1..9 while the window has the keyboard; true when the key was the window's, so the code's find bar and the other shortcuts stay out of it
    private bool LocalAiKeyPressed()
    {
        if (localAiWindow is not { Visible: true } window || compositor.Focus is not { } focus || !window.Holds(focus))
            return false;
        var keyboard = Engine.Input.Keyboard;
        if (keyboard.Primary && !keyboard.Alt && keyboard.WasPressed(Key.F))
        {
            if (!LocalAiAdvanced)
                SetLocalAiMode(LocalAiWindowState.ADVANCED);
            localAiSearch?.Focus();
            localAiSearch?.SelectAll();
            return true;
        }
        if (!keyboard.Alt || keyboard.Primary)
            return false;
        Key[] digits = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6, Key.Number7, Key.Number8, Key.Number9];
        for (int i = 0; i < digits.Length; i++)
        {
            if (!keyboard.WasPressed(digits[i]) && !keyboard.WasPressed(Key.Keypad1 + i))
                continue;
            if (LocalAiAdvanced && LocalAiWindowState.PageOfDigit(i + 1) is { } page)
            {
                localAiPage = page;
                localAiOpenHelp = null;
                RefreshLlamaSetup();
            }
            return true;
        }
        return false;
    }
}
