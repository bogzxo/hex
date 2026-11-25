using System.Diagnostics;
using System.Numerics;
using System.Text;

using Hex.Core;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// AI edits (docs/ai-edit-plan.md, 2): select code, right-click, AI, Edit…, say what to change, and a model that follows
// instructions rewrites it. What the person sees goes Idle → Asking → Working → Reviewing:
//   Asking     a box under the target's first line, over the code: one text field (Enter runs, Esc closes, Up and Down walk
//              the recent instructions), chips for the quick actions, the model's name. The target is selected so it's
//              clear what will change.
//   Working    the same box as one progress line (loading, thinking with its seconds, writing with its lines) and a ✕.
//              The model's answer is streamed and read as it comes; nothing in the document changes.
//   Reviewing  the answer shown as the editor's prediction diff, a bar above the target with Accept, Reject, Retry and
//              Refine. Accept makes it one undo step.
//   Failed     what went wrong in words, and the way out (Retry, Set Up a Model…).
// Edit predictions pause for all of it (see PredictionGateOf). The request runs off the editor's thread and comes back
// through PostLink; a run that was cancelled or replaced is told apart by its number.
internal sealed partial class DawnScene
{
    private enum AiEditState
    {
        Idle,
        Asking,
        Working,
        Reviewing,
        Failed,
    }

    /// <summary>What the request's thread tells the editor's as the answer streams in, read by the tick.</summary>
    private sealed class AiEditLive
    {
        public volatile int Thinking, Answer, Lines, Index, Total = 1;
        public volatile bool WaitingForServer;
    }

    // The gap the box sits in is kept open under this owner, with a little room round the box
    private const string AI_EDIT_BLOCK = "ai-edit";
    private const float AI_EDIT_GAP_PAD = 3.0f;
    private AiEditState aiEditGapFor;

    private const float AI_EDIT_MIN_WIDTH = 300, AI_EDIT_MAX_WIDTH = 780, AI_EDIT_TEMPERATURE = 0.2f, AI_EDIT_RETRY_TEMPERATURE = 0.6f;

    private AiEditState aiEditState;

    // What the edit is about: the document and its editor, its lines and text as they were asked about, the target as
    // offsets into that text, the version the text was at, and the selection the person had before the target took its place
    private HexDocument? aiEditDocument;
    private CodeEditor? aiEditEditor;
    private string[] aiEditLines = [];
    private string aiEditText = string.Empty, aiEditPath = string.Empty;
    private int aiEditStart, aiEditEnd, aiEditVersion;
    private UITextPosition aiEditWasFrom, aiEditWasTo;
    private List<UITextSelection> aiEditWasExtras = [];

    // Every target of the edit (one for each caret or selection, in the order of the text) and which one holds the primary
    // selection: aiEditStart and aiEditEnd are always that one's. The requests and their answers go with the targets
    private List<(int Start, int End)> aiEditTargets = [];
    private int aiEditPrimary;
    private List<InstructRequest> aiEditRequests = [];
    private List<InstructParsed?> aiEditParts = [];

    // What the person added with @: the files by their name in the workspace, and the language server's problems of the whole file
    private List<string> aiEditFiles = [];
    private bool aiEditDiagnostics;

    // The instruction and the conversation so far, the recent instructions of this session, and the walk through them
    private string aiEditInstruction = string.Empty, aiEditDraft = string.Empty;
    private List<ChatTurn> aiEditEarlier = [];
    private List<string> aiEditRecent = [];
    private int aiEditWalk = -1;

    // The request in flight or answered: what was sent, the model, the answer and what it came to
    private InstructRequest? aiEditRequest;
    private AiEditModel? aiEditModel;
    private ChatTurn? aiEditAsked;
    private string aiEditAnswer = string.Empty;
    private InstructParsed? aiEditResult;
    private CancellationTokenSource? aiEditCts;
    private int aiEditRun;
    private AiEditLive? aiEditLive;
    private long aiEditStarted;

    // What went wrong, said, and whether the way out is to set a model up
    private string aiEditProblem = string.Empty;
    private bool aiEditSetUp;

    // Reviewing: the editor's handler for a prediction ending before ours took its place, and whether we ended it ourselves
    private Action<bool>? aiEditHandler;
    private bool aiEditEnding, aiEditReshow;

    // The box
    private Panel? aiEditPanel;
    private StackPanel? aiEditContent;
    private TextBox? aiEditField;
    private Label? aiEditStatus;
    private UISkin? aiEditSkin;
    private float aiEditWidth;
    private string aiEditStatusShown = string.Empty;

    private bool aiEditCommandsAdded;
    private MenuItem? aiEditAcceptCommand;

    /// <summary>Whether an AI edit is being asked for, worked on or reviewed. Edit predictions wait for it.</summary>
    private bool AiEditBusy => aiEditState != AiEditState.Idle;

    /* Getting to it */

    // Whether the file on screen can be edited this way: a code file of its own that can be changed
    private bool CanAiEdit() =>
        !HexAi.Off && !LayoutTools && !NotebookShown && document.IsText && document.Picture is null && document.DiffOf is null
        && !document.Editor.ReadOnly && !code.ReadOnly;

    /// <summary>The AI rows of the code's right-click menu: Edit…, the quick actions, the recent instructions and the model.</summary>
    private void AddAiEditMenu(Menu menu)
    {
        Menu ai = menu.AddMenu(Said("ai_edit.menu"), "sparkle");
        ai.Add(Said("ai_edit.edit"), () => OpenAiEdit(), "Ctrl+I", "edit").IsEnabled = CanAiEdit;

        // Fix is only there with problems on the lines, the others always
        bool problems = CanAiEdit() && AiEditProblemsAt(document.Editor.Document).Count > 0;
        ai.AddSeparator();
        foreach (string action in HexAiEditGlue.QuickActions)
        {
            MenuItem item = ai.Add(Said("ai_edit.action_" + action), () => OpenAiEdit(action), icon: action == "fix" ? "lightbulb" : "sparkle");
            item.IsEnabled = CanAiEdit;
            if (action == "fix")
                item.IsVisible = () => problems;
        }

        if (aiEditRecent.Count > 0)
        {
            ai.AddSeparator();
            foreach (string text in aiEditRecent.Take(HexAiEditGlue.MENU_RECENT))
            {
                string instruction = text;
                ai.Add(OneLine(instruction, 48), () => OpenAiEdit(null, instruction), icon: "history").IsEnabled = CanAiEdit;
            }
        }

        // The model that will do it, dim; with none, why not and the way to get one
        ai.AddSeparator();
        if (AiEditModels.Resolve(settings, out string? why) is { } model)
            ai.Add(AiEditModelText(model), () => { }, icon: "chip").IsEnabled = () => false;
        else
        {
            ai.Add(Said("ai_edit.problem_" + (why ?? "no_model")), () => { }).IsEnabled = () => false;
            ai.Add(Said("ai_edit.set_up"), AiEditSetUpModel, icon: "settings-gear");
        }
        ai.Add(Said("local_ai.command_open"), () => OpenLocalAiWindow(), icon: "settings-gear");
        menu.AddSeparator();
    }

    /// <summary>Ctrl+I: the box when nothing's going on, Refine when an answer is being reviewed, else the box's field.</summary>
    private void AiEditKey() => OpenAiEdit();

