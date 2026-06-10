## MikoPBX dialplan patches (Callspire)

This folder contains **drop-in Asterisk dialplan fragments** that we use to patch / extend MikoPBX behaviour for Callspire-related integrations.

These files are **not** consumed by the Windows softphone directly. They are stored here so the team can:

- track the exact dialplan changes in source control,
- copy/paste them into the PBX (usually via the MikoPBX “custom dialplan” mechanism),
- and reproduce the same behaviour across test/stage/prod PBXs.

### Contents

- `telnyx-preserve-plus-e164.conf`: Fixes outbound routing where MikoPBX strips the leading `+` from an E.164 destination number (e.g. `+372…`). When the trunk/provider expects E.164, losing the `+` can cause the provider to “guess” the region and route the call incorrectly (e.g. prepending a default country code).

