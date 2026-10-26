using System.Numerics;

namespace Hex.Dawn;

/// <summary>
/// The pointer an agent moves, in place of the mouse for the compositors it's handed to while it has it. It's taken when
/// a tool clicks, hovers or scrolls, and given back to the real mouse the moment somebody moves that, so a person at the
/// machine never finds theirs dead. Whatever the compositors were listening to before (the self test's pointer) comes back.
/// </summary>
internal sealed class HexAgentPointer(UICompositor[] compositors)
{
    private readonly Func<UIPointer>?[] before = new Func<UIPointer>?[compositors.Length];
    private Vector2 mouseWhenTaken;

    /// <summary>Where it is, in the world the compositors' camera looks at.</summary>
    public Vector2 Position { get; private set; }

    public bool Down { get; private set; }
    public bool Secondary { get; private set; }

    /// <summary>Whether it has the compositors right now.</summary>
    public bool Taken { get; private set; }

    /// <summary>Takes the compositors from the mouse (or whoever had them), the mouse where it is now kept to see it move.</summary>
    public void Take(Vector2 mouseNow)
    {
        if (Taken)
            return;

        for (int i = 0; i < compositors.Length; i++)
        {
            before[i] = compositors[i].PointerSource;
            compositors[i].PointerSource = Read;
        }

        mouseWhenTaken = mouseNow;
        Taken = true;
    }

    /// <summary>Gives the compositors back, nothing held.</summary>
    public void Release()
    {
        if (!Taken)
            return;

        for (int i = 0; i < compositors.Length; i++)
            compositors[i].PointerSource = before[i];
        Down = Secondary = false;
        Taken = false;
    }

    /// <summary>Puts it somewhere, with the buttons held or not.</summary>
    public void Set(Vector2 at, bool down = false, bool secondary = false)
    {
        Position = at;
        Down = down;
        Secondary = secondary;
    }

    /// <summary>Every tick, given back once the real mouse has moved a couple of pixels and nothing of the agent's is held.</summary>
    public void Update(Vector2 mouseNow)
    {
        if (Taken && !Down && !Secondary && Vector2.DistanceSquared(mouseNow, mouseWhenTaken) > 4.0f)
            Release();
    }

    private UIPointer Read() => new(Position, Down, Secondary);
}
