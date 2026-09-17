import Foundation

struct ConnectionStatus: Codable, Hashable {
    var slot: String = "main"
    var label: String = ""
    var text: String = ""
    var isOnline: Bool = false
    var isError: Bool = false
    var isConfigured: Bool = false
    var isWebRtc: Bool = false
    var displayName: String = ""
}

struct CallerIdItem: Codable, Hashable, Identifiable {
    var number: String = ""
    var name: String = ""
    var displayText: String = ""
    var id: String { number }
}

struct HistoryItem: Codable, Hashable, Identifiable {
    var phoneNumber: String = ""
    var callTime: Date = Date()
    var phoneNumberDisplay: String = ""
    var callTimeText: String = ""
    var durationText: String = ""
    var status: String = ""
    var statusKind: String = "neutral"
    var isIncoming: Bool = false
    var isMissed: Bool = false
    var wasAnswered: Bool = false
    var transportLabel: String = ""
    var isWebRtc: Bool = false
    var connectionLabel: String = ""
    var hasRecording: Bool = false
    var outboundCallerId: String?
    var crmStatus: String = ""
    var id: String { "\(phoneNumber)|\(callTime.timeIntervalSince1970)" }
}

struct StatsRow: Codable, Hashable, Identifiable {
    var name: String = ""
    var detail: String = ""
    var phoneNumber: String = ""
    var id: String { name + detail }
}

struct DayBucket: Codable, Hashable, Identifiable {
    var date: Date = Date()
    var label: String = ""
    var total: Int = 0
    var answered: Int = 0
    var barHeight: Double = 0
    var toolTipText: String = ""
    var id: String { label }
}

struct HourBucket: Codable, Hashable, Identifiable {
    var hour: Int = 0
    var label: String = ""
    var total: Int = 0
    var barHeight: Double = 0
    var id: Int { hour }
}

struct StatisticsState: Codable {
    var periodOptions: [String] = []
    var directionOptions: [String] = []
    var connectionOptions: [String] = []
    var periodIndex: Int = 0
    var connectionIndex: Int = 0
    var directionIndex: Int = 0
    var customFrom: Date?
    var customTo: Date?
    var isCustomPeriod: Bool = false
    var secondaryEnabled: Bool = false
    var periodHint: String = ""
    var hasLegacyUnknownConnection: Bool = false
    var hasReport: Bool = false
    var kpiTotal: String = ""
    var kpiDirectionHint: String = ""
    var kpiAnswered: String = ""
    var kpiSuccessRate: String = ""
    var kpiUnanswered: String = ""
    var kpiUnansweredHint: String = ""
    var kpiTalkTime: String = ""
    var kpiAvgTalk: String = ""
    var kpiContactRate: String = ""
    var kpiRingTime: String = ""
    var kpiMissed: String = ""
    var kpiFailedCancelled: String = ""
    var peakHourText: String = ""
    var outcomeAnswered: String = ""
    var outcomeCancelled: String = ""
    var outcomeAgentCancel: String = ""
    var outcomeFailed: String = ""
    var outcomeMissed: String = ""
    var daily: [DayBucket] = []
    var hourly: [HourBucket] = []
    var connectionCompare: [StatsRow] = []
    var callerIds: [StatsRow] = []
    var topNumbers: [StatsRow] = []
}

struct MainState: Codable {
    var phoneNumber: String = ""
    var canCall: Bool = false
    var accountText: String = ""
    var accountToolTip: String = ""
    var anyOnline: Bool = false
    var showSplitCallButtons: Bool = false
    var splitPrimaryLabel: String = ""
    var splitSecondaryLabel: String = ""
    var sipAuthFailureText: String?
    var showCallerIdPicker: Bool = false
    var selectedCallerId: String?
    var historyFilterHint: String?
    var amoCrmConfigured: Bool = false
    var amoCrmStatusText: String = ""
    var amoCrmOnline: Bool = false
    var hasActiveCall: Bool = false
    var themeMode: String = "system"
    var main: ConnectionStatus = ConnectionStatus()
    var secondary: ConnectionStatus = ConnectionStatus()
    var callerIds: [CallerIdItem] = []
    var history: [HistoryItem] = []
    var statistics: StatisticsState = StatisticsState()
}

struct CallState: Codable {
    var sessionId: String = ""
    var callerDisplay: String = ""
    var statusText: String = ""
    var timerText: String = ""
    var statusTone: String = "neutral"
    var showIncomingButtons: Bool = false
    var showHangupButton: Bool = false
    var showControls: Bool = false
    var isMuted: Bool = false
    var isOnHold: Bool = false
    var isKeypadVisible: Bool = false
    var isRecordingIndicatorVisible: Bool = false
    var wasAnswered: Bool = false
}

