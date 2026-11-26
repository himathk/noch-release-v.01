namespace Noch.Core;

public enum KeyAction { None, Start, Finish, Cancel }

/// Ctrl and Alt held on their own for a moment starts listening; letting go of either finishes.
/// Any other key along the way means a shortcut was being typed, so nothing happens until both are let go.
public sealed class HoldKeys
{
    public TimeSpan Hold { get; init; } = TimeSpan.FromMilliseconds(350);
    DateTime _since = DateTime.MaxValue;
    bool _spoiled;

    public KeyAction Step(bool ctrl, bool alt, bool other, bool listening, DateTime now)
    {
        if (!(ctrl && alt))
        {
            _since = DateTime.MaxValue; _spoiled = false;
            return listening ? KeyAction.Finish : KeyAction.None;
        }
        if (listening)
        {
            if (!other) return KeyAction.None;
            _spoiled = true;
            return KeyAction.Cancel;
        }
        if (_spoiled) return KeyAction.None;
        if (other) { _spoiled = true; return KeyAction.None; }
        if (_since == DateTime.MaxValue) { _since = now; return KeyAction.None; }
        if (now - _since < Hold) return KeyAction.None;
        // Once per hold: after it finishes, the keys must be let go before it starts again.
        _spoiled = true;
        return KeyAction.Start;
    }
}
