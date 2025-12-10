using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Windows;

namespace Softphone
{
    public class SipService : IDisposable
    {
        private readonly string _username;
        private readonly string _password;
        private readonly string _server;
        private readonly int _port;

        private SIPTransport? _sipTransport;
        private SIPUserAgent? _userAgent;
        private SIPRegistrationUserAgent? _regUserAgent;

        private WindowsAudioEndPoint? _audioEndPoint;
        private VoIPMediaSession? _voipMediaSession;

        public event Action<string>? OnStatusChanged;
        public event Action? OnCallEnded;

        public bool IsRegistered { get; private set; }
        public bool IsInCall => _userAgent?.IsCallActive == true;
        private bool _wasCallActive = false; // Флаг для отслеживания завершения звонка

        private int? _microphoneDeviceNumber;
        private int? _speakerDeviceNumber;

        public SipService(string username, string password, string server, int port = 5060, 
            int? microphoneDeviceNumber = null, int? speakerDeviceNumber = null)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _port = port;
            _microphoneDeviceNumber = microphoneDeviceNumber;
            _speakerDeviceNumber = speakerDeviceNumber;
        }

        private void SetStatus(string message)
        {
            OnStatusChanged?.Invoke(message);
        }

        private void InitializeAudio(bool throwOnError = false)
        {
            // Если аудио уже инициализировано, не делаем ничего
            if (_audioEndPoint != null && _voipMediaSession != null)
            {
                return;
            }

            // Закрываем предыдущие экземпляры, если они есть
            try
            {
                _voipMediaSession?.Close("reinitialize");
                _audioEndPoint?.CloseAudio();
            }
            catch
            {
                // Игнорируем ошибки при закрытии
            }

            _audioEndPoint = null;
            _voipMediaSession = null;

            try
            {
                SetStatus("Initializing audio...");
                
                // Используем только аудио кодировщик, без видео
                var audioEncoder = new AudioEncoder();
                
                // WindowsAudioEndPoint использует устройства по умолчанию Windows
                // Выбранные устройства сохраняются в настройках для будущего использования
                // Примечание: Для явного выбора устройств может потребоваться дополнительная настройка
                try
                {
                    // Пробуем создать аудио эндпоинт
                    // Если возникает ошибка с видео компонентами, это проблема совместимости версий
                    _audioEndPoint = new WindowsAudioEndPoint(audioEncoder);
                }
                catch (MissingMethodException ex)
                {
                    // Ошибка совместимости версий - метод не найден
                    string errorMsg = $"Library version incompatibility detected. Please ensure SIPSorcery and SIPSorceryMedia.Windows versions match. Error: {ex.Message}";
                    if (ex.Message.Contains("VideoTestPatternSource") || ex.Message.Contains("OnVideoSourceRawSampleFaster"))
                    {
                        errorMsg += "\n\nSolution: Update both SIPSorcery and SIPSorceryMedia.Windows to the same version (recommended: 6.1.0 or later).";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                catch (TypeLoadException ex)
                {
                    string errorMsg = $"Failed to load audio endpoint type. This is likely due to library version incompatibility. Error: {ex.Message}";
                    if (ex.Message.Contains("VideoTestPatternSource"))
                    {
                        errorMsg += "\n\nSolution: Update both SIPSorcery and SIPSorceryMedia.Windows to matching versions.";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                catch (Exception ex)
                {
                    // Перехватываем все остальные исключения для лучшей диагностики
                    string errorMsg = $"Failed to create WindowsAudioEndPoint: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMsg += $" ({ex.InnerException.Message})";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                
                if (_audioEndPoint == null)
                {
                    throw new InvalidOperationException("Failed to create WindowsAudioEndPoint");
                }
                
                var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                if (mediaEndPoints == null)
                {
                    throw new InvalidOperationException("No media endpoints available");
                }
                
                _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                {
                    AcceptRtpFromAny = true
                };
                
                if (_voipMediaSession == null)
                {
                    throw new InvalidOperationException("Failed to create VoIPMediaSession");
                }
                
                string deviceInfo = "";
                if (_microphoneDeviceNumber.HasValue || _speakerDeviceNumber.HasValue)
                {
                    deviceInfo = " (selected devices will be used on next restart)";
                }
                SetStatus($"Audio initialized successfully{deviceInfo}");
            }
            catch (Exception ex)
            {
                string errorMessage = $"Audio initialization error: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $" ({ex.InnerException.Message})";
                }
                errorMessage += ". Please check your audio devices are connected and not being used by another application.";
                
                SetStatus(errorMessage);
                
                // Продолжаем без аудио для тестирования регистрации
                _audioEndPoint = null;
                _voipMediaSession = null;
                
                // Если требуется пробросить исключение (при звонке), делаем это
                if (throwOnError)
                {
                    throw new InvalidOperationException($"Audio service not initialized. {ex.Message}. Please check your audio devices and try again.", ex);
                }
            }
        }

        /// <summary>
        /// ������������� SIP ����������, ����� � ����������� �� �������.
        /// </summary>
        public async Task StartAsync()
        {
            if (_sipTransport != null)
            {
                // ��� ����������������.
                return;
            }

            SetStatus("Initializing SIP...");

            // 1. SIP ���������.
            _sipTransport = new SIPTransport();

            // UDP-����� �� ����� IP, ���� ����� ��������� �������������.
            var listenEndPoint = new IPEndPoint(IPAddress.Any, 0);
            _sipTransport.AddSIPChannel(new SIPUDPChannel(listenEndPoint));

            // 2. UserAgent ��� �������.
            _userAgent = new SIPUserAgent(_sipTransport, outboundProxy: null);

            // На входящий звонок пока просто отклоняем (заглушка).
            _userAgent.OnIncomingCall += async (ua, req) =>
            {
                SetStatus("Incoming call - rejecting (not implemented yet).");
                // Отправляем ответ Busy Here через транспорт
                var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, "Not implemented");
                if (_sipTransport != null)
                {
                    await _sipTransport.SendResponseAsync(busyResponse);
                }
            };

            // 3. Аудио и медиа-сессия.
            // Создаем аудио эндпоинт с кодировщиком
            InitializeAudio();

            // 4. Регистрация на SIP-сервере (MikoPBX).
            // Формируем правильный адрес сервера с портом
            var serverAddress = _server.Contains(":") 
                ? _server 
                : $"{_server}:{_port}";
            
            // Используем упрощенный конструктор SIPRegistrationUserAgent
            // который принимает: transport, username, password, server, expiry
            _regUserAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _username,
                _password,
                serverAddress,
                expiry: 300);

            _regUserAgent.RegistrationFailed += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                SetStatus($"Registration failed: {errorMessage}");
            };

            _regUserAgent.RegistrationTemporaryFailure += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                SetStatus($"Registration temporary failure: {errorMessage}");
            };

            _regUserAgent.RegistrationRemoved += (uri, response) =>
            {
                IsRegistered = false;
                SetStatus("Registration removed.");
            };

            _regUserAgent.RegistrationSuccessful += (uri, response) =>
            {
                IsRegistered = true;
                SetStatus("Registration successful!");
            };

            SetStatus("Registering on SIP server...");
            _regUserAgent.Start();

            // ��������� �����, ����� ���� ����������� ���� ����������.
            await Task.Delay(500);
        }