    // The palette's commands and their keys, made once on the first tick with AI on. Under the predictions' own menu
    private void EnsureAiEditCommands()
    {
        if (aiEditCommandsAdded || HexAi.Off)
            return;
        aiEditCommandsAdded = true;

        MenuItem edit = commands.Add(PREDICTION_MENU, Said("ai_edit.command_edit"), AiEditKey, "Ctrl+I", "edit", UIKeyUse.BoundWhileTyping);
        edit.IsEnabled = () => CanAiEdit() || AiEditBusy;
        foreach (string action in HexAiEditGlue.QuickActions)
        {
            string chosen = action;
            commands.Add(PREDICTION_MENU, Said("ai_edit.command_" + chosen), () => OpenAiEdit(chosen), icon: "sparkle").IsEnabled = CanAiEdit;
        }

        aiEditAcceptCommand = commands.Add(PREDICTION_MENU, Said("ai_edit.command_accept"), AiEditAccept, "Ctrl+Enter", "check", UIKeyUse.BoundWhileTyping);
        aiEditAcceptCommand.IsEnabled = () => aiEditState == AiEditState.Reviewing;
        MenuItem retry = commands.Add(PREDICTION_MENU, Said("ai_edit.command_retry"), AiEditRetry, "Ctrl+R", "refresh", UIKeyUse.BoundWhileTyping);
        retry.IsEnabled = () => aiEditState is AiEditState.Reviewing or AiEditState.Failed;
        commands.Add(PREDICTION_MENU, Said("ai_edit.command_reject"), () => AiEditClose(restore: true), icon: "close").IsEnabled = () => AiEditBusy;

        BindShared("Ctrl+I", whileTyping: true, edit);
        BindShared("Ctrl+Enter", whileTyping: true, aiEditAcceptCommand);
        BindShared("Ctrl+R", whileTyping: true, retry);
        ApplyKeyLayer(force: true);
    }

    // Set Up a Model…: the Local AI window, at its Engine page for Ollama or another OpenAI server
    private void AiEditSetUpModel()
    {
        if (settings.EditPredictionsProvider is PredictionProviders.OLLAMA or PredictionProviders.OPENAI)
            OpenLocalAiWindow("engine");
        else
            OpenLlamaSetup();
    }

    /* Asking */

