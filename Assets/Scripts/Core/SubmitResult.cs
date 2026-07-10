namespace Pascension.Engine.Core
{
    /// <summary>Outcome of submitting a PlayerAction to a game engine.</summary>
    public readonly struct SubmitResult
    {
        public readonly bool Accepted;
        public readonly string Error;

        private SubmitResult(bool accepted, string error)
        {
            Accepted = accepted;
            Error = error;
        }

        public static SubmitResult Ok() => new(true, null);
        public static SubmitResult Rejected(string error) => new(false, error);
    }
}
