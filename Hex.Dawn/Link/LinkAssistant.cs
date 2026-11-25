namespace Hex.Dawn;

/// <summary>
/// What the model answering in Link Chat is asked with: the server's URL, the model, the system prompt and the tuning, and
/// the kind of server (ollama, or openai for any server with OpenAI's chat API, llama.cpp's llama-server among them) with
/// its key.
/// </summary>
internal sealed record LinkAssistantOptions(string ApiUrl, string Model, string SystemPrompt, PredictionTuning Tuning)
{
    /// <summary>ollama or openai.</summary>
    public string Provider { get; init; } = PredictionProviders.OLLAMA;

    /// <summary>The key an OpenAI compatible server wants, null for none. Never written out.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The key is left out, so that writing the options somewhere never writes it.</summary>
    public override string ToString() =>
        $"LinkAssistantOptions {{ Provider = {Provider}, ApiUrl = {ApiUrl}, Model = {Model}, SystemPrompt = {SystemPrompt.Length} characters, Tuning = {Tuning} }}";
}

/// <summary>
/// This Hex's model answering in Hex Link's chat. A message that starts with @ai is a question for it, from anyone in the
/// session, this device's person too; while the settings let it answer (<see cref="options"/> not null), the question
/// goes to the model with the questions and answers before it, and the answer is said in the chat from this device,
/// marked as the model's. Nothing else said in the chat is sent to the model. One question is answered at a time and at
/// most a few wait, the rest are let go. Free of the scene, so the tests run it with a model of their own.
/// </summary>
internal sealed class LinkAssistant : IDisposable
{
    /// <summary>What a question for the model starts with.</summary>
    public const string MENTION = "@ai";

    // The questions and answers kept for the next question, the most that wait for one before it, and the most messages
    // one answer is said in
    private const int MOST_HISTORY = 12, MOST_WAITING = 3, MOST_PARTS = 3;

    /// <summary>The system prompt when the settings give none.</summary>
    public const string USUAL_SYSTEM = "You are a helpful assistant answering questions in Hex Link Chat, a chat between people working together in the Hex code editor. "
        + "Each question comes as 'Name: question'. Answer the person asking, briefly and in plain text (Markdown isn't shown), in at most a few short paragraphs.";

    private readonly Func<LinkAssistantOptions?> options;
    private readonly Func<string, string?, string?, HexLinkTalkResult> say;

    /// <summary>What this device says in the chat when asked @ai with no model to answer, null to say nothing (answering off).</summary>
    public Func<string?>? NoModel { get; init; }

    /// <summary>
    /// Whether this device is the one to say it has no model for a question put by the device with the given id, null for
    /// always. With several devices in the chat that can't answer, one line (the designated device's) is said, not one each.
    /// </summary>
    public Func<string, bool>? SpeaksForNoModel { get; init; }

    /// <summary>How long the "no model" line waits for some device to answer first, zero to say it at once.</summary>
    public TimeSpan NoModelGrace { get; init; }

    private long lastAnswerTick;
    private readonly Func<string?> self;
    private readonly Func<LinkAssistantOptions, IReadOnlyList<ChatTurn>, CancellationToken, Task<ChatAnswer>> ask;
    private readonly Lock gate = new();
    private readonly List<ChatTurn> history = [];
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private int waiting;

    /// <param name="options">What the model is asked with now, null while it isn't to answer. Read on Link's threads.</param>
    /// <param name="say">Says an answer in the chat as the model named: the text, the model and what it thought, or null.</param>
    /// <param name="self">This device's id, whose model's answers are the assistant's own turns.</param>
    /// <param name="ask">Asks the model, its server's chat API when null.</param>
    public LinkAssistant(Func<LinkAssistantOptions?> options, Func<string, string?, string?, HexLinkTalkResult> say, Func<string?> self,
        Func<LinkAssistantOptions, IReadOnlyList<ChatTurn>, CancellationToken, Task<ChatAnswer>>? ask = null)
    {
        this.options = options;
        this.say = say;
        this.self = self;
        this.ask = ask ?? AskModel;
    }

    /// <summary>Why a question wasn't answered, said to this device's person only, on a thread of the assistant's.</summary>
    public Action<string>? Failed { get; init; }

    /// <summary>What is said in the chat when the model couldn't answer, null to say nothing there.</summary>
    public string? Unanswered { get; init; }