struct CallWindowInfo: Codable {
    var sessionId: String = ""
    var phoneNumber: String = ""
    var isIncoming: Bool = false
    var isWebRtc: Bool = false
    var slot: String = "main"
    var transportLabel: String = ""
    var connectionLabel: String = ""
    var windowTitle: String = ""
    var callStartTime: Date = Date()
    var isOriginateCall: Bool = false
    var state: CallState = CallState()
}

struct Choice: Codable, Hashable, Identifiable {
    var key: String = ""
    var label: String = ""
    var id: String { key }
}

struct AudioDevice: Codable, Hashable, Identifiable {
    var index: Int = -1
    var name: String = ""
    var id: Int { index }
}

struct KommoOAuthStatus: Codable {
    var isAuthorized: Bool = false
    var statusText: String = "Not authorized"
    var expiresAt: Date?
    var isBusy: Bool = false
    var error: String?
}

struct SettingsFields: Codable {
    var mainName = "", mainTransport = "Sip", mainServer = "", mainPort = "5060"
    var mainUsername = "", mainPassword = ""
    var mainUseTls = false, mainUseSrtp = false
    var mainWsUri = "", mainWebRtcUsername = "", mainWebRtcPassword = ""
    var mainTurnUri = "", mainTurnUsername = "", mainTurnPassword = ""
    var secondaryName = "", secondaryTransport = "Sip", secondaryServer = "", secondaryRtpServer = ""
    var secondaryUsername = "", secondaryPassword = ""
    var secondaryUseTls = false, secondaryUseSrtp = false
    var secondaryWsUri = "", secondaryWebRtcUsername = "", secondaryWebRtcPassword = ""
    var secondaryTurnUri = "", secondaryTurnUsername = "", secondaryTurnPassword = ""
    var selectedMicrophone = -1, selectedSpeaker = -1
    var echoCancellation = false, ringtoneVolume = 0.7, ringtoneWav = false
    var sipCodec = "auto", sipSampleRate = 8000, sipOpusBitrate = 16000
    var gatewayEnabled = false, gatewayUrl = "", gatewayToken = "", gatewayExtension = ""
    var kommoEnabled = false, kommoSubdomain = "", kommoAuthMode = "manual", kommoToken = ""
    var kommoClientId = "", kommoClientSecret = "", kommoRedirectUri = ""
    var kommoLeadSelection = false, kommoRecordingUpload = false, kommoSource = "local"
    var theme = "system", callRecording = false, webRtcDebug = false
}

struct SettingsDto: Codable {
    var fields: SettingsFields = SettingsFields()
    // Flattened overlay — C# SettingsDto inherits fields. We decode both via a custom decoder? Easier: duplicate keys at top level.
    var mainName = "", mainTransport = "Sip", mainServer = "", mainPort = "5060"
    var mainUsername = "", mainPassword = ""
    var mainUseTls = false, mainUseSrtp = false
    var mainWsUri = "", mainWebRtcUsername = "", mainWebRtcPassword = ""
    var mainTurnUri = "", mainTurnUsername = "", mainTurnPassword = ""
    var secondaryName = "", secondaryTransport = "Sip", secondaryServer = "", secondaryRtpServer = ""
    var secondaryUsername = "", secondaryPassword = ""
    var secondaryUseTls = false, secondaryUseSrtp = false
    var secondaryWsUri = "", secondaryWebRtcUsername = "", secondaryWebRtcPassword = ""
    var secondaryTurnUri = "", secondaryTurnUsername = "", secondaryTurnPassword = ""
    var selectedMicrophone = -1, selectedSpeaker = -1
    var echoCancellation = false, ringtoneVolume = 0.7, ringtoneWav = false
    var sipCodec = "auto", sipSampleRate = 8000, sipOpusBitrate = 16000
    var gatewayEnabled = false, gatewayUrl = "", gatewayToken = "", gatewayExtension = ""
    var kommoEnabled = false, kommoSubdomain = "", kommoAuthMode = "manual", kommoToken = ""
    var kommoClientId = "", kommoClientSecret = "", kommoRedirectUri = ""
    var kommoLeadSelection = false, kommoRecordingUpload = false, kommoSource = "local"
    var theme = "system", callRecording = false, webRtcDebug = false
    var transportOptions: [Choice] = []
    var themeOptions: [Choice] = []
    var kommoAuthOptions: [Choice] = []
    var kommoSourceOptions: [Choice] = []
    var sipCodecOptions: [Choice] = []
    var sipSampleRateOptions: [Int] = []
    var microphones: [AudioDevice] = []
    var speakers: [AudioDevice] = []
    var audioBackendInfo = ""
    var recordingsFolder = "", logsFolder = "", settingsFile = ""
    var versionText = "", updateStatus = "", updateUrl = ""
    var updateAvailable = false, statusText = "", statusIsError = false, isBusy = false
    var kommoOAuth = KommoOAuthStatus()

