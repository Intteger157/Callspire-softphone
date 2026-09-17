import SwiftUI

struct DialerView: View {
    @EnvironmentObject var state: AppState
    private let keys = [["1","2","3"],["4","5","6"],["7","8","9"],["*","0","#"]]

    var body: some View {
        VStack(spacing: 16) {
            statusBlock(state.main.main, slot: "main")
            if state.main.secondary.isConfigured {
                statusBlock(state.main.secondary, slot: "secondary")
            }
            if state.main.amoCrmConfigured {
                HStack {
                    Circle().fill(state.main.amoCrmOnline ? Color.green : Color.orange).frame(width: 8, height: 8)
                    Text("Kommo").font(.caption.weight(.semibold))
                    Text(state.main.amoCrmStatusText).font(.caption).foregroundStyle(.secondary)
                    Spacer()
                }
                .padding(.horizontal, 24)
            }
            if let banner = state.main.sipAuthFailureText, !banner.isEmpty {
                Text(banner).font(.caption).foregroundStyle(.red).padding(.horizontal)
            }

            HStack {
                TextField("Enter the number", text: Binding(
                    get: { state.main.phoneNumber },
                    set: { state.setPhone($0) }
                ))
                .textFieldStyle(.plain)
                .font(.title2)
                .multilineTextAlignment(.center)
                Button {
                    if !state.main.phoneNumber.isEmpty {
                        state.setPhone(String(state.main.phoneNumber.dropLast()))
                    }
                } label: { Image(systemName: "delete.left") }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 48)
            .overlay(alignment: .bottom) { Rectangle().fill(Color.accentColor).frame(height: 2) }
            .padding(.top, 8)

            if state.main.showCallerIdPicker {
                Picker("Caller ID", selection: Binding(
                    get: { state.main.selectedCallerId ?? "" },
                    set: { v in
                        state.main.selectedCallerId = v
                        Task { try? await state.ipc.requestVoid("selectCallerId", params: ["number": v]) }
                    }
                )) {
                    ForEach(state.main.callerIds) { c in Text(c.displayText).tag(c.number) }
                }
                .frame(width: 260)
            }

            VStack(spacing: 8) {
                ForEach(keys, id: \.self) { row in
                    HStack(spacing: 12) {
                        ForEach(row, id: \.self) { k in
                            Button {
                                state.setPhone(state.main.phoneNumber + k)
                            } label: {
                                Text(k).font(.title).frame(width: 64, height: 64)
                                    .background(Color.primary.opacity(0.08), in: Circle())
                            }.buttonStyle(.plain)
                        }
                    }
                }
            }

            HStack(spacing: 12) {
                if state.main.showSplitCallButtons {
                    callButton(state.main.splitPrimaryLabel, slot: "main")
                    callButton(state.main.splitSecondaryLabel, slot: "secondary")
                } else {
                    callButton("Call", slot: nil)
                }
            }
        }
        .frame(maxWidth: 420)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func statusBlock(_ c: ConnectionStatus, slot: String) -> some View {
        HStack {
            Text(c.label.isEmpty ? slot : c.label).font(.caption.weight(.semibold)).frame(width: 80, alignment: .leading)
            Circle().fill(c.isError ? Color.red : (c.isOnline ? Color.green : Color.secondary)).frame(width: 8, height: 8)
            Text(c.text).font(.caption).foregroundStyle(.secondary)
            Spacer()
            Button { state.reconnect(slot: slot) } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.plain).help("Reconnect")
        }
        .padding(.horizontal, 24)
    }

    private func callButton(_ title: String, slot: String?) -> some View {
        Button { state.placeCall(slot: slot) } label: {
            VStack {
                Image(systemName: "phone.fill")
                if state.main.showSplitCallButtons { Text(title).font(.caption2) }
            }
            .foregroundStyle(.white)
            .frame(width: state.main.showSplitCallButtons ? 72 : 64, height: 64)
            .background(state.main.canCall ? Color.green : Color.gray, in: Circle())
        }
        .buttonStyle(.plain)
        .disabled(!state.main.canCall)
    }
}
