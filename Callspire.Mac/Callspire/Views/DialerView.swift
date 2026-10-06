import SwiftUI

/// Dialer — phone-style layout: line status, large number display, keypad, call button.
struct DialerView: View {
    @EnvironmentObject var state: AppState
    @FocusState private var numberFocused: Bool

    private let keys: [(String, String?)] = [
        ("1", nil), ("2", "ABC"), ("3", "DEF"),
        ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
        ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"),
        ("*", nil), ("0", "+"), ("#", nil),
    ]

    var body: some View {
        VStack(spacing: 0) {
            statusArea
                .padding(.top, 16)
                .padding(.horizontal, 24)

            Spacer(minLength: 16)

            VStack(spacing: 22) {
                numberDisplay
                keypad
                callActions
            }

            Spacer(minLength: 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { numberFocused = true }
    }

    // MARK: - Status

    @ViewBuilder
    private var statusArea: some View {
        let main = state.main.main
        VStack(spacing: 8) {
            if !main.isConfigured && !main.isOnline {
                setupBanner
            } else {
                lineRow(main, slot: "main")
                if state.main.secondary.isConfigured {
                    lineRow(state.main.secondary, slot: "secondary")
                }
            }
            if state.main.amoCrmConfigured {
                HStack(spacing: 6) {
                    StatusDot(tone: state.main.amoCrmOnline ? .online : .warning)
                    Text("Kommo")
                        .font(.caption.weight(.medium))
                    Text(state.main.amoCrmStatusText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let banner = state.main.sipAuthFailureText, !banner.isEmpty {
                Label(banner, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.red)
            }
        }
        .frame(maxWidth: .infinity)
    }

    private var setupBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "phone.badge.plus")
                .font(.title2)
                .symbolRenderingMode(.hierarchical)
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text("No line configured").font(.callout.weight(.semibold))
                Text("Add your SIP or WebRTC account to start calling.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            Button("Set Up…") { state.openSettings(panel: .connection) }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
        }
        .padding(12)
        .frame(maxWidth: 440)
        .background(MacTheme.controlFill, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func lineRow(_ c: ConnectionStatus, slot: String) -> some View {
        HStack(spacing: 6) {
            StatusDot(tone: c.isError ? .error : (c.isOnline ? .online : .offline))
            Text(c.label.isEmpty ? (slot == "main" ? "Main line" : "Second line") : c.label)
                .font(.caption.weight(.medium))
            Text(c.text.isEmpty ? (c.isOnline ? "Connected" : "Not connected") : c.text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            if !c.isOnline {
                Button("Reconnect") { state.reconnect(slot: slot) }
                    .buttonStyle(.link)
                    .font(.caption)
            }
        }
    }

    // MARK: - Number

    private var numberDisplay: some View {
        VStack(spacing: 10) {
            ZStack {
                TextField("", text: Binding(
                    get: { state.main.phoneNumber },
                    set: { state.setPhone($0) }
                ), prompt: Text("Enter number").foregroundColor(.secondary.opacity(0.6)))
                .textFieldStyle(.plain)
                .font(.system(size: 34, weight: .light))
                .multilineTextAlignment(.center)
                .focused($numberFocused)
                .onSubmit { state.placeCall() }
                .padding(.horizontal, 44)

                HStack {
                    Spacer()
                    if !state.main.phoneNumber.isEmpty {
                        Button {
                            state.setPhone(String(state.main.phoneNumber.dropLast()))
                        } label: {
                            Image(systemName: "delete.left")
                                .font(.system(size: 18))
                                .foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Delete")
                        .transition(.opacity)
                    }
                }
            }
            .frame(width: 300, height: 44)

            if state.main.showCallerIdPicker && !state.main.callerIds.isEmpty {
                Menu {
                    ForEach(state.main.callerIds) { c in
                        Button(c.displayText) { state.selectCallerId(c.number) }
                    }
                } label: {
                    Label(callerIdLabel, systemImage: "person.text.rectangle")
                        .font(.caption)
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("Outbound Caller ID")
            }
        }
        .animation(.easeOut(duration: 0.12), value: state.main.phoneNumber.isEmpty)
    }

    private var callerIdLabel: String {
        let sel = state.main.selectedCallerId ?? state.main.callerIds.first?.number
        return state.main.callerIds.first(where: { $0.number == sel })?.displayText ?? "Caller ID"
    }

    // MARK: - Keypad / Call

    private var keypad: some View {
        let rows = stride(from: 0, to: keys.count, by: 3).map { Array(keys[$0..<min($0 + 3, keys.count)]) }
        return VStack(spacing: 14) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                HStack(spacing: 22) {
                    ForEach(row, id: \.0) { key in
                        KeypadKey(label: key.0, letters: key.1) {
                            state.setPhone(state.main.phoneNumber + key.0)
                            numberFocused = true
                        }
                    }
                }
            }
        }
    }

    private var callActions: some View {
        HStack(spacing: 16) {
            if state.main.showSplitCallButtons {
                CallActionButton(title: state.main.splitPrimaryLabel, enabled: canDial) {
                    state.placeCall(slot: "main")
                }
                .keyboardShortcut(.defaultAction)
                CallActionButton(title: state.main.splitSecondaryLabel, enabled: canDial) {
                    state.placeCall(slot: "secondary")
                }
                .keyboardShortcut(.return, modifiers: .shift)
            } else {
                CallActionButton(enabled: canDial) { state.placeCall() }
                    .keyboardShortcut(.defaultAction)
                    .help(state.main.canCall ? "Call" : "No connection")
            }
        }
    }

    private var canDial: Bool {
        state.main.canCall && !state.main.phoneNumber.trimmingCharacters(in: .whitespaces).isEmpty
    }
}
