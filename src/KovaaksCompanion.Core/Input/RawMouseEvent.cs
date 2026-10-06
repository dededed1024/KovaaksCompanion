namespace KovaaksCompanion.Core.Input;

public enum MouseButton : byte { Left = 0, Right = 1 }

public enum RawMouseKind : byte { Move, ButtonDown, ButtonUp }

/// <summary>One raw mouse input: relative counts (Move) or a button transition. <see cref="Qpc"/> is Stopwatch ticks.</summary>
public readonly record struct RawMouseEvent(long Qpc, RawMouseKind Kind, int Dx = 0, int Dy = 0, MouseButton Button = MouseButton.Left);
