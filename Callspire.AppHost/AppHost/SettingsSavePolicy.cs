using System;

namespace Softphone.AppHost
{
    /// <summary>
    /// Decides whether saving settings needs a full SIP/WebRTC reconnect or only gateway/Kommo/UI refresh.
    /// </summary>
    public static class SettingsSavePolicy
    {
        public static bool RequiresTelephonyReconnect(AppSettings before, AppSettings after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));

            return !Eq(before.MainConnectionTransport, after.MainConnectionTransport)
                || !Eq(before.MainConnectionName, after.MainConnectionName)
                || !Eq(before.SipServer, after.SipServer)
                || !Eq(before.SipUsername, after.SipUsername)
                || !Eq(before.SipPasswordEncrypted, after.SipPasswordEncrypted)
                || before.SipUseTls != after.SipUseTls
                || before.SipUseSrtp != after.SipUseSrtp
                || before.UseWebRtcAudio != after.UseWebRtcAudio
                || !Eq(before.WebRtcWsUri, after.WebRtcWsUri)
                || !Eq(before.WebRtcUsername, after.WebRtcUsername)
                || !Eq(before.WebRtcPasswordEncrypted, after.WebRtcPasswordEncrypted)
                || !Eq(before.MainWebRtcTurnUri, after.MainWebRtcTurnUri)
                || !Eq(before.MainWebRtcTurnUsername, after.MainWebRtcTurnUsername)
                || !Eq(before.MainWebRtcTurnPasswordEncrypted, after.MainWebRtcTurnPasswordEncrypted)
                || !Eq(before.SecondaryConnectionTransport, after.SecondaryConnectionTransport)
                || !Eq(before.SecondaryConnectionName, after.SecondaryConnectionName)
                || !Eq(before.SipServer2, after.SipServer2)
                || !Eq(before.RtpServer2, after.RtpServer2)
                || !Eq(before.SipUsername2, after.SipUsername2)
                || !Eq(before.SipPasswordEncrypted2, after.SipPasswordEncrypted2)
                || before.SipUseTls2 != after.SipUseTls2
                || before.SipUseSrtp2 != after.SipUseSrtp2
                || !Eq(before.WebRtcWsUri2, after.WebRtcWsUri2)
                || !Eq(before.WebRtcUsername2, after.WebRtcUsername2)
                || !Eq(before.WebRtcPasswordEncrypted2, after.WebRtcPasswordEncrypted2)
                || !Eq(before.SecondaryWebRtcTurnUri, after.SecondaryWebRtcTurnUri)
                || !Eq(before.SecondaryWebRtcTurnUsername, after.SecondaryWebRtcTurnUsername)
                || !Eq(before.SecondaryWebRtcTurnPasswordEncrypted, after.SecondaryWebRtcTurnPasswordEncrypted)
                || before.EnableEchoCancellation != after.EnableEchoCancellation
                || Math.Abs(before.RingtoneVolume - after.RingtoneVolume) > 0.001
                || !Eq(before.RingtoneSoundMode, after.RingtoneSoundMode)
                || !Eq(before.AudioCodec, after.AudioCodec)
                || before.AudioSampleRate != after.AudioSampleRate
                || before.AudioBitrate != after.AudioBitrate
                || before.MicrophoneDeviceNumber != after.MicrophoneDeviceNumber
                || before.SpeakerDeviceNumber != after.SpeakerDeviceNumber;
        }

        private static bool Eq(string? a, string? b)
            => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
    }
}