    /// <summary>
    /// The box for what's selected (or the block round the caret), or with a quick action's key or an instruction given,
    /// that run at once. Refine while an answer is being reviewed.
    /// </summary>
    private void OpenAiEdit(string? action = null, string? instruction = null)
    {
        if (HexAi.Off)
            return;
        if (AiEditBusy)
        {
            if (action is null && instruction is null)
            {
                if (aiEditState == AiEditState.Reviewing)
                    AiEditRefine();
                else
                    aiEditField?.Focus();
                return;
            }
            AiEditClose(restore: true);
        }

        if (!CanAiEdit())
        {
            Say(Said("ai_edit.cannot"), error: true);
            return;
        }

        HexDocument at = document;
        CodeEditor editor = at.Editor;
        UITextDocument text = editor.Document;
        var lines = new string[text.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = text.LineAt(i);
        string joined = string.Join('\n', lines);

        UITextPosition from = text.HasSelection ? text.SelectionStart : text.Caret, to = text.HasSelection ? text.SelectionEnd : text.Caret;
        // One target for each caret or selection; the primary one (put down last) is where the box hangs
        var selections = new List<(int From, int To, int Caret)> { (AiEditOffset(lines, from), AiEditOffset(lines, to), AiEditOffset(lines, text.Caret)) };
        foreach (UITextSelection extra in text.ExtraCarets)
        {
            UITextPosition low = extra.Caret < extra.Anchor ? extra.Caret : extra.Anchor, high = extra.Caret < extra.Anchor ? extra.Anchor : extra.Caret;
            selections.Add((AiEditOffset(lines, low), AiEditOffset(lines, high), AiEditOffset(lines, extra.Caret)));
        }
        (List<(int Start, int End)> targets, int primary) = HexAiEditGlue.Targets(joined, selections, 0);
        if (joined.Length == 0 || targets.Count == 0)
        {
            Say(Said("ai_edit.nothing"));
            return;
        }
        (int start, int end) = targets[primary];
        aiEditTargets = targets;
        aiEditPrimary = primary;
        aiEditFiles = [];
        aiEditDiagnostics = false;

        aiEditDocument = at;
        aiEditEditor = editor;
        aiEditLines = lines;
        aiEditText = joined;
        aiEditPath = HexPredictionGlue.Sent(WorkspaceRoots(), at.Path ?? at.Name);
        aiEditStart = start;
        aiEditEnd = end;
        aiEditVersion = text.Version;
        aiEditWasFrom = from;
        aiEditWasTo = to;
        aiEditWasExtras = [.. text.ExtraCarets];
        aiEditEarlier = [];
        aiEditResult = null;
        aiEditWalk = -1;
        aiEditDraft = string.Empty;

        // Every target selected, the primary one's caret at its start: the box hangs from there
        AiEditSelectTargets(text);

        if (action is not null)
        {
            string asked = HexAiEditGlue.Instruction(action);
            if (action == "fix" && !AiEditAnyProblems())
            {
                AiEditRestoreSelection(text);
                Say(Said("ai_edit.no_problems"));
                return;
            }
            RunAiEdit(asked);
            return;
        }
        if (instruction is not null)
        {
            RunAiEdit(instruction);
            return;
        }

        Enter(AiEditState.Asking, string.Empty);
    }

    // Each target selected (the primary one as the main selection, its caret at the start), so every one of them is lit
    private void AiEditSelectTargets(UITextDocument text)
    {
        var extras = new List<UITextSelection>();
        for (int i = 0; i < aiEditTargets.Count; i++)
        {
            if (i != aiEditPrimary)
                extras.Add(new UITextSelection(HexPredictionGlue.Position(aiEditLines, aiEditTargets[i].Start), HexPredictionGlue.Position(aiEditLines, aiEditTargets[i].End)));
        }
        text.Select(HexPredictionGlue.Position(aiEditLines, aiEditEnd), HexPredictionGlue.Position(aiEditLines, aiEditStart));
        text.SetExtraCarets(extras);
    }

    // The selections the person had before the edit, all of them
    private void AiEditRestoreSelection(UITextDocument text)
    {
        if (aiEditWasFrom == aiEditWasTo)
            text.MoveTo(aiEditWasFrom);
        else
            text.Select(aiEditWasFrom, aiEditWasTo);
        text.SetExtraCarets(aiEditWasExtras);
    }

    // The offset of a place into the lines joined with '\n'
    private static int AiEditOffset(string[] lines, UITextPosition at)
    {
        int offset = 0;
        for (int i = 0; i < at.Line && i < lines.Length; i++)
            offset += lines[i].Length + 1;
        return offset + (at.Line < lines.Length ? Math.Clamp(at.Column, 0, lines[at.Line].Length) : 0);
    }

    // What the language server says is wrong on the lines of what's selected or the caret's line, as the prompt lists it
    private List<string> AiEditProblemsAt(UITextDocument text)
    {
        int first = text.HasSelection ? text.SelectionStart.Line : text.Caret.Line, last = text.HasSelection ? text.SelectionEnd.Line : text.Caret.Line;
        return AiEditProblemsBetween(first, last);
    }

    // The same for the lines of the target
    private List<string> AiEditProblemsOf() => AiEditProblemsFor(aiEditStart, aiEditEnd);

    // Whether any target has a problem on its lines
    private bool AiEditAnyProblems() => aiEditTargets.Any(target => AiEditProblemsFor(target.Start, target.End).Count > 0);

    // The problems on the lines of the target at those offsets, and with @diagnostics the rest of the file's after them
    private List<string> AiEditProblemsFor(int start, int end)
    {
        List<string> found = AiEditProblemsBetween(HexPredictionGlue.Position(aiEditLines, start).Line, HexPredictionGlue.Position(aiEditLines, Math.Max(start, end - 1)).Line);
        if (aiEditDiagnostics)
        {
            foreach (string more in AiEditProblemsBetween(0, int.MaxValue))
            {
                if (!found.Contains(more))
                    found.Add(more);
            }
        }
        return found;
    }

    private List<string> AiEditProblemsBetween(int first, int last)
    {
        var found = new List<string>();
        if (languages?.ServerDiagnosticsOf(AiEditBusy && aiEditDocument is { } busy ? busy : document) is not { } all)
            return found;
        foreach (UICodeDiagnostic each in all.Where(d => !d.Hint && d.Line >= first && d.Line <= last).OrderBy(d => d.Warning).ThenBy(d => d.Line).Take(10))
            found.Add(HexAiEditGlue.ProblemLine(each.Line, each.Message));
        return found;
    }

    // The target's text asked about again when the document was changed since, the same place when it can be found
    private bool AiEditRecapture()
    {
        if (aiEditEditor is not { } editor)
            return false;
        UITextDocument text = editor.Document;
        if (text.Version == aiEditVersion && aiEditLines.Length == text.LineCount)
            return true;
        if (AiEditRebase())
            return true;
        if (aiEditTargets.Count > 1)
            return false;

        // The target itself was changed: the block round where it was, if there's any
        var lines = new string[text.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = text.LineAt(i);
        string joined = string.Join('\n', lines);
        int at = Math.Min(aiEditStart, joined.Length);
        (int start, int end) = InstructTarget.Around(joined, at, at, at);
        if (joined.Length == 0 || end <= start)
            return false;
        aiEditTargets = [(start, end)];
        aiEditPrimary = 0;
        aiEditStart = start;
        aiEditEnd = end;
        aiEditLines = lines;
        aiEditText = joined;
        aiEditVersion = text.Version;
        return true;
    }

    // The document was typed in since the request was made: the target is found again by its own text, nearest where it
    // was, and the offsets move with it. False when the target's text changed or is gone, then the edit can't go on.
    // The request keeps the text it was sent with; its answer's edits move by the difference when they're shown
    private bool AiEditRebase()
    {
        if (aiEditEditor is not { } editor)
            return false;
        UITextDocument text = editor.Document;
        var lines = new string[text.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = text.LineAt(i);
        string joined = string.Join('\n', lines);
        if (joined != aiEditText)
        {
            var moved = new List<(int Start, int End)>();
            foreach ((int from, int to) in aiEditTargets)
            {
                if (HexAiEditGlue.Rebase(aiEditText, from, to, joined) is not { } start)
                    return false;
                moved.Add((start, to + start - from));
            }
            aiEditTargets = moved;
            (aiEditStart, aiEditEnd) = moved[aiEditPrimary];
            aiEditLines = lines;
            aiEditText = joined;
        }
        aiEditVersion = text.Version;
        return true;
    }

    /* Working */

    // Runs an instruction: the model found, the server started when it's Hex's own, the request sent off the editor's thread
    private void RunAiEdit(string instruction, float? temperature = null)
    {
        aiEditInstruction = instruction.Trim();
        if (aiEditInstruction.Length == 0 || aiEditEditor is not { } editor)
            return;
        if (!AiEditRecapture())
        {
            AiEditClose(restore: false);
            Say(Said("ai_edit.nothing"));
            return;
        }
        aiEditRecent = HexAiEditGlue.Remember(aiEditRecent, aiEditInstruction);

        // "Add a comment above" with only carets: above each caret's own line, not above the block it is in
        if (HexAiEditGlue.IsPlacement(aiEditInstruction) && editor.Document.Version == aiEditVersion && aiEditLines.Length == editor.Document.LineCount
            && aiEditWasFrom == aiEditWasTo && aiEditWasExtras.All(extra => extra.Caret == extra.Anchor))
        {
            var carets = new List<(int From, int To, int Caret)> { (AiEditOffset(aiEditLines, aiEditWasFrom), AiEditOffset(aiEditLines, aiEditWasTo), AiEditOffset(aiEditLines, aiEditWasFrom)) };
            foreach (UITextSelection extra in aiEditWasExtras)
                carets.Add((AiEditOffset(aiEditLines, extra.Caret), AiEditOffset(aiEditLines, extra.Caret), AiEditOffset(aiEditLines, extra.Caret)));
            (List<(int Start, int End)> narrowed, int narrowedPrimary) = HexAiEditGlue.Targets(aiEditText, carets, 0, lineOnly: true);
            if (narrowed.Count > 0)
            {
                aiEditTargets = narrowed;
                aiEditPrimary = narrowedPrimary;
                (aiEditStart, aiEditEnd) = narrowed[narrowedPrimary];
            }
        }

        AiEditModel? model = AiEditModels.Resolve(settings, out string? why);
        if (model is null)
        {
            AiEditFail(Said("ai_edit.problem_" + (why ?? "no_model")), setUp: true);
            return;
        }
        aiEditModel = model;

        // @file and @diagnostics typed in the instruction count as if they had been picked
        (string said, List<string> typedFiles, bool typedDiagnostics) = InstructMentions.Parse(aiEditInstruction);
        if (said.Length > 0)
            aiEditInstruction = said;
        foreach (string file in typedFiles)
        {
            if (!aiEditFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                aiEditFiles.Add(file);
        }
        aiEditDiagnostics |= typedDiagnostics;

        string language = PredictionLanguage(new PredictionTarget(aiEditDocument!, editor));
        FlushPredictionHistory();
        IReadOnlyList<PredictionEvent> events = predictionSession?.Engine.History.Events(aiEditPath) ?? [];
        List<InstructContext> context = AiEditContextBlocks();
        // One request for each target, the same but for the target and its problems; the earlier turns of a refinement only for one target
        var requests = new List<InstructRequest>();
        foreach ((int from, int to) in aiEditTargets)
        {
            requests.Add(new InstructRequest(aiEditPath, language, aiEditText, from, to, aiEditInstruction, events, AiEditProblemsFor(from, to),
                aiEditTargets.Count == 1 ? aiEditEarlier : [], context));
        }
        InstructRequest request = requests[aiEditPrimary];
        aiEditRequests = requests;
        aiEditParts = [];
        aiEditRequest = request;

        bool thinks = model.Thinks && settings.AiEditThinking;
        PredictionTuning given = settings.TuningFor(LocalAiRole.Chat);
        PredictionTuning tuning = given with
        {
            Temperature = temperature ?? (given.Temperature > 0 ? given.Temperature : AI_EDIT_TEMPERATURE),
            Thinking = thinks ? PredictionTuning.THINK_ON : PredictionTuning.THINK_OFF,
        };
        int maxTokens = requests.Max(each => InstructEditFormat.MaxTokens(each, thinks));
        if (settings.AiEditMaxTokens > 0)
            maxTokens = Math.Max(maxTokens, settings.AiEditMaxTokens);

        IChatStreamer? streamer = ChatStreams.Make(model.Provider, model.Url, model.Model, model.ApiKey, tuning, maxTokens);
        if (streamer is null)
        {
            AiEditFail(Said("ai_edit.problem_not_set_up"), setUp: true);
            return;
        }

        // Hex's own llama-server is started if it isn't, and waited for
        bool server = model.Provider == PredictionProviders.LLAMACPP;
        if (server)
        {
            if (settings.LlamaCppConfig is null)
            {
                AiEditFail(Said("ai_edit.problem_not_set_up"), setUp: true);
                return;
            }
            if (LlamaServer.State is not (LlamaCppServerState.Ready or LlamaCppServerState.Starting))
                StartLlamaServer();
        }

        List<List<ChatTurn>> conversations = [.. requests.Select(each => InstructEditFormat.Build(each, settings.EditPredictionsSystemPrompt))];
        aiEditAsked = conversations[aiEditPrimary][^1];
        aiEditAnswer = string.Empty;
        aiEditResult = null;
        int primary = aiEditPrimary;

        var cancel = aiEditCts = new CancellationTokenSource();
        var live = aiEditLive = new AiEditLive { WaitingForServer = server, Total = requests.Count };
        int run = ++aiEditRun;
        aiEditStarted = Stopwatch.GetTimestamp();
        linkAiQueuedAt = null;
        string serverFailed = Said("ai_edit.problem_server");
        LlamaCppServer? llama = server ? LlamaServer : null;

        // Back on the editor's thread: where the editor was left selecting the target stays until the review
        AiEditSelectTargets(editor.Document);
        Enter(AiEditState.Working, aiEditInstruction);

        _ = Task.Run(async () =>
        {
            var parts = new List<InstructParsed?>();
            string? error = null, raw = string.Empty;
            try
            {
                if (llama is not null)
                {
                    for (int waited = 0; llama.State != LlamaCppServerState.Ready; waited += 200)
                    {
                        if (llama.State == LlamaCppServerState.Failed || waited > 240_000 || (llama.State == LlamaCppServerState.Stopped && waited > 20_000))
                            throw new InvalidOperationException(llama.Problem is { Length: > 0 } problem ? problem : serverFailed);
                        await Task.Delay(200, cancel.Token).ConfigureAwait(false);
                    }
                    live.WaitingForServer = false;
                }

                // One target after the other: one card holds one model, and the server keeps the prompt's start (system, file,
                // context, recent edits) from one request to the next
                for (int index = 0; index < requests.Count; index++)
                {
                    InstructRequest each = requests[index];
                    live.Index = index;
                    live.Thinking = live.Answer = live.Lines = 0;
                    InstructParsed? result = null;
                    var answer = new StringBuilder();
                    await foreach (ChatPiece piece in streamer.Stream(conversations[index], cancel.Token).ConfigureAwait(false))
                    {
                        if (piece.Thinking)
                        {
                            live.Thinking++;
                            continue;
                        }
                        live.Answer++;
                        answer.Append(piece.Text);
                        live.Lines = HexAiEditGlue.LinesWritten(answer.ToString());
                        InstructParsed parsed = InstructEditFormat.Parse(each, answer.ToString(), complete: false);
                        if (parsed.Done)
                        {
                            result = parsed;
                            break;
                        }
                    }
                    result ??= InstructEditFormat.Parse(each, answer.ToString(), complete: true);
                    parts.Add(result);
                    if (index == primary)
                        raw = answer.ToString();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception caught)
            {
                error = caught.Message;
            }

            string primaryAnswer = raw;
            PostLink(() => AiEditAnswered(run, parts, primaryAnswer, error));
        });
    }

    // The request's thread finished: its answer, or what went wrong. Told apart from a run that was cancelled by its number
    private void AiEditAnswered(int run, List<InstructParsed?> parts, string raw, string? error)
    {
        if (run != aiEditRun || aiEditState != AiEditState.Working)
            return;
        aiEditCts?.Dispose();
        aiEditCts = null;
        aiEditAnswer = raw;

        if (error is not null)
        {
            AiEditFail(error, setUp: false);
            return;
        }
        if (parts.Count != aiEditRequests.Count || parts.Any(part => part is null))
        {
            AiEditFail(Said("ai_edit.problem_no_code"), setUp: false);
            return;
        }

        // A target left as it was is no problem as long as another changed; any other problem ends the edit
        if (parts.FirstOrDefault(part => part!.Problem is { } p && p != InstructProblem.UNCHANGED) is { } failed)
        {
            AiEditFail(Said("ai_edit.problem_" + failed.Problem), setUp: false);
            return;
        }
        if (parts.All(part => part!.Problem is not null))
        {
            AiEditFail(Said("ai_edit.problem_" + parts[0]!.Problem), setUp: false);
            return;
        }
        aiEditParts = parts;
        aiEditResult = parts.First(part => part!.Problem is null);
        AiEditReview();
    }

    /* Reviewing */

    // The answer shown in place as the prediction diff, the bar where the box was
    private void AiEditReview()
    {
        if (aiEditEditor is not { } editor || aiEditResult is not { } result || aiEditRequest is null)
            return;
        UITextDocument text = editor.Document;
        if (text.Version != aiEditVersion && !AiEditSameText())
        {
            AiEditFail(Said("ai_edit.problem_changed"), setUp: false);
            return;
        }
        aiEditVersion = text.Version;

        // The selection gives way to the diff, the caret at the target's start
        text.MoveTo(HexPredictionGlue.Position(aiEditLines, aiEditStart));
        ClearShownPrediction();
        aiEditHandler = editor.OnPredictionEnded;
        editor.OnPredictionEnded = AiEditPredictionEnded;
        aiEditEnding = false;
        if (!AiEditShow())
        {
            editor.OnPredictionEnded = aiEditHandler;
            AiEditFail(Said("ai_edit.problem_no_fit"), setUp: false);
            return;
        }
        Enter(AiEditState.Reviewing, aiEditInstruction);
        if (AiEditEditorShown())
            editor.Focus();
    }

    // The edits given to the editor to show, false when they don't fit its text
    private bool AiEditShow()
    {
        if (aiEditEditor is not { } editor || aiEditResult is null || aiEditParts.Count != aiEditTargets.Count)
            return false;

        // The answers' edits are offsets into the text each request was sent with; the targets may have moved since. All of
        // them go to the editor as one prediction, so accepting is one undo step
        var parts = new List<IReadOnlyList<PredictionEdit>>();
        int delta = 0;
        int? after = null;
        for (int i = 0; i < aiEditParts.Count; i++)
        {
            if (aiEditParts[i] is not { Problem: null, Replacement: { } replacement } part)
                continue;
            (int start, int end) = aiEditTargets[i];
            int moved = start - aiEditRequests[i].TargetStart;
            parts.Add(moved == 0 ? part.Edits : HexAiEditGlue.Shift(part.Edits, moved));
            if (i == aiEditPrimary)
                after = start + delta + replacement.Length;
            delta += replacement.Length - (end - start);
        }
        if (HexAiEditGlue.Combine(parts) is not { } shifted)
            return false;
        UITextEdit[]? edits = HexPredictionGlue.Edits(shifted, aiEditLines);
        if (edits is null)
            return false;
        editor.SetPrediction(new UICodePrediction(edits, HexPredictionGlue.CaretAfter(shifted, aiEditLines, after)));
        return editor.HasPrediction;
    }

    // Whether the document's text is what it was when the request was made, an edit that was undone included
    private bool AiEditSameText()
    {
        if (aiEditEditor is not { } editor)
            return false;
        UITextDocument text = editor.Document;
        if (text.LineCount != aiEditLines.Length)
            return false;
        for (int i = 0; i < aiEditLines.Length; i++)
        {
            if (!string.Equals(text.LineAt(i), aiEditLines[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // The editor's prediction ended while one was being reviewed: taken, or let go of by the person, or its focus went
    // to one of the bar's buttons (a click anywhere takes the keyboard away from the editor, and with it the diff), when
    // it's shown again, the button's press does the rest
    private void AiEditPredictionEnded(bool taken)
    {
        if (aiEditState != AiEditState.Reviewing || aiEditEditor is not { } editor)
            return;
        if (taken)
        {
            editor.OnPredictionEnded = aiEditHandler;
            AiEditAccepted(editor);
            return;
        }
        if (aiEditEnding)
            return;

        // The document was typed in: the tick finds the target again and shows the diff again
        if (editor.Document.Version != aiEditVersion)
        {
            aiEditReshow = true;
            return;
        }
        if (!editor.IsFocused && AiEditShow())
            return;
        AiEditClose(restore: false);
    }

    // Accept: the whole answer made as one undo step, and into the history as an accepted prediction
    private void AiEditAccept()
    {
        if (aiEditState != AiEditState.Reviewing || aiEditEditor is not { } editor)
            return;
        if (!editor.AcceptPrediction())
        {
            AiEditFail(Said("ai_edit.problem_changed"), setUp: false);
            return;
        }
        // AcceptPrediction told AiEditPredictionEnded, which finished it
    }

    private void AiEditAccepted(CodeEditor editor)
    {
        UITextDocument text = editor.Document;
        string before = string.Join('\n', aiEditLines);
        var lines = new string[text.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = text.LineAt(i);
        string after = string.Join('\n', lines), path = aiEditPath;
        if (predictionSession?.Engine.History is { } history)
            _ = Task.Run(() => history.Accepted(path, before, after));

        AiEditEnd();
        editor.Focus();
        Say(Said("ai_edit.accepted"));
    }

    // Retry: the same instruction again, with a little more temperature
    private void AiEditRetry()
    {
        if (aiEditState is not (AiEditState.Reviewing or AiEditState.Failed) || aiEditEditor is null)
            return;
        string instruction = aiEditInstruction;
        AiEditLetGoOfReview();
        RunAiEdit(instruction, AI_EDIT_RETRY_TEMPERATURE);
    }

    // Refine: the box again with the instruction kept, the question and the answer given to the model as the talk so far
    private void AiEditRefine()
    {
        if (aiEditState != AiEditState.Reviewing || aiEditEditor is not { } editor || aiEditAsked is not { } asked)
            return;
        // The talk so far is about one target's answer: with several, the refinement starts afresh
        var earlier = aiEditTargets.Count > 1 ? [] : new List<ChatTurn>(aiEditEarlier) { asked, new ChatTurn("assistant", aiEditAnswer) };
        string instruction = aiEditInstruction;
        AiEditLetGoOfReview();
        aiEditEarlier = earlier;
        AiEditSelectTargets(editor.Document);
        Enter(AiEditState.Asking, instruction);
    }

    // The prediction taken off the editor without anything ending the edit
    private void AiEditLetGoOfReview()
    {
        if (aiEditState != AiEditState.Reviewing || aiEditEditor is not { } editor)
            return;
        aiEditEnding = true;
        editor.ClearPrediction();
        editor.OnPredictionEnded = aiEditHandler;
        aiEditEnding = false;
    }

    /* Failing and ending */

    private void AiEditFail(string problem, bool setUp)
    {
        AiEditLetGoOfReview();
        aiEditCts?.Cancel();
        aiEditCts?.Dispose();
        aiEditCts = null;
        aiEditRun++;
        aiEditProblem = problem;
        aiEditSetUp = setUp;
        Enter(AiEditState.Failed, aiEditInstruction);
    }

    /// <summary>Everything let go of: the request dropped, the diff cleared, the box gone, the selection the person had given back.</summary>
    private void AiEditClose(bool restore)
    {
        if (!AiEditBusy)
            return;
        CodeEditor? editor = aiEditEditor;
        bool same = editor is not null && editor.Document.Version == aiEditVersion;
        AiEditLetGoOfReview();
        AiEditEnd();
        if (restore && same && editor is not null)
            AiEditRestoreSelection(editor.Document);

        // The keyboard goes back to the editor that owns the edit, whatever had it (a terminal, the box's field)
        if (editor is not null && aiEditDocument is { } owner && documents.Contains(owner) && owner == document && editor.VisibleInHierarchy)
            editor.Focus();
    }

    // The state back to idle and the box taken down, the request dropped
    private void AiEditEnd()
    {
        aiEditCts?.Cancel();
        aiEditCts?.Dispose();
        aiEditCts = null;
        aiEditRun++;
        aiEditState = AiEditState.Idle;
        aiEditResult = null;
        aiEditRequest = null;
        aiEditRequests = [];
        aiEditParts = [];
        aiEditLive = null;
        aiEditGapFor = AiEditState.Idle;
        aiEditEditor?.ClearBlockSpace(AI_EDIT_BLOCK);
        if (aiEditPanel is { } panel)
        {
            panel.Visible = false;
            panel.Parent?.Remove(panel);
        }
        aiEditField = null;
        aiEditContent = null;
        aiEditStatus = null;
        aiEditPanel = null;
        aiEditStatusShown = string.Empty;
    }

    /* Every tick */

    /// <summary>Every tick: the commands made once, a run followed, the box kept under the target's first line.</summary>
    private void UpdateAiEdit()
    {
        EnsureAiEditCommands();
        if (aiEditState == AiEditState.Idle)
            return;

        // The document gone or another one on screen, the editor closed: nothing to hang the box from
        if (aiEditDocument is not { } at || aiEditEditor is not { } editor || !documents.Contains(at) || editor.Module is null || HexAi.Off)
        {
            AiEditClose(restore: false);
            return;
        }

        UITextDocument text = editor.Document;
        // Typed in elsewhere meanwhile: the target is found again by its text and everything moves with it; only a change
        // to the target itself (or its text gone) ends the edit. The editor let go of the diff when it was typed in, shown again
        if (aiEditState is AiEditState.Working or AiEditState.Reviewing && (text.Version != aiEditVersion || aiEditReshow))
        {
            bool reshow = aiEditState == AiEditState.Reviewing;
            aiEditReshow = false;
            if (!AiEditRebase() || reshow && !AiEditShow())
            {
                AiEditFail(Said("ai_edit.problem_changed"), setUp: false);
                return;
            }
        }

        if (aiEditState == AiEditState.Working && aiEditStatus is { } status && aiEditLive is { } live)
        {
            string said = AiEditProgressText(live);
            if (said != aiEditStatusShown)
            {
                aiEditStatusShown = said;
                status.Text = said;
            }
        }

        AiEditPlace(editor);
    }

    // What the progress line says: the model loading, thinking with its seconds, writing with its lines
    private string AiEditProgressText(AiEditLive live)
    {
        string model = aiEditModel?.Title ?? string.Empty;
        if (live.WaitingForServer)
            return "$(loading~spin) " + Said("ai_edit.progress_server");
        string seconds = HexAiEditGlue.Seconds(Stopwatch.GetElapsedTime(aiEditStarted).TotalSeconds);
        string of = live.Total > 1
            ? Said("ai_edit.progress_of", ("n", (live.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("total", live.Total.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " · "
            : string.Empty;
        // A linked device's model with this edit in line behind its other chats: its place, until the first piece comes back
        if (aiEditModel is { } linked && LinkAiDeviceOf(linked) is { } linkedDevice && linkAiQueuedAt is { Place: >= 2 } queued
            && live.Thinking + live.Answer == 0)
            return "$(loading~spin) " + of + Said("ai_edit.progress_queued", ("device", LinkTalkLiteral(linkedDevice)), ("place", Ordinal(queued.Place)), ("seconds", seconds));
        return "$(loading~spin) " + of + HexAiEditGlue.ProgressOf(aiEditModel?.Swaps == true, live.Thinking, live.Answer) switch
        {
            AiEditProgress.Loading => Said("ai_edit.progress_loading", ("model", LinkTalkLiteral(model)), ("seconds", seconds)),
            AiEditProgress.Waiting => Said("ai_edit.progress_waiting", ("seconds", seconds)),
            AiEditProgress.Thinking => Said("ai_edit.progress_thinking", ("seconds", seconds)),
            _ => Said("ai_edit.progress_writing", ("lines", live.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        };
    }

    // The box put under the target's first line (above it while reviewing, so the diff stays in sight), inside the code's area
    private void AiEditPlace(CodeEditor editor)
    {
        if (editor.Module is not { } from)
            return;
        // Only over the editor it runs in: another tab, layout or face, the editor collapsed or covered, and the box and its
        // gap are gone until it's back (the request goes on)
        if (!HexAiEditGlue.CardVisible(aiEditDocument == document, editor.VisibleInHierarchy, (0, 0, 1, 1), (0, 0, 1, 1)))
        {
            AiEditHideCard(editor);
            return;
        }
        // The panel is made by AiEditBuild, which only runs from here: made on the first placing
        if (aiEditPanel is null)
            AiEditBuild(Math.Clamp(editor.CodeBounds.Max.X - editor.CodeBounds.Min.X - 12.0f, AI_EDIT_MIN_WIDTH, AI_EDIT_MAX_WIDTH));
        if (aiEditPanel is not { } panel)
            return;
        if (editor.CaretLineHeight <= 0.0f)
        {
            // Not laid out yet: tried again next tick rather than left unseen
            panel.Visible = false;
            return;
        }

        UIRect area = editor.CodeBounds;
        Vector2 a = chrome.ToLocal(from.ToWorld(area.Min)), b = chrome.ToLocal(from.ToWorld(area.Max));
        Vector2 low = Vector2.Min(a, b), high = Vector2.Max(a, b);
        UIRect visibleArea = chrome.Root.Bounds;
        if (!HexAiEditGlue.CardVisible(true, true, (low.X, low.Y, high.X, high.Y),
                (Math.Min(visibleArea.Min.X, visibleArea.Max.X), Math.Min(visibleArea.Min.Y, visibleArea.Max.Y), Math.Max(visibleArea.Min.X, visibleArea.Max.X), Math.Max(visibleArea.Min.Y, visibleArea.Max.Y))))
        {
            AiEditHideCard(editor);
            return;
        }

        float width = Math.Clamp(high.X - low.X - 12.0f, AI_EDIT_MIN_WIDTH, AI_EDIT_MAX_WIDTH);
        if (aiEditContent is null || MathF.Abs(width - aiEditWidth) > 1.0f || Skin != aiEditSkin)
            AiEditBuild(width);

        // A gap kept open in the code for the box, the lines under it pushed down so nothing is covered: over the target's
        // first line while asking and working (it works at line 1 too), under the last line of the diff while reviewing
        // (or of the target, with no diff yet), at the end of the file too. The box sits in it exactly and follows the scroll
        float height = panel.Size.Y;
        bool review = aiEditState == AiEditState.Reviewing;
        int first = HexPredictionGlue.Position(aiEditLines, aiEditStart).Line;
        int last = HexPredictionGlue.Position(aiEditLines, Math.Max(aiEditStart, aiEditEnd - 1)).Line;
        // With several targets the gap is the primary target's, not the end of the whole prediction
        int diffLast = aiEditTargets.Count > 1 ? AiEditPrimaryDiffLine(last) : editor.PredictionLastLine;
        editor.SetBlockSpace(AI_EDIT_BLOCK, review ? Math.Max(last, diffLast) : first, above: !review, height + AI_EDIT_GAP_PAD * 2.0f);
        if (aiEditGapFor != aiEditState)
        {
            aiEditGapFor = aiEditState;
            editor.RevealBlockSpace(AI_EDIT_BLOCK);
        }

        // Not laid out yet, or the gap not wholly in sight (scrolled off an edge): the box waits rather than float over the tabs
        if (!editor.BlockSpaceBounds(AI_EDIT_BLOCK, out UIRect gap))
        {
            panel.Visible = false;
            return;
        }
        Vector2 g1 = chrome.ToLocal(from.ToWorld(gap.Min)), g2 = chrome.ToLocal(from.ToWorld(gap.Max));
        Vector2 gapLow = Vector2.Min(g1, g2), gapHigh = Vector2.Max(g1, g2);
        float top = gapHigh.Y - AI_EDIT_GAP_PAD;
        if (top > high.Y + 0.5f || top - height < low.Y - 0.5f)
        {
            panel.Visible = false;
            return;
        }

        if (panel.Parent != chrome.Root)
        {
            panel.Parent?.Remove(panel);
            panel.Anchor = Origin.TopLeft;
            panel.Pivot = Origin.TopLeft;
            chrome.Root.Add(panel);
        }
        panel.Visible = true;
        panel.Position = new Vector2(low.X + 6.0f - chrome.Root.Bounds.Min.X, top - chrome.Root.Bounds.Max.Y);
    }

    // The box taken off the screen and its gap given back, the edit itself going on; both come back when the editor is shown
    private void AiEditHideCard(CodeEditor editor)
    {
        if (aiEditPanel is { } panel)
            panel.Visible = false;
        editor.ClearBlockSpace(AI_EDIT_BLOCK);
        aiEditGapFor = AiEditState.Idle;
    }

    // The last line of the document the primary target's own edits reach, at least the given line
    private int AiEditPrimaryDiffLine(int atLeast)
    {
        if (aiEditPrimary >= aiEditParts.Count || aiEditPrimary >= aiEditTargets.Count || aiEditParts[aiEditPrimary] is not { } part)
            return atLeast;
        int moved = aiEditTargets[aiEditPrimary].Start - aiEditRequests[aiEditPrimary].TargetStart;
        int line = atLeast;
        foreach (PredictionEdit edit in part.Edits)
            line = Math.Max(line, HexPredictionGlue.Position(aiEditLines, Math.Clamp(edit.End + moved, 0, aiEditText.Length)).Line);
        return line;
    }

    /* The box */

    // Moves to a state, the box made again for it. The field keeps the keyboard in every state but reviewing
    private void Enter(AiEditState state, string instruction)
    {
        aiEditState = state;
        aiEditWalk = -1;
        aiEditStatusShown = string.Empty;
        aiEditField ??= MakeAiEditField();
        aiEditField.Text = instruction;
        aiEditField.ReadOnly = state != AiEditState.Asking;
        // The last state's rows taken down with it, else they stay under the new ones (the chips behind the progress line)
        if (aiEditContent is { } old)
            aiEditPanel?.Remove(old);
        aiEditContent = null;
        if (aiEditEditor is { Module: not null } editor)
            AiEditPlace(editor);
        if (state != AiEditState.Reviewing && AiEditEditorShown())
            aiEditField.Focus();
    }

    // Whether the editor the edit runs in is the one on screen and visible, where its keyboard and card belong
    private bool AiEditEditorShown() => aiEditEditor is { } editor && aiEditDocument == document && editor.VisibleInHierarchy;

    // The box made again for what it holds now (a chip added or taken out), as a change of state does
    private void AiEditRebuildBox()
    {
        if (aiEditContent is { } old)
            aiEditPanel?.Remove(old);
        aiEditContent = null;
        if (aiEditEditor is { Module: not null } editor)
            AiEditPlace(editor);
        aiEditField?.Focus();
    }

    // @ typed in the field: a list of the workspace's files (quick open's) and @diagnostics to pick the context from
    private void AiEditPickContext()
    {
        var choices = new List<UIChoice>
        {
            new("@diagnostics", () => AiEditAddContext(null))
            {
                Detail = Said("ai_edit.mention_diagnostics_detail"), Icon = "lightbulb", Id = "@diagnostics"
            },
        };
        foreach (string file in AllWorkspaceFiles())
        {
            string shown = Relative(file);
            string folder = Path.GetDirectoryName(shown)?.Replace('\\', '/') ?? string.Empty;
            choices.Add(new UIChoice(Path.GetFileName(shown), () => AiEditAddContext(shown))
            {
                Detail = folder, Icon = HexExplorer.FileIcon(file), IconColor = explorer.FileIconColor(file), Id = shown
            });
        }
        ChooseAt(Said("ai_edit.mention_title"), Said("ai_edit.mention_prompt"), choices, aiEditField is { } field ? AnchorOf(field) : null,
            escaped: () => aiEditField?.Focus());
    }

    // A file (or, with null, the diagnostics) added to the context, the @ that asked for it taken out of the field
    private void AiEditAddContext(string? file)
    {
        if (aiEditState != AiEditState.Asking)
            return;
        if (file is null)
            aiEditDiagnostics = true;
        else if (!aiEditFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            aiEditFiles.Add(file);
        if (aiEditField is { } field)
            field.Text = InstructMentions.WithoutTyping(field.Text);
        AiEditRebuildBox();
    }

    // The files added to the edit as the prompt carries them, each capped to its tokens and cut round the target's names when long
    private List<InstructContext> AiEditContextBlocks()
    {
        var blocks = new List<InstructContext>();
        if (aiEditFiles.Count == 0)
            return blocks;

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in AllWorkspaceFiles())
            byName[Relative(file)] = file;
        List<string> terms = InstructMentions.Terms(string.Join('\n', aiEditTargets.Select(target => aiEditText[target.Start..target.End])));
        foreach (string name in aiEditFiles)
        {
            string? path = byName.TryGetValue(name, out string? found) ? found
                : byName.FirstOrDefault(pair => pair.Key.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) || pair.Key.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)).Value;
            if (path is null)
                continue;
            try
            {
                blocks.Add(new InstructContext(name, InstructMentions.Cap(File.ReadAllText(path), terms)));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return blocks;
    }

    /// <summary>
    /// The box's field. Escape reaches the compositor before the field's own keys, which let go of the focus and so the
    /// field never saw it (the box stayed up with the keyboard on nothing): the field says what Escape does itself.
    /// </summary>
    private sealed class AiEditBox(Action escaped) : TextBox
    {
        protected override bool OnEscape()
        {
            escaped();
            return true;
        }
    }

    private TextBox MakeAiEditField()
    {
        var field = new AiEditBox(() => AiEditClose(restore: true))
        {
            Placeholder = Said("ai_edit.placeholder"), TextScale = 0.14f, MaxLength = 600, Anchor = Origin.Left,
            Tooltip = Said("ai_edit.field_tip")
        };
        field.OnSubmitted = text =>
        {
            if (aiEditState == AiEditState.Asking)
                RunAiEdit(text);
            else if (aiEditState == AiEditState.Failed)
                AiEditRetry();
        };
        field.OnChanged = text =>
        {
            if (aiEditWalk >= 0 && (aiEditWalk >= aiEditRecent.Count || aiEditRecent[aiEditWalk] != text))
                aiEditWalk = -1;
            // An @ that starts a word opens the list of what to add
            if (aiEditState == AiEditState.Asking && InstructMentions.Typing(text) is { Length: 0 })
                AiEditPickContext();
        };
        field.TakeKey = key =>
        {
            switch (key)
            {
                case UIKeyboard.ESCAPE:
                    AiEditClose(restore: true);
                    return true;
                case UIKeyboard.UP or UIKeyboard.DOWN when aiEditState == AiEditState.Asking && aiEditRecent.Count > 0:
                    if (aiEditWalk < 0)
                        aiEditDraft = field.Text;
                    aiEditWalk = HexAiEditGlue.Walk(aiEditWalk, key == UIKeyboard.UP ? -1 : 1, aiEditRecent.Count);
                    field.Text = aiEditWalk >= 0 ? aiEditRecent[aiEditWalk] : aiEditDraft;
                    return true;
                default:
                    return false;
            }
        };
        return field;
    }

    // The width a text takes as a button's label
    private float AiEditWide(string text, float scale = 0.13f) =>
        Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine(text, scale, Horizon.UI.Drawing.UITextStyle.None, default) + skin.ButtonPadding.Total.X + 12.0f) : 96.0f;

    private Button AiEditButton(string label, Action pressed, string tip = "", bool primary = false, bool flat = false)
    {
        var button = new Button
        {
            Label = label, LabelScale = 0.13f, Size = new Vector2(AiEditWide(label), 24), Anchor = Origin.Left, Tooltip = tip,
            Primary = primary, OnPressed = pressed
        };
        if (flat)
        {
            button.Style = "button_flat";
            button.Animated = false;
        }
        return button;
    }

    // The panel and what's in it for the state, made again when the state, the width or the skin changes
    private void AiEditBuild(float width)
    {
        aiEditWidth = width;
        aiEditSkin = Skin;
        Panel panel = aiEditPanel ??= new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Radius = 6, Visible = false, Clip = true };
        if (Skin is { } skin)
        {
            panel.Color = skin.Palette.Panel with { W = 1 };
            panel.Outline = skin.BorderColor;
        }
        if (aiEditContent is { } old)
            panel.Remove(old);
        aiEditField?.Parent?.Remove(aiEditField);

        float inner = width - 16.0f;
        var content = new StackPanel { Direction = UIDirection.Vertical, Spacing = 4, Padding = new UIEdges(8, 6, 8, 6) };
        StackPanel Row(float height) => new() { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(inner, height) };
        Label Note(string text, float wide, float height = 18) => new()
        {
            Text = text, Align = Origin.Left, Anchor = Origin.Left, TextScale = 0.125f, Size = new Vector2(wide, height), Dim = true
        };

        TextBox field = aiEditField ??= MakeAiEditField();
        float close = 26.0f, icon = 20.0f;
        Button Close() => AiEditButton("$(close)", () => AiEditClose(restore: true), Said(aiEditState == AiEditState.Working ? "ai_edit.cancel_tip" : "ai_edit.close_tip"), flat: true);
        float height;

        switch (aiEditState)
        {
            case AiEditState.Asking:
            {
                StackPanel top = Row(28);
                top.Add(Note("$(sparkle)", icon, 28));
                field.Size = new Vector2(inner - icon - close - 12.0f, 26);
                top.Add(field);
                Button x = Close();
                x.Size = new Vector2(close, 24);
                top.Add(x);
                content.Add(top);

                // What was added with @, each a chip that takes itself out when pressed
                bool added = aiEditFiles.Count > 0 || aiEditDiagnostics;
                if (added)
                {
                    StackPanel context = Row(24);
                    float room = inner;
                    if (aiEditDiagnostics)
                    {
                        Button chip = AiEditButton("@diagnostics $(close)", () => { aiEditDiagnostics = false; AiEditRebuildBox(); },
                            Said("ai_edit.chip_remove", ("name", "diagnostics")), flat: true);
                        room -= chip.Size.X + 6.0f;
                        context.Add(chip);
                    }
                    foreach (string file in aiEditFiles)
                    {
                        string chosen = file;
                        Button chip = AiEditButton("@" + Path.GetFileName(chosen) + " $(close)", () => { aiEditFiles.Remove(chosen); AiEditRebuildBox(); },
                            Said("ai_edit.chip_remove", ("name", LinkTalkLiteral(chosen))), flat: true);
                        if (chip.Size.X > room)
                            break;
                        room -= chip.Size.X + 6.0f;
                        context.Add(chip);
                    }
                    content.Add(context);
                }

                StackPanel chips = Row(24);
                bool problems = AiEditAnyProblems();
                foreach (string action in HexAiEditGlue.QuickActions)
                {
                    if (action == "fix" && !problems)
                        continue;
                    string chosen = action;
                    chips.Add(AiEditButton(Said("ai_edit.action_" + chosen), () => RunAiEdit(HexAiEditGlue.Instruction(chosen)),
                        Said("ai_edit.action_tip", ("what", HexAiEditGlue.Instruction(chosen))), flat: true));
                }
                content.Add(chips);

                StackPanel foot = Row(20);
                if (AiEditModels.Resolve(settings, out string? why) is { } model)
                    // The model as a chip: pressed it opens the picker for the chat role
                {
                    Button chip = null!;
                    chip = AiEditButton(AiEditModelText(model),
                        () => PickModelFor(LocalAiRole.Chat, AnchorOf(chip), () => aiEditField?.Focus()), Said("local_ai_picker.title_chat"), flat: true);
                    foot.Add(chip);
                    if (aiEditTargets.Count > 1 && inner - chip.Size.X - 6.0f > 60.0f)
                        foot.Add(Note(Said("ai_edit.selections", ("count", aiEditTargets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), inner - chip.Size.X - 6.0f));
                }
                else
                {
                    string said = Said("ai_edit.problem_" + (why ?? "no_model"));
                    float wide = AiEditWide(Said("ai_edit.set_up"));
                    foot.Add(Note(LinkTalkLiteral(said), inner - wide - 6.0f));
                    foot.Add(AiEditButton(Said("ai_edit.set_up"), AiEditSetUpModel, flat: true));
                }
                content.Add(foot);
                height = 6 + 28 + 4 + 24 + 4 + 20 + 6 + (added ? 28 : 0);
                break;
            }
            case AiEditState.Working:
            {
                StackPanel row = Row(28);
                float status = MathF.Min(260.0f, inner * 0.5f);
                aiEditStatus = Note(string.Empty, status, 28);
                aiEditStatus.TextScale = 0.13f;
                aiEditStatus.Dim = false;
                row.Add(aiEditStatus);
                field.Size = new Vector2(inner - status - close - 12.0f, 26);
                row.Add(field);
                Button x = Close();
                x.Size = new Vector2(close, 24);
                row.Add(x);
                content.Add(row);
                height = 6 + 28 + 6;
                break;
            }
            case AiEditState.Reviewing:
            {
                StackPanel row = Row(28);
                int added = 0, removed = 0;
                for (int i = 0; i < aiEditParts.Count && i < aiEditRequests.Count; i++)
                {
                    if (aiEditParts[i]?.Replacement is { } replacement)
                    {
                        (int more, int fewer) = HexAiEditGlue.Lines(aiEditRequests[i].Target, replacement);
                        added += more;
                        removed += fewer;
                    }
                }
                string counted = Said("ai_edit.lines_changed", ("added", added.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("removed", removed.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                row.Add(Note(LinkTalkLiteral(counted), AiEditWide(counted, 0.125f), 28));
                row.Add(AiEditButton(Said("ai_edit.accept"), AiEditAccept, Said("ai_edit.accept_tip"), primary: true));
                row.Add(AiEditButton(Said("ai_edit.reject"), () => AiEditClose(restore: true), Said("ai_edit.reject_tip")));
                row.Add(AiEditButton(Said("ai_edit.retry"), AiEditRetry, Said("ai_edit.retry_tip")));
                row.Add(AiEditButton(Said("ai_edit.refine"), AiEditRefine, Said("ai_edit.refine_tip")));
                content.Add(row);
                height = 6 + 28 + 6;
                break;
            }
            default:
            {
                StackPanel top = Row(28);
                top.Add(Note("$(warning)", icon, 28));
                field.Size = new Vector2(inner - icon - close - 12.0f, 26);
                top.Add(field);
                Button x = Close();
                x.Size = new Vector2(close, 24);
                top.Add(x);
                content.Add(top);

                content.Add(new Label
                {
                    Text = LinkTalkLiteral(aiEditProblem), Align = Origin.TopLeft, Wrap = true, TextScale = 0.13f, Size = new Vector2(inner, 34)
                });

                StackPanel buttons = Row(24);
                buttons.Add(AiEditButton(Said("ai_edit.retry"), AiEditRetry, Said("ai_edit.retry_tip"), primary: !aiEditSetUp));
                if (aiEditSetUp)
                    buttons.Add(AiEditButton(Said("ai_edit.set_up"), AiEditSetUpModel, primary: true));
                content.Add(buttons);
                height = 6 + 28 + 4 + 34 + 4 + 24 + 6;
                break;
            }
        }

        content.Size = new Vector2(width, height);
        panel.Size = new Vector2(width, height);
        panel.Add(content);
        aiEditContent = content;
    }
}
