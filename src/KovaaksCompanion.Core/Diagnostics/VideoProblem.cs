namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>A failure that is not an exception (libobs error line, capture that delivers no frames) so <see cref="ErrorReporter"/> can send it.</summary>
public sealed class VideoProblem(string message) : Exception(message);