    /// <summary>What is said in the chat when a question is let go because too many wait already, null to say nothing there.</summary>
    public string? Busy { get; init; }

    /// <summary>What is said in the chat for a question waiting behind others, from its place (2 is next), null to say nothing there.</summary>
    public Func<int, string?>? Queued { get; init; }

    /// <summary>The questions being answered or waiting, for the tests.</summary>
    internal int Waiting { get { lock (gate) return waiting; } }

    /// <summary>
    /// A message heard in the chat, here or from another device: a question for the model answered, this model's answer
    /// kept for the next question, anything else let go.
    /// </summary>
    public void Heard(HexLinkTalk message)
    {
        if (stop.IsCancellationRequested)
            return;

        if (message.Model is not null)
        {
            // Any device's answer means the question was answered, so no "no model" notice follows it
            Interlocked.Exchange(ref lastAnswerTick, Environment.TickCount64);
            // Only this device's model's answers are its own turns. Another device's model is never asked anything
            if (message.Sender.Id == self())
                Remember(new ChatTurn("assistant", message.Text));
            return;
        }

        if (Question(message.Text) is not { } question)
            return;
        if (options() is not { } asked)
        {
            // Answering is on but there's no model to do it: the asker is told so in the chat, as a plain row of this device's
            if (SpeaksForNoModel?.Invoke(message.Sender.Id) != false && NoModel?.Invoke() is { } nothing && HexLinkTalk.Valid(nothing))
            {
                if (NoModelGrace <= TimeSpan.Zero)
                    say(nothing, null, null);
                else
                {
                    // Said only when no device answered within the grace: another one's model may be answering
                    long asked_at = Environment.TickCount64;
                    _ = Task.Delay(NoModelGrace, stop.Token).ContinueWith(_ =>
                    {
                        if (Interlocked.Read(ref lastAnswerTick) < asked_at)
                            say(nothing, null, null);
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }
            }
            return;
        }

        var put = new ChatTurn("user", $"{message.Sender.Name}: {question}");
        ChatTurn[] before;
        int ahead;
        lock (gate)
        {
            if (waiting >= MOST_WAITING)
            {
                // Let go, but never in silence: the asker is told the model is busy
                if (Busy is { } busy)
                    say(busy, asked.Model, null);
                return;
            }

            ahead = waiting;
            waiting++;
            before = [.. history];
            Remember(put);
        }
        // Behind other questions: said in the chat which place it is in
        if (ahead > 0 && Queued?.Invoke(ahead + 1) is { } place)
            say(place, asked.Model, null);
        _ = Answer(asked, before, put);
    }

    /// <summary>The question in a message for the model, the words after @ai; null when it isn't one or asks nothing.</summary>
    public static string? Question(string text)
    {
        string said = text.TrimStart();
        if (!said.StartsWith(MENTION, StringComparison.OrdinalIgnoreCase))
            return null;
        string rest = said[MENTION.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] is not (':' or ',' or '.'))
            return null;
        rest = rest.TrimStart(':', ',', '.').Trim();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>
    /// An answer cut into messages the chat takes: at most <see cref="MOST_PARTS"/>, each within its limit, cut at a line
    /// or a space where there is one, what's left over said to be cut. Characters the chat doesn't take are dropped.
    /// </summary>
    public static List<string> Parts(string answer)
    {
        string text = new([.. answer.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Where(c => !char.IsControl(c) || c is '\n' or '\t')]);
        text = text.Trim();
        var parts = new List<string>();
        const int most = HexLinkTalk.MOST_TEXT;
        while (text.Length > 0 && parts.Count < MOST_PARTS)
        {
            if (text.Length <= most)
            {
                parts.Add(text);
                text = string.Empty;
                break;
            }

            int room = parts.Count == MOST_PARTS - 1 ? most - 1 : most;
            int cut = text.LastIndexOf('\n', room - 1, room / 2);
            if (cut <= 0)
                cut = text.LastIndexOf(' ', room - 1, room / 2);
            if (cut <= 0)
                cut = char.IsHighSurrogate(text[room - 1]) ? room - 1 : room;
            parts.Add(text[..cut].TrimEnd());
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0 && parts.Count > 0)
            parts[^1] = parts[^1][..Math.Min(parts[^1].Length, HexLinkTalk.MOST_TEXT - 1)] + "…";
        parts.RemoveAll(part => !HexLinkTalk.Valid(part));
        return parts;
    }

    /// <summary>
    /// What the model thought as a message carries it: characters the chat doesn't take dropped, the start kept when it
    /// is too long and said to be cut. Null when it thought nothing.
    /// </summary>
    public static string? Thought(string? thinking)
    {
        if (thinking is null)
            return null;
        string text = new string([.. thinking.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Where(c => !char.IsControl(c) || c is '\n' or '\t')]).Trim();
        if (text.Length > HexLinkTalk.MOST_THINKING)
        {
            int end = HexLinkTalk.MOST_THINKING - 1;
            if (char.IsHighSurrogate(text[end - 1]))
                end--;
            text = text[..end] + "…";
        }
        return HexLinkTalk.ValidThinking(text) ? text : null;
    }

    private async Task Answer(LinkAssistantOptions asked, ChatTurn[] before, ChatTurn put)
    {
        bool entered = false;
        try
        {
            await turn.WaitAsync(stop.Token).ConfigureAwait(false);
            entered = true;
            var conversation = new List<ChatTurn>(before.Length + 2)
            {
                new("system", string.IsNullOrWhiteSpace(asked.SystemPrompt) ? USUAL_SYSTEM : asked.SystemPrompt)
            };
            conversation.AddRange(before);
            conversation.Add(put);

            ChatAnswer answer = await ask(asked, conversation, stop.Token).ConfigureAwait(false);
            List<string> parts = Parts(answer.Text);
            if (parts.Count == 0)
                throw new PredictionException(PredictionStatus.BadAnswer, "The model's answer was empty.");
            string? thought = Thought(answer.Thinking);
            for (int i = 0; i < parts.Count; i++)
            {
                // What it thought goes with the first part of the answer, folded above it
                HexLinkTalkResult result = say(parts[i], asked.Model, i == 0 ? thought : null);
                if (result != HexLinkTalkResult.Sent)
                {
                    Failed?.Invoke($"The model's answer couldn't be said in Link Chat ({result}).");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is PredictionException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            Failed?.Invoke(e.Message);
            if (Unanswered is { } sorry && !stop.IsCancellationRequested)
                say(sorry, asked.Model, null);
        }
        finally
        {
            if (entered)
                turn.Release();
            lock (gate)
                waiting--;
        }
    }

    // Kept for the next question, the oldest let go past the most. The caller may hold the gate already
    private void Remember(ChatTurn said)
    {
        lock (gate)
        {
            history.Add(said);
            if (history.Count > MOST_HISTORY)
                history.RemoveRange(0, history.Count - MOST_HISTORY);
        }
    }

    // The model asked through its server's chat API, Ollama's own or OpenAI's
    private static Task<ChatAnswer> AskModel(LinkAssistantOptions asked, IReadOnlyList<ChatTurn> conversation, CancellationToken cancel)
    {
        // A model on another linked device, asked over Link (Serve AI)
        if (asked.Provider == PredictionProviders.LINKAI)
            return new OpenAiChat(LinkAiRouter.Http, LinkAiRouter.Api, asked.Model, null) { Tuning = asked.Tuning }.Ask(conversation, cancel);

        if (asked.Provider == PredictionProviders.OPENAI)
        {
            if (OpenAiChat.Url(asked.ApiUrl) is not { } chat)
                throw new PredictionException(PredictionStatus.Unreachable, "The server's URL isn't a http or https URL.");
            return new OpenAiChat(PredictionProviders.Shared, chat, asked.Model, string.IsNullOrWhiteSpace(asked.ApiKey) ? null : asked.ApiKey.Trim())
            {
                Tuning = asked.Tuning
            }.Ask(conversation, cancel);
        }

        if (OllamaChat.Url(asked.ApiUrl) is not { } url)
            throw new PredictionException(PredictionStatus.Unreachable, "The server's URL isn't a http or https URL.");
        return new OllamaChat(PredictionProviders.Shared, url, asked.Model) { Tuning = asked.Tuning }.Ask(conversation, cancel);
    }

    public void Dispose()
    {
        // The semaphore is left to the collector: an answer still running releases it as it ends
        if (!stop.IsCancellationRequested)
            stop.Cancel();
    }
}
