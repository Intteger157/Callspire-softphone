using System;
using System.Threading.Tasks;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorceryMedia.Windows;

namespace Softphone
{
    public class SipService : IDisposable
    {
        private SIPTransport? _sipTransport;
        private SIPUserAgent? _userAgent;
        private SIPRegistrationUserAgent? _registrationAgent;
        private WindowsAudioEndPoint? _audioEndPoint;
        private bool _isDisposed = false;
        private SIPUserAgentCall? _currentCall;

        private readonly string _username;
        private readonly string _password;
        private readonly string _server;

        public event Action<string>? OnStatusChanged;

        public SipService(string username, string password, string server)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public async Task StartAsync()
        {
            try
            {
                OnStatusChanged?.Invoke("Initializing...");

                // Create SIP transport
                _sipTransport = new SIPTransport();
                _sipTransport.AddSIPChannel(new SIPUDPChannel(System.Net.IPAddress.Any, 0));

                // Create audio endpoint
                _audioEndPoint = new WindowsAudioEndPoint(new WindowsAudioEndPointOptions
                {
                    AudioSourceOptions = new AudioSourceOptions
                    {
                        AudioSource = AudioSourcesEnum.Microphone
                    },
                    AudioSinkOptions = new AudioSinkOptions
                    {
                        AudioSink = AudioSinksEnum.Speaker
                    }
                });

                await _audioEndPoint.StartAudio();

                OnStatusChanged?.Invoke("Audio initialized");

                // Create user agent
                _userAgent = new SIPUserAgent(_sipTransport, null);

                // Register on SIP server
                var serverUri = SIPURI.ParseSIPURI($"sip:{_server}");
                var fromURI = new SIPURI(_username, serverUri.Host, null, SIPSchemesEnum.sip, serverUri.Port);
                var toURI = fromURI.CopyOf();

                _registrationAgent = new SIPRegistrationUserAgent(
                    _sipTransport,
                    fromURI,
                    toURI,
                    _password,
                    null,
                    null);

                _registrationAgent.RegistrationFailed += (uri, response) =>
                {
                    OnStatusChanged?.Invoke($"Registration failed: {response?.ReasonPhrase ?? "Unknown error"}");
                };

                _registrationAgent.RegistrationTemporaryFailure += (uri, response) =>
                {
                    OnStatusChanged?.Invoke($"Registration temporary failure: {response?.ReasonPhrase ?? "Unknown error"}");
                };

                _registrationAgent.RegistrationRemoved += (uri, response) =>
                {
                    OnStatusChanged?.Invoke("Registration removed");
                };

                _registrationAgent.RegistrationSuccessful += (uri, response) =>
                {
                    OnStatusChanged?.Invoke("Registration successful");
                };

                // Handle incoming calls
                _userAgent.OnIncomingCall += async (ua, req) =>
                {
                    OnStatusChanged?.Invoke("Incoming call - rejecting for now");
                    await ua.Reject();
                };

                // Start registration
                _registrationAgent.Start();

                OnStatusChanged?.Invoke("Registering...");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Error: {ex.Message}");
                throw;
            }
        }

        public async Task CallAsync(string number)
        {
            if (_userAgent == null || _sipTransport == null || _audioEndPoint == null)
            {
                throw new InvalidOperationException("SIP service not initialized. Call StartAsync first.");
            }

            try
            {
                OnStatusChanged?.Invoke($"Calling {number}...");

                var serverUri = SIPURI.ParseSIPURI($"sip:{_server}");
                var callUri = new SIPURI(number, serverUri.Host, null, SIPSchemesEnum.sip, serverUri.Port);

                var callOptions = new SIPCallDescriptor(
                    callUri.ToString(),
                    _username,
                    _username,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    SIPCallDirection.Out,
                    SIPConstants.SIP_CONTENT_TYPE_SDP,
                    null,
                    null);

                _currentCall = await _userAgent.Call(callOptions);

                if (_currentCall != null && _currentCall.IsCallActive)
                {
                    // Attach audio
                    if (_currentCall.MediaSession != null && _audioEndPoint != null)
                    {
                        _currentCall.MediaSession.OnRtpPacketReceived += (mediaType, rtpPacket) =>
                        {
                            if (mediaType == SDPWellKnownMediaFormatsEnum.PCMU || mediaType == SDPWellKnownMediaFormatsEnum.PCMA)
                            {
                                _audioEndPoint.GotAudioRtp(rtpPacket.GetPayload(), rtpPacket.Header.SequenceNumber, rtpPacket.Header.Timestamp, rtpPacket.Header.PayloadType, rtpPacket.Header.Ssrc);
                            }
                        };

                        _currentCall.MediaSession.OnAudioFormatsNegotiated += (audioFormats) =>
                        {
                            _audioEndPoint.RestrictFormats(audioFormats);
                        };

                        _audioEndPoint.OnAudioSourceEncodedSample += (duration, timestamp, sample) =>
                        {
                            _currentCall.MediaSession.SendAudio((uint)duration.TotalMilliseconds, timestamp, sample);
                        };

                        await _currentCall.MediaSession.Start();
                        await _audioEndPoint.StartAudio();
                    }

                    OnStatusChanged?.Invoke($"Call connected to {number}");
                }
                else
                {
                    OnStatusChanged?.Invoke("Call failed");
                    throw new Exception("Call failed - no response or call not active");
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Call error: {ex.Message}");
                throw;
            }
        }

        public void Hangup()
        {
            try
            {
                _currentCall?.Hangup();
                _userAgent?.Hangup();
                _currentCall = null;
                OnStatusChanged?.Invoke("Call ended");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Hangup error: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                _currentCall?.Hangup();
                _userAgent?.Hangup();
                _registrationAgent?.Stop();
                _audioEndPoint?.StopAudio();
                _audioEndPoint?.Dispose();
                _sipTransport?.Shutdown();
            }
            catch
            {
                // Ignore disposal errors
            }

            _isDisposed = true;
        }
    }
}