    func asFields() -> SettingsFields {
        var f = SettingsFields()
        f.mainName = mainName; f.mainTransport = mainTransport; f.mainServer = mainServer; f.mainPort = mainPort
        f.mainUsername = mainUsername; f.mainPassword = mainPassword
        f.mainUseTls = mainUseTls; f.mainUseSrtp = mainUseSrtp
        f.mainWsUri = mainWsUri; f.mainWebRtcUsername = mainWebRtcUsername; f.mainWebRtcPassword = mainWebRtcPassword
        f.mainTurnUri = mainTurnUri; f.mainTurnUsername = mainTurnUsername; f.mainTurnPassword = mainTurnPassword
        f.secondaryName = secondaryName; f.secondaryTransport = secondaryTransport; f.secondaryServer = secondaryServer
        f.secondaryRtpServer = secondaryRtpServer; f.secondaryUsername = secondaryUsername; f.secondaryPassword = secondaryPassword
        f.secondaryUseTls = secondaryUseTls; f.secondaryUseSrtp = secondaryUseSrtp
        f.secondaryWsUri = secondaryWsUri; f.secondaryWebRtcUsername = secondaryWebRtcUsername; f.secondaryWebRtcPassword = secondaryWebRtcPassword
        f.secondaryTurnUri = secondaryTurnUri; f.secondaryTurnUsername = secondaryTurnUsername; f.secondaryTurnPassword = secondaryTurnPassword
        f.selectedMicrophone = selectedMicrophone; f.selectedSpeaker = selectedSpeaker
        f.echoCancellation = echoCancellation; f.ringtoneVolume = ringtoneVolume; f.ringtoneWav = ringtoneWav
        f.sipCodec = sipCodec; f.sipSampleRate = sipSampleRate; f.sipOpusBitrate = sipOpusBitrate
        f.gatewayEnabled = gatewayEnabled; f.gatewayUrl = gatewayUrl; f.gatewayToken = gatewayToken; f.gatewayExtension = gatewayExtension
        f.kommoEnabled = kommoEnabled; f.kommoSubdomain = kommoSubdomain; f.kommoAuthMode = kommoAuthMode; f.kommoToken = kommoToken
        f.kommoClientId = kommoClientId; f.kommoClientSecret = kommoClientSecret; f.kommoRedirectUri = kommoRedirectUri
        f.kommoLeadSelection = kommoLeadSelection; f.kommoRecordingUpload = kommoRecordingUpload; f.kommoSource = kommoSource
        f.theme = theme; f.callRecording = callRecording; f.webRtcDebug = webRtcDebug
        return f
    }
}

struct CallDetails: Codable {
    var phoneNumber = ""
    var callTime = Date()
    var ringbackStart: Date?
    var ringbackEnd: Date?
    var answerTime: Date?
    var wasAnswered = false
    var endedBy = ""
    var durationText = ""
    var recordingFilePath: String?
    var hasRecording = false
    var transportLabel = ""
    var outboundCallerId: String?
    var connectionName: String?
    var isIncoming = false
    var status = ""
    var technicalDetails: [String] = []
    var kommoEnabled = false
    var kommoUploadStatus = ""
    var kommoUploadReason: String?
    var kommoLeadId: Int64?
    var kommoSubdomain: String?
    var canRetryKommo = false
}

struct ConnectionSelectionRequest: Codable {
    var hasMain = false, isMainWebRtc = false
    var mainStatus: String?, mainName: String?
    var hasSecondary = false, isSecondaryWebRtc = false
    var secondaryStatus: String?, secondaryName: String?
    var mainCallerIds: [CallerIdItem] = []
    var selectedCallerId: String?
}

struct ConnectionSelectionResult: Codable {
    var slot: String?
    var callerId: String?
}

struct LeadSelectionResult: Codable {
    var proceed = false
    var cancelled = false
    var leadId: Int64?
}

struct KommoLead: Codable, Identifiable {
    var id: Int64
    var name: String
    var description: String
    var responsibleUserId: Int64?
}

struct KommoLeadPickerRequest: Codable {
    var subdomain: String?
    var phoneNumber: String?
    var isIncoming = false
    var durationSeconds = 0
    var wasAnswered = false
    var callTime: Date?
    var leads: [KommoLead] = []
}

struct MessageAlert: Identifiable {
    var id = UUID()
    var title: String
    var text: String
}

struct UpdateInfo: Codable, Identifiable {
    var id: String { version }
    var version = ""
    var currentVersion = ""
    var url = ""
    var sha256: String?
    var notes: String?
}

struct OkError: Codable {
    var ok: Bool?
    var error: String?
}
