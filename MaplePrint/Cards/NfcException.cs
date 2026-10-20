namespace MaplePrint.Cards;

/// <summary>
/// Something about the reader or the card stopped a read or a write. The message is written for
/// the person at the counter ("the card was lifted off the reader"), because the activity log and the
/// API's answers are where they read it.
/// </summary>
public sealed class NfcException : Exception
{
    /// <summary>The PC/SC return code or the card's status word, when there was one; 0 otherwise.</summary>
    public int Code { get; }

    public NfcException(string message, int code = 0) : base(message) => Code = code;
}
