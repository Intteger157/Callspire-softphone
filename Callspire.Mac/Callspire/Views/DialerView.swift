import SwiftUI

/// Dialer page — mirrors the WPF MainWindow dialer: line status rows, Kommo status, number field,
/// Caller ID picker, 3×4 keypad and the green call button (split into two when both lines are up).
struct DialerView: View {
    @EnvironmentObject var state: AppState
    @FocusState private var numberFocused: Bool
    private let keys = [["1","2","3"],["4","5","6"],["7","8","9"],["*","0","#"]]

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                Spacer(minLength: 24)
                statusRows

                if let banner = state.main.sipAuthFailureText, !banner.isEmpty {
                    Label(banner, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout).foregroundStyle(.red)
                        .padding(10)
                        .background(Color.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
                }

                numberField

                if state.main.showCallerIdPicker && !state.main.callerIds.isEmpty {
                    VStack(spacing: 4) {
                        Text("Caller ID").font(.caption).foregroundStyle(.secondary)
                        Picker("", selection: Binding(
                            get: { state.main.selectedCallerId ?? state.main.callerIds.first?.number ?? "" },
                            set: { state.selectCallerId($0) }
                        )) {
                            ForEach(state.main.callerIds) { c in Text(c.displayText).tag(c.number) }
                        }
                        .labelsHidden()
                        .frame(width: 260)
                    }
                }

                keypad

                HStack(spacing: 16) {
                    if state.main.showSplitCallButtons {
                        callButton(state.main.splitPrimaryLabel, slot: "main")
                        callButton(state.main.splitSecondaryLabel, slot: "secondary")
                    } else {
                        callButton(nil, slot: nil)
                    }
                }
                .padding(.top, 4)
                Spacer(minLength: 24)
            }
            .frame(maxWidth: 440)
            .frame(maxWidth: .infinity)
            .padding(.horizontal, 24)
        }
        .onAppear { numberFocused = true }
    }

    // MARK: pieces

    private var statusRows: some View {
        VStack(alignment: .leading, spacing: 6) {
            statusRow(state.main.main, slot: "main")
            if state.main.secondary.isConfigured {
                statusRow(state.main.secondary, slot: "secondary")
            }
            if state.main.amoCrmConfigured {
                HStack(spacing: 6) {
                    Text("AmoCRM:").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    StatusDot(tone: state.main.amoCrmOnline ? .online : .warning)
                    Text(state.main.amoCrmStatusText).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func statusRow(_ c: ConnectionStatus, slot: String) -> some View {
        HStack(spacing: 6) {
            Text("\(c.label.isEmpty ? (slot == "main" ? "Main" : "Secondary") : c.label):")
                .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            StatusDot(tone: c.isError ? .error : (c.isOnline ? .online : .offline))
            Text(c.text).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Button { state.reconnect(slot: slot) } label: {
                Image(systemName: "arrow.clockwise").font(.caption)
            }
            .buttonStyle(.plain).foregroundStyle(.secondary)
            .help("Reconnect")
        }
    }

    private var numberField: some View {
        VStack(spacing: 6) {
            HStack(spacing: 8) {
                TextField("Enter the number", text: Binding(
                    get: { state.main.phoneNumber },
                    set: { state.setPhone($0) }
                ))
                .textFieldStyle(.plain)
                .font(.system(size: 24, weight: .light))
                .multilineTextAlignment(.center)
                .focused($numberFocused)
                .onSubmit { state.placeCall() }

                Button {
                    if !state.main.phoneNumber.isEmpty { state.setPhone(String(state.main.phoneNumber.dropLast())) }
                } label: {
                    Image(systemName: "delete.left").font(.title3)
                }
                .buttonStyle(.plain).foregroundStyle(.secondary)
                .opacity(state.main.phoneNumber.isEmpty ? 0.3 : 1)
                .help("Backspace")
            }
            .padding(.horizontal, 32)
            Rectangle().fill(Color.accentColor).frame(height: 2).padding(.horizontal, 24)
        }
        .frame(maxWidth: 300)
    }

    private var keypad: some View {
        VStack(spacing: 10) {
            ForEach(keys, id: \.self) { row in
                HStack(spacing: 14) {
                    ForEach(row, id: \.self) { k in
                        Button { state.setPhone(state.main.phoneNumber + k) } label: {
                            Text(k)
                                .font(.system(size: 22, weight: .medium))
                                .frame(width: 56, height: 56)
                                .background(Circle().strokeBorder(Color(nsColor: .separatorColor), lineWidth: 1))
                                .contentShape(Circle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private func callButton(_ title: String?, slot: String?) -> some View {
        Button { state.placeCall(slot: slot) } label: {
            VStack(spacing: 2) {
                Image(systemName: "phone.fill").font(.system(size: 22, weight: .semibold))
                if let title, !title.isEmpty { Text(title).font(.caption2).lineLimit(1) }
            }
            .foregroundStyle(.white)
            .frame(width: title == nil ? 60 : 84, height: 60)
            .background(state.main.canCall ? Color.green : Color.gray.opacity(0.5), in: title == nil ? AnyShape(Circle()) : AnyShape(Capsule()))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!state.main.canCall)
        .keyboardShortcut(slot == "secondary" ? KeyboardShortcut(.return, modifiers: .shift) : .defaultAction)
        .help(state.main.canCall ? "Call" : "No connection")
    }
}