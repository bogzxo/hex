namespace Hex.Dawn;

/// <summary>
/// A part of the editor that has something to do every tick: the languages taking in what their servers said, Git
/// looking at what changed, the tasks watching what they started. It says what it's called and is ticked, in its place
/// among the steps of the editor (<see cref="HexTicks"/>).
/// </summary>
internal interface IHexPart
{
    /// <summary>What it's called where the time of a tick is written out (HEX_PROFILE), a word.</summary>
    string TickName { get; }

    /// <summary>One tick.</summary>
    void Tick(float dt);
}

/// <summary>
/// Steps of the editor's tick in the order they run, each with a name. What the editor does every tick was a run of
/// calls written out by hand in one method, forty of them, the parts' among the editor's own: the order was nowhere but
/// in the reading of that method, a part with a tick of its own had to be written into it, and the profile could only
/// say which of seven stretches of it was slow. Here the order is a list that is made once and can be read out, a part
/// adds itself with its name, and with the profile on every step is timed by that name.
/// <para>
/// A step is a method kept as a delegate, made once, so running the list makes no garbage. Nothing is added while it runs.
/// </para>
/// </summary>
internal sealed class HexTicks(string name, HexProfiler profile)
{
    private readonly List<(string Name, Action<float> Run)> steps = [];

    /// <summary>What this stretch of the tick is called, before the dot in a step's name in the profile.</summary>
    public string Name => name;

    /// <summary>The steps by name, in the order they run.</summary>
    public IEnumerable<string> Names => steps.Select(step => step.Name);

    public int Count => steps.Count;

    /// <summary>A step that goes by how long the tick was.</summary>
    public HexTicks Add(string step, Action<float> run)
    {
        steps.Add(($"{name}.{step}", run));
        return this;
    }

    /// <summary>A step that doesn't.</summary>
    public HexTicks Add(string step, Action run) => Add(step, _ => run());

    /// <summary>A part, under its own name.</summary>
    public HexTicks Add(IHexPart part) => Add(part.TickName, part.Tick);

    /// <summary>Every step, in order. With the profile on each is marked as it finishes, by its name.</summary>
    public void Run(float dt)
    {
        if (!profile.On)
        {
            for (int i = 0; i < steps.Count; i++)
                steps[i].Run(dt);
            return;
        }

        for (int i = 0; i < steps.Count; i++)
        {
            steps[i].Run(dt);
            profile.Mark(steps[i].Name);
        }
    }
}

// The parts that are ticked, each by the name the profile writes

internal sealed partial class HexLanguages : IHexPart
{
    string IHexPart.TickName => "languages";
    void IHexPart.Tick(float dt) => Update(dt);
}

internal sealed partial class HexGit : IHexPart
{
    string IHexPart.TickName => "git";
    void IHexPart.Tick(float dt) => Update(dt);
}

internal sealed partial class HexTasks : IHexPart
{
    string IHexPart.TickName => "tasks";
    void IHexPart.Tick(float dt) => Update(dt);
}

internal sealed partial class HexProperties : IHexPart
{
    string IHexPart.TickName => "properties";
    void IHexPart.Tick(float dt) => Update();
}

internal sealed partial class HexDebugging : IHexPart
{
    string IHexPart.TickName => "debugger";
    void IHexPart.Tick(float dt) => Update();
}
