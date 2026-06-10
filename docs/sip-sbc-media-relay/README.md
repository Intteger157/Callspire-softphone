## SIP SBC + media relay for Secondary connection

The Secondary connection in Callspire is **SIP-only** (SIPSorcery RTP), not WebRTC.
Because of that, **TURN does not apply** to this line: TURN is an ICE/WebRTC mechanism.

If the user is behind **symmetric NAT / CGNAT**, direct RTP often breaks (silence / one-way audio),
and you need a **public media relay** in the SIP path.

### Recommended architecture

- **Callspire (Secondary line)** registers to **your SBC** (public IP).
- **Your SBC** registers to / calls **Beeline SIP trunk**.
- RTP is anchored on the SBC via a media relay (e.g. **RTPengine**).

Typical stacks:

- **Kamailio + RTPengine** (common, flexible)
- **OpenSIPS + RTPengine**
- **FreeSWITCH** (B2BUA, heavier, but simpler mental model)

### What changes in Callspire

Nothing special is required in the app besides configuring the Secondary connection's:

- **Server**: your SBC FQDN / IP
- **Username/Password**: credentials issued by your SBC

Callspire then becomes a normal SIP UA, and NAT traversal is handled by the SBC + media relay.

### Notes for implementation (server-side)

At a minimum the SBC must:

- accept REGISTER from clients (digest auth),
- route outbound calls to the Beeline trunk,
- anchor RTP (rewrite SDP c=/m= lines to the relay public IP),
- support symmetric RTP and NAT keepalives.

If you want, we can add a `docker-compose.yml` reference setup (Kamailio + RTPengine)
and a checklist of required ports (SIP 5060/5061, RTP UDP range, etc.) tailored to your deployment.