        /// <summary>
        /// ��������� ������ �� ��������� �����.
        /// </summary>
        public async Task CallAsync(string number)
        {
            if (_userAgent == null)
            {
                SetStatus("SIP not initialized. Click Connect.");
                return;
            }
            
            // Инициализируем аудио, если оно не было инициализировано
            if (_voipMediaSession == null)
            {
                SetStatus("Audio not initialized. Attempting to initialize...");
                InitializeAudio(throwOnError: true);
                
                // Проверяем еще раз после попытки инициализации
                if (_voipMediaSession == null)
                {
                    string errorMsg = "Audio initialization failed. Please check:\n1. Microphone and speaker are connected\n2. Audio devices are not being used by another application\n3. Audio drivers are installed correctly";
                    SetStatus(errorMsg);
                    throw new InvalidOperationException("Audio service not initialized. Please check your audio devices and try again.");
                }
            }
            else if (_wasCallActive)
            {
                // Если был предыдущий звонок, полностью переинициализируем аудио перед новым звонком
                SetStatus("Reinitializing audio for new call...");
                try
                {
                    // Закрываем старую медиа-сессию
                    try
                    {
                        _voipMediaSession?.Close("new call");
                    }
                    catch
                    {
                        // Игнорируем ошибки при закрытии
                    }
                    _voipMediaSession = null;
                    
                    // Полностью закрываем и пересоздаем аудио эндпоинт
                    try
                    {
                        _audioEndPoint?.CloseAudio();
                    }
                    catch
                    {
                        // Игнорируем ошибки при закрытии
                    }
                    _audioEndPoint = null;
                    
                    // Задержка для полного завершения закрытия
                    System.Threading.Thread.Sleep(500);
                    
                    // Полностью переинициализируем аудио
                    InitializeAudio(throwOnError: true);
                    
                    if (_voipMediaSession == null)
                    {
                        throw new InvalidOperationException("Failed to reinitialize audio for new call");
                    }
                    
                    SetStatus("Audio reinitialized and ready for new call");
                }
                catch (Exception ex)
                {
                    SetStatus($"Error reinitializing audio: {ex.Message}");
                    throw new InvalidOperationException($"Failed to reinitialize audio for new call: {ex.Message}", ex);
                }
                _wasCallActive = false;
            }

            if (_userAgent.IsCallActive)
            {
                SetStatus("Call already active.");
                return;
            }

            if (string.IsNullOrWhiteSpace(number))
            {
                SetStatus("Enter number for call.");
                return;
            }

            // Формируем адрес назначения для звонка через MikoPBX
            string destination;
            if (number.Contains("@"))
            {
                destination = number;
            }
            else
            {
                // Для MikoPBX используем формат: sip:number@server:port
                var serverPart = _server.Contains(":") ? _server : $"{_server}:{_port}";
                destination = $"sip:{number}@{serverPart}";
            }

            SetStatus($"Calling {destination}...");

            try
            {
                // �����: ����� ������������ ���������� Call(string dst, string username, string password, IMediaSession mediaSession, int ringTimeout = 0)
                bool callResult = await _userAgent.Call(
                    destination,
                    _username,
                    _password,
                    _voipMediaSession);

                if (callResult)
                {
                    SetStatus("Call connected!");
                    _wasCallActive = true; // Устанавливаем флаг при успешном подключении
                    // �����-������ ���� �������� ������ SIPUserAgent.
                }
                else
                {
                    SetStatus("Call failed (timeout/rejected).");
                    _wasCallActive = false; // Сбрасываем флаг при неудаче
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Call error: {ex.Message}");
            }
        }

        /// <summary>
        /// ���������� ��������� ������.
        /// </summary>
        public void Hangup()
        {
            if (_userAgent == null)
            {
                return;
            }

            if (_userAgent.IsCallActive)
            {
                SetStatus("Hanging up call...");
                try
                {
                    _userAgent.Hangup();
                    
                    // Устанавливаем флаг, что звонок был активен
                    _wasCallActive = true;
                    
                    // Закрываем медиа-сессию после завершения звонка
                    // Это необходимо для корректной работы аудио при следующем звонке
                    try
                    {
                        _voipMediaSession?.Close("hangup");
                    }
                    catch
                    {
                        // Игнорируем ошибки при закрытии
                    }
                    
                    SetStatus("Call ended");
                    OnCallEnded?.Invoke();
                }
                catch (Exception ex)
                {
                    SetStatus($"Hangup error: {ex.Message}");
                    throw;
                }
            }
            else
            {
                SetStatus("No active call.");
            }
        }

        private async System.Threading.Tasks.Task ReinitializeMediaSessionAsync()
        {
            try
            {
                SetStatus("Reinitializing media session...");
                
                // Закрываем текущую медиа-сессию
                try
                {
                    _voipMediaSession?.Close("reinitialize");
                }
                catch
                {
                    // Игнорируем ошибки при закрытии
                }
                _voipMediaSession = null;

                // Небольшая асинхронная задержка для завершения закрытия
                await System.Threading.Tasks.Task.Delay(200);

                // Если аудио эндпоинт существует, создаем новую медиа-сессию
                if (_audioEndPoint != null)
                {
                    var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                    if (mediaEndPoints != null)
                    {
                        _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                        {
                            AcceptRtpFromAny = true
                        };
                        SetStatus("Media session reinitialized");
                    }
                    else
                    {
                        SetStatus("Warning: No media endpoints available for reinitialization");
                    }
                }
                else
                {
                    SetStatus("Warning: Audio endpoint not available for reinitialization");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Warning: Failed to reinitialize media session: {ex.Message}");
                // Не пробрасываем исключение, чтобы не блокировать завершение звонка
            }
        }

        public void Dispose()
        {
            try
            {
                _regUserAgent?.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                _userAgent?.Hangup();
                _userAgent?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _voipMediaSession?.Close("dispose");
            }
            catch
            {
                // ignore
            }

            try
            {
                _audioEndPoint?.CloseAudio();
            }
            catch
            {
                // ignore
            }

            try
            {
                _sipTransport?.Shutdown();
            }
            catch
            {
                // ignore
            }
        }
    }
}
