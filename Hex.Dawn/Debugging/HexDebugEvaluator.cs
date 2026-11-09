using System.Globalization;

using Horizon.UI.Scripting;

using static Hex.Dawn.HexDebugExpressions;

namespace Hex.Dawn;

/// <summary>
/// C# evaluated through netcoredbg the way it would be in the file it's stopped in, or refused in plain words where
/// netcoredbg would get it wrong. netcoredbg knows a type only by its whole name, can't see a const local, can't cast,
/// won't convert an argument to the parameter's type, and passes an enum's member to a call as the enum's first value. So
/// a short name is tried with the file's namespaces and usings in front until the debugger knows it (and remembered for
/// the session), a const is put in as what it's set to, a cast to a number in front is worked out here from what's cast,
/// whole numbers passed to a call are widened the way C# would, and a call given an enum's member isn't made at all. An
/// array's Length and a string's are worked out from what they come to, netcoredbg finds no member of either, and a
/// member a value really hasn't got is said plainly, with what it has that's nearest.
/// </summary>
/// <param name="raw">The debugger's own evaluation of an expression in a frame, in a context of DAP's.</param>
/// <param name="said">Hex's words for a key, with what goes in each of its {names}.</param>
/// <param name="members">The variables inside a value by its reference, what a member it hasn't got is told apart by.</param>
internal sealed class HexDebugEvaluator(Func<string, int, string, Task<DebugEvaluation>> raw, Func<string, (string Name, string Value)[], string> said,
    Func<int, Task<IReadOnlyList<DebugVariable>>>? members = null)
{
    private readonly Func<string, int, string, Task<DebugEvaluation>> ask = raw;
    private readonly Func<int, Task<IReadOnlyList<DebugVariable>>>? inside = members;

    // Hex's words for a key with what goes in its {what}
    private string Word(string key, string what) => said(key, [("what", what)]);

    private string Words(string key, params (string Name, string Value)[] values) => said(key, values);

    // A name the debugger didn't know and what it turned out to be, the same in every frame of a session
    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);

    // The names asked alone in a frame that the debugger didn't know as a value there (a type's short name), not asked
    // alone in it again. A local of one frame isn't one of the next, so it's by the frame, and let go of past a few hundred
    private readonly HashSet<(int Frame, string Name)> notValues = [];
    private const int MOST_NOT_VALUES = 256;

    // A hover, a watch and a condition are evaluated on threads of their own, at once
    private bool NotValue(int frame, string name)
    {
        lock (notValues)
            return notValues.Contains((frame, name));
    }

    private void NotAValue(int frame, string name)
    {
        lock (notValues)
        {
            if (notValues.Count >= MOST_NOT_VALUES)
                notValues.Clear();
            notValues.Add((frame, name));
        }
    }

    // How many questions one expression may ask of the debugger before it's given up on, a slow adapter and a long list
    // of usings never keep it going for ever
    private const int MOST_ASKED = 48;

    /// <summary>What an expression comes to in a frame of a file with that scope, or why not in words that say what to do.</summary>
    public async Task<DebugEvaluation> Evaluate(string expression, int frame, HexDebugScope scope, string context = "watch")
    {
        var asking = new Asking(this, frame, scope, context);
        string whole = expression.Trim();
        DebugEvaluation evaluated = LogicalParts(whole) is { } parts
            ? await asking.Logical(parts).ConfigureAwait(false)
            : await asking.Whole(whole, 0).ConfigureAwait(false);
        return evaluated.Value is { } value ? new DebugEvaluation(value with { Name = expression }, null) : evaluated;
    }

    // One expression's evaluation, with how many questions it has asked
    private sealed class Asking(HexDebugEvaluator of, int frame, HexDebugScope scope, string context)
    {
        private int asked;

        private async Task<DebugEvaluation> Ask(string expression)
        {
            asked++;
            return await of.ask(expression, frame, context).ConfigureAwait(false);
        }

        private bool Spent => asked >= MOST_ASKED;

        /// <summary>
        /// Operands of &amp;&amp; and || evaluated one at a time, left to right, stopping where C# would. netcoredbg evaluates both
        /// sides of either, args.Length == 1 &amp;&amp; args[0] == "x" with no args said 0x80070057 where it's false.
        /// </summary>
        public async Task<DebugEvaluation> Logical(List<List<string>> ors)
        {
            foreach (List<string> ands in ors)
            {
                bool all = true;
                foreach (string operand in ands)
                {
                    DebugEvaluation got = await Whole(operand, 0).ConfigureAwait(false);
                    if (got.Value is not { } value)
                        return got;
                    if (value.Value == "false")
                    {
                        all = false;
                        break;
                    }

                    if (value.Value != "true")
                        return new DebugEvaluation(null, of.Word("debugging.eval_not_bool", operand));
                }

                if (all)
                    return Bool(true);
            }

            return Bool(false);
        }

        private static DebugEvaluation Bool(bool value) => new(new DebugVariable(string.Empty, value ? "true" : "false", "bool", 0), null);

        public async Task<DebugEvaluation> Whole(string expression, int depth)
        {
            expression = KeywordTypes(expression);

            // A cast to a number is worked out from what's cast, netcoredbg has no casts at all
            if (depth < 4 && TopCast(expression) is var (type, inner))
            {
                DebugEvaluation cast = await Whole(inner, depth + 1).ConfigureAwait(false);
                if (cast.Value is not { } value)
                    return cast;
                return HexDebugExpressions.Cast(value, type, out string? why) is { } came
                    ? new DebugEvaluation(new DebugVariable(expression, came, type, 0), null)
                    : new DebugEvaluation(null, of.Word("debugging.eval_cast_" + why, type));
            }

            // Every argument of a call that's a chain of names worked out on its own first, so a call isn't made with an
            // enum's member netcoredbg would pass wrong, and short names are whole before the call is
            List<Call> calls = Calls(expression);
            if (calls.Count > 0)
            {
                foreach (Call call in calls)
                {
                    foreach (string argument in call.Arguments)
                    {
                        if (Spent || Chains(argument) is not [{ Called: false } chain] || chain.Start != 0 || chain.End != argument.Length || chain.Names.Length < 2)
                            continue;

                        var (written, value) = await Chain(argument).ConfigureAwait(false);
                        if (value.Value is { } got && IsEnumMember(written, got))
                            return new DebugEvaluation(null, of.Word("debugging.eval_enum_argument", argument));
                        if (written != argument && value.Value is not null)
                            expression = Replaced(expression, argument, written);
                    }
                }
            }

            return await Resolved(expression).ConfigureAwait(false);
        }

        // A chain of names alone, a.b.c with no call, and how it came to be written to be found
        private async Task<(string Written, DebugEvaluation Value)> Chain(string chain)
        {
            DebugEvaluation value = await Ask(Known(chain)).ConfigureAwait(false);
            if (value.Value is not null)
                return (Known(chain), value);

            string written = chain;
            for (int tries = 0; tries < 4 && value.Value is null && !Spent; tries++)
            {
                Failed failed = FailureOf(value.Error);
                if (failed.Kind != Failure.NoName || await Named(written, failed.Name).ConfigureAwait(false) is not { } named)
                    break;
                written = named.Written;
                value = named.Value;
            }

            return (written, value);
        }

        // An expression asked of the debugger, with what it couldn't find found and what it can't take written another way
        private async Task<DebugEvaluation> Resolved(string expression)
        {
            DebugEvaluation value = await Ask(expression).ConfigureAwait(false);
            string written = expression;
            for (int tries = 0; tries < 8 && value.Value is null && !Spent; tries++)
            {
                Failed failed = FailureOf(value.Error);
                (string Written, DebugEvaluation Value)? better = failed.Kind switch
                {
                    Failure.NoName => await Member(written, failed.Name).ConfigureAwait(false) ?? await Named(written, failed.Name).ConfigureAwait(false),
                    Failure.NoMethod => await Method(written).ConfigureAwait(false),
                    Failure.Arguments => await Widened(written).ConfigureAwait(false),
                    _ => null
                };

                if (better is not { } found)
                    break;
                written = found.Written;
                value = found.Value;
            }

            return value.Value is not null ? value : new DebugEvaluation(null, Plain(value.Error));
        }

        // A member of a value the debugger couldn't find, a.b.c with a.b worked out on its own first. An array's or a string's
        // Length is put in as what it comes to, netcoredbg finds no member of either, args.Length said "The name
        // 'args.Length' does not exist" with args right there. A member the value hasn't got is said to be missing, with
        // what it has that's nearest, TestCatalog.Tests.Count on an array said the same "does not exist" as a typo would.
        // Null when what's before the member isn't a value (a type, a namespace), the usings are looked through then
        private async Task<(string Written, DebugEvaluation Value)?> Member(string expression, string name)
        {
            string[] parts = [.. name.Split('.').Select(part => part.Trim())];
            if (parts is ["Length" or "LongLength"] && !Spent)
                return await LengthOfCall(expression, parts[0]).ConfigureAwait(false);
            if (parts.Length < 2 || Spent || SpanOf(expression, parts) is not var (start, end))
                return null;

            // A name found to be a type before, or found not to be a value, is the usings' to look through
            if (of.known.ContainsKey(parts[0]) || (parts.Length == 2 && of.NotValue(frame, parts[0])))
                return null;

            // A name alone is a local, an argument or a member of this, the debugger knows those as they're written
            string member = parts[^1];
            DebugEvaluation before = parts.Length == 2
                ? await Ask(parts[0]).ConfigureAwait(false)
                : (await Chain(string.Join(".", parts[..^1])).ConfigureAwait(false)).Value;
            if (before.Value is not { } value || value.Value == "null")
            {
                if (parts.Length == 2 && FailureOf(before.Error).Kind == Failure.NoName)
                    of.NotAValue(frame, parts[0]);
                return null;
            }

            if (member is "Length" or "LongLength" && LengthIn(value) is { } length)
                return await PutLength(expression, start, end, length, member == "LongLength").ConfigureAwait(false);

            IReadOnlyList<string> has = await MembersOf(value).ConfigureAwait(false);
            if (has.Count == 0 || has.Contains(member, StringComparer.Ordinal))
                return null;

            string type = ShortType(value.Type ?? "?");
            string said = NearestMember(member, has) is { } nearest
                ? of.Words("debugging.eval_no_member_has", ("member", member), ("type", type), ("has", nearest))
                : of.Words("debugging.eval_no_member", ("member", member), ("type", type));
            return (expression, new DebugEvaluation(null, said));
        }

        // The Length of what a call or an indexer comes to, s.Trim().Length, what's before it evaluated on its own first.
        // netcoredbg names the Length alone as what doesn't exist then
        private async Task<(string Written, DebugEvaluation Value)?> LengthOfCall(string expression, string member)
        {
            if (MemberOfCall(expression, member) is not var (start, dot, end))
                return null;

            DebugEvaluation of = await Whole(expression[start..dot].Trim(), 1).ConfigureAwait(false);
            return of.Value is { } value && LengthIn(value) is { } length
                ? await PutLength(expression, start, end, length, member == "LongLength").ConfigureAwait(false)
                : null;
        }

        // A Length worked out put in the expression in place of what it's the Length of, the expression asked again, or
        // what it is when it was the whole expression
        private async Task<(string Written, DebugEvaluation Value)> PutLength(string expression, int start, int end, long length, bool isLong)
        {
            string number = length.ToString(CultureInfo.InvariantCulture);
            if (start == 0 && end == expression.Length)
                return (expression, new DebugEvaluation(new DebugVariable(expression, number, isLong ? "long" : "int", 0), null));

            // In brackets where a member of it comes next, 3.ToString() isn't C#
            string literal = number + (isLong ? "L" : string.Empty);
            string put = expression[..start] + (expression[end..].TrimStart().StartsWith('.') ? $"({literal})" : literal) + expression[end..];
            return (put, await Ask(put).ConfigureAwait(false));
        }

        // The names of what a value has, an array's and a string's out of .NET's own, the rest the debugger's variables
        // inside it with its static and non-public ones. Empty when that can't be said
        private async Task<IReadOnlyList<string>> MembersOf(DebugVariable value)
        {
            if (BuiltInMembers(value.Type) is { } builtIn)
                return builtIn;
            if (value.Reference <= 0 || of.inside is not { } variables)
                return [];

            try
            {
                var names = new List<string>();
                foreach (DebugVariable child in await variables(value.Reference).ConfigureAwait(false))
                {
                    if (child.Name is "Static members" or "Non-Public members" && child.Reference > 0)
                        names.AddRange((await variables(child.Reference).ConfigureAwait(false)).Select(grouped => grouped.Name));
                    else
                        names.Add(child.Name);
                }

                return names;
            }
            catch (Exception)
            {
                // gone on since, nothing said of it
                return [];
            }
        }

        // A name the debugger didn't know found, a const of the file, an alias, or a type of a namespace the file uses
        private async Task<(string Written, DebugEvaluation Value)?> Named(string expression, string name)
        {
            string head = name.Split('.')[0].Trim();
            if (head.Length == 0)
                return null;

            // Asked the way it was found before in this session
            if (of.known.ContainsKey(head) && Known(expression) is var again && again != expression)
                return (again, await Ask(again).ConfigureAwait(false));

            if (name == head && scope.Constants.TryGetValue(head, out HexDebugConstant constant) && constant.Literal is { } literal)
            {
                string put = ReplaceHead(expression, head, literal);
                return put == expression ? null : (put, await Ask(put).ConfigureAwait(false));
            }

            if (scope.Aliases.TryGetValue(head, out string? alias))
                return await Try(expression, head, alias).ConfigureAwait(false);

            // Each place the name could be from, the name alone asked first (no call is made doing that), then the whole
            IEnumerable<string> from = name.Contains('.')
                ? scope.Namespaces.Select(space => $"{space}.{head}")
                : scope.StaticTypes.Select(type => $"{type}.{head}");
            foreach (string whole in from)
            {
                if (Spent)
                    break;

                string probe = whole + name[head.Length..];
                if ((await Ask(probe).ConfigureAwait(false)).Value is null)
                    continue;

                of.known[head] = whole;
                return await Try(expression, head, whole).ConfigureAwait(false);
            }

            return null;
        }

        // A call of a method the debugger couldn't find, its type's short name tried with each namespace the file uses
        private async Task<(string Written, DebugEvaluation Value)?> Method(string expression)
        {
            if (Known(expression) is var again && again != expression)
                return (again, await Ask(again).ConfigureAwait(false));

            foreach (Call call in Calls(expression))
            {
                string head = call.Callee.Head;
                IEnumerable<string> from = call.Callee.Names.Length > 1
                    ? scope.Namespaces.Select(space => $"{space}.{head}")
                    : scope.StaticTypes.Select(type => $"{type}.{head}");
                foreach (string whole in from)
                {
                    if (Spent)
                        return null;

                    string written = ReplaceHead(expression, head, whole);
                    DebugEvaluation value = await Ask(written).ConfigureAwait(false);

                    // Found once it says anything but that it found no method, a call it can't take the arguments of included
                    if (value.Value is not null || FailureOf(value.Error).Kind != Failure.NoMethod)
                    {
                        of.known[head] = whole;
                        return (written, value);
                    }
                }
            }

            return null;
        }

        // Whole numbers passed to a call widened until a method takes them, C#'s own order
        private async Task<(string Written, DebugEvaluation Value)?> Widened(string expression)
        {
            foreach (string written in WiderNumbers(expression))
            {
                if (Spent)
                    break;

                DebugEvaluation value = await Ask(written).ConfigureAwait(false);
                if (value.Value is not null)
                    return (written, value);
            }

            return null;
        }

        private async Task<(string Written, DebugEvaluation Value)?> Try(string expression, string head, string with)
        {
            string written = ReplaceHead(expression, head, with);
            return written == expression ? null : (written, await Ask(written).ConfigureAwait(false));
        }

        // An expression with every name found before in this session written the way it was found
        private string Known(string expression)
        {
            foreach (Chain chain in Chains(expression).AsEnumerable().Reverse())
            {
                if (chain.Names.Length > 1 && of.known.TryGetValue(chain.Head, out string? whole))
                    expression = ReplaceChain(expression, chain, whole + string.Concat(chain.Names.Skip(1).Select(name => "." + name)));
            }

            return expression;
        }

        // What netcoredbg said in words that say what to do about it, its own words where they say enough
        private string? Plain(string? error)
        {
            Failed failed = FailureOf(error);
            return failed.Kind switch
            {
                Failure.NoMethod => of.Word("debugging.eval_no_method", string.Empty),
                Failure.Arguments => of.Word("debugging.eval_arguments", string.Empty),
                Failure.NotDone => of.Word("debugging.eval_not_done", NotDoneKey(failed.Name) is { Length: > 0 } key ? of.Word(key, string.Empty) : failed.Name),
                _ => error?.Replace("error: ", string.Empty, StringComparison.Ordinal)
            };
        }
    }

    // An argument's text written another way, every place it is in the expression as a whole argument
    private static string Replaced(string expression, string argument, string with)
    {
        foreach (Chain chain in Chains(expression).AsEnumerable().Reverse())
        {
            if (expression[chain.Start..chain.End] == argument)
                expression = ReplaceChain(expression, chain, with);
        }

        return expression;
    }
}
