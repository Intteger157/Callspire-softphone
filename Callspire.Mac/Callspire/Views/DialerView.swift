import SwiftUI

/// Native macOS dialer — compact centered pad, system controls, SF Symbols.
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
            statusBar
                .padding(.top, 12)
                .padding(.horizontal, 20)

            Spacer(minLength: 12)

            VStack(spacing: 16) {
                if let banner = state.main.sipAuthFailureText, !banner.isEmpty {
                    Label(banner, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.red)
                        .padding(10)
                        .frame(maxWidth: MacTheme.contentMaxWidth)
                        .background(Color.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                }

                numberField
                callerIdPicker
                keypad
                callActions
            }
            .frame(maxWidth: MacTheme.contentMaxWidth)

            Spacer(minLength: 20)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { numberFocused = true }
    }

    // MARK: - Status

    private var statusBar: some View {
        VStack(alignment: .leading, spacing: 6) {
            connectionChip(state.main.main, slot: "main")
            if state.main.secondary.isConfigured {
                connectionChip(state.main.secondary, slot: "secondary")
            }
            if state.main.amoCrmConfigured {
                HStack(spacing: 8) {
                    Image(systemName: "person.crop.circle")
                        .foregroundStyle(.secondary)
                    Text("AmoCRM")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(.secondary)
                    StatusPill(
                        text: state.main.amoCrmStatusText.isEmpty ? (state.main.amoCrmOnline ? "Connected" : "Offline") : state.main.amoCrmStatusText,
                        tone: state.main.amoCrmOnline ? .online : .warning
                    )
                    Spacer(minLength: 0)
                }
            }
        }
    }

    private func connectionChip(_ c: ConnectionStatus, slot: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: c.isWebRtc ? "wifi" : "phone")
                .symbolRenderingMode(.hierarchical)
                .foregroundStyle(.secondary)
            Text(c.label.isEmpty ? (slot == "main" ? "Main" : "Secondary") : c.label)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            StatusPill(
                text: c.text.isEmpty ? (c.isOnline ? "Connected" : "Not connected") : c.text,
                tone: c.isError ? .error : (c.isOnline ? .online : .offline)
            )
            Spacer(minLength: 0)
            Button {
                state.reconnect(slot: slot)
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Reconnect")
            .controlSize(.small)
        }
    }

    // MARK: - Number

    private var numberField: some View {
        HStack(spacing: 8) {
            TextField("Enter the number", text: Binding(
                get: { state.main.phoneNumber },
                set: { state.setPhone($0) }
            ))
            .textFieldStyle(.plain)
            .font(.system(size: 26, weight: .light, design: .rounded))
            .multilineTextAlignment(.center)
            .focused($numberFocused)
            .onSubmit { state.placeCall() }

            Button {
                if !state.main.phoneNumber.isEmpty {
                    state.setPhone(String(state.main.phoneNumber.dropLast()))
                }
            } label: {
                Image(systemName: "delete.left.fill")
                    .symbolRenderingMode(.hierarchical)
                    .font(.title3)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .opacity(state.main.phoneNumber.isEmpty ? 0.25 : 1)
            .disabled(state.main.phoneNumber.isEmpty)
            .help("Backspace")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    @ViewBuilder
    private var callerIdPicker: some View {
        if state.main.showCallerIdPicker && !state.main.callerIds.isEmpty {
            Picker("Caller ID", selection: Binding(
                get: { state.main.selectedCallerId ?? state.main.callerIds.first?.number ?? "" },
                set: { state.selectCallerId($0) }
            )) {
                ForEach(state.main.callerIds) { c in
                    Text(c.displayText).tag(c.number)
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .frame(maxWidth: 280)
        }
    }

    // MARK: - Keypad / Call

    private var keypad: some View {
        let rows = stride(from: 0, to: keys.count, by: 3).map { Array(keys[$0..<min($0 + 3, keys.count)]) }
        return VStack(spacing: 10) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                HStack(spacing: 12) {
                    ForEach(row, id: \.0) { key in
                        KeypadKey(label: key.0, letters: key.1) {
                            state.setPhone(state.main.phoneNumber + key.0)
                        }
                    }
                }
            }
        }
    }

    private var callActions: some View {
        HStack(spacing: 20) {
            if state.main.showSplitCallButtons {
                CallActionButton(title: state.main.splitPrimaryLabel, enabled: state.main.canCall) {
                    state.placeCall(slot: "main")
                }
                .keyboardShortcut(.defaultAction)
                CallActionButton(title: state.main.splitSecondaryLabel, enabled: state.main.canCall) {
                    state.placeCall(slot: "secondary")
                }
                .keyboardShortcut(.return, modifiers: .shift)
            } else {
                CallActionButton(enabled: state.main.canCall) {
                    state.placeCall()
                }
                .keyboardShortcut(.defaultAction)
                .help(state.main.canCall ? "Call" : "No connection")
            }
        }
        .padding(.top, 4)
    }
}
