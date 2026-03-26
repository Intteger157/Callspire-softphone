using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// AudioSink wrapper that filters out non-audio RTP payload types (DTMF, Comfort Noise, etc.)
    /// before they reach the real audio decoder (WindowsAudioEndPoint).
    /// 
    /// VoIPMediaSession internally calls GotAudioRtp for ALL audio-channel RTP packets,
    /// including telephone-event (DTMF, PT 101) and Comfort Noise (CN, PT 13).
    /// When these are decoded as PCM audio, they produce noise/clicks.
    /// This wrapper prevents that by only forwarding real audio codec packets.
    /// </summary>
    public class FilteringAudioSink : IAudioSink
    {
        private readonly IAudioSink _innerSink;
        private readonly HashSet<int> _allowedPayloadTypes;
        private int _filteredCount;
        private int _forwardedCount;
        private bool _loggedFirstFiltered;
        private readonly string _label;

        /// <summary>
        /// Creates a filtering audio sink.
        /// </summary>
        /// <param name="innerSink">The real audio sink to forward valid packets to</param>
        /// <param name="label">Label for logging (e.g. "[Connection2]")</param>
        public FilteringAudioSink(IAudioSink innerSink, string label = "")
        {
            _innerSink = innerSink ?? throw new ArgumentNullException(nameof(innerSink));
            _label = label;
            
            // Standard audio codec payload types that should be decoded as audio:
            // 0  = PCMU (G.711 μ-law)
            // 8  = PCMA (G.711 A-law)  
            // 9  = G.722
            // 18 = G.729
            // 3  = GSM
            // 4  = G.723
            // We do NOT include:
            // 13 = CN (Comfort Noise) - generates white noise when decoded as PCM
            // 101 = telephone-event (DTMF) - generates harsh noise when decoded as PCM
            _allowedPayloadTypes = new HashSet<int> { 0, 3, 4, 8, 9, 18 };
        }

        /// <summary>
        /// Sets the negotiated audio payload type after SDP negotiation.
        /// This overrides the default allowed set - only this PT and the defaults will pass.
        /// </summary>
        public void SetNegotiatedPayloadType(int payloadType)
        {
            _allowedPayloadTypes.Add(payloadType);
            MainWindow.Log($"[FilteringAudioSink] {_label}Added negotiated PT {payloadType} to allowed set");
        }

        public event SourceErrorDelegate? OnAudioSinkError
        {
            add => _innerSink.OnAudioSinkError += value;
            remove => _innerSink.OnAudioSinkError -= value;
        }

        public Task CloseAudioSink() => _innerSink.CloseAudioSink();
        public List<AudioFormat> GetAudioSinkFormats() => _innerSink.GetAudioSinkFormats();
        public Task StartAudioSink() => _innerSink.StartAudioSink();
        public Task PauseAudioSink() => _innerSink.PauseAudioSink();
        public Task ResumeAudioSink() => _innerSink.ResumeAudioSink();
        public void SetAudioSinkFormat(AudioFormat audioFormat) => _innerSink.SetAudioSinkFormat(audioFormat);
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _innerSink.RestrictFormats(filter);

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            if (_allowedPayloadTypes.Contains(payloadID))
            {
                // This is a real audio codec packet - forward to decoder
                Interlocked.Increment(ref _forwardedCount);
                _innerSink.GotAudioRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
            }
            else
            {
                // This is DTMF, Comfort Noise, or unknown - DROP it
                int count = Interlocked.Increment(ref _filteredCount);
                if (!_loggedFirstFiltered)
                {
                    _loggedFirstFiltered = true;
                    string ptName = payloadID switch
                    {
                        13 => "CN (Comfort Noise)",
                        101 => "telephone-event (DTMF)",
                        _ => $"Unknown ({payloadID})"
                    };
                    MainWindow.Log($"[FilteringAudioSink] {_label}FILTERED first non-audio RTP packet: PT={payloadID} ({ptName}), len={payload?.Length ?? 0}. " +
                                   $"These packets would cause noise if decoded as audio.");
                }
                // Log stats periodically
                if (count % 500 == 0)
                {
                    MainWindow.Log($"[FilteringAudioSink] {_label}Stats: {_forwardedCount} audio packets forwarded, {_filteredCount} non-audio packets filtered");
                }
            }
        }
    }
}
