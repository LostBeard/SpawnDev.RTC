namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform DTMF sender for sending telephone dial tones over WebRTC.
    /// </summary>
    public interface IRTCDTMFSender
    {
        /// <summary>
        /// Inserts DTMF tones to be sent to the remote peer.
        /// </summary>
        /// <param name="tones">String of DTMF characters (0-9, A-D, *, #, comma for pause).</param>
        /// <param name="duration">Duration of each tone in milliseconds (default 100, range 40-6000).</param>
        /// <param name="interToneGap">Gap between tones in milliseconds (default 70, minimum 30).</param>
        void InsertDTMF(string tones, int duration = 100, int interToneGap = 70);

        /// <summary>
        /// The tones remaining to be played. Empty string when complete.
        /// </summary>
        string ToneBuffer { get; }

        /// <summary>
        /// Fired when a tone has been played or the tone buffer has been emptied.
        /// </summary>
        event Action? OnToneChange;
    }
}
