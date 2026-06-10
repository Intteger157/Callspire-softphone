export type Me = {
  email: string
  extension: string
  must_change_password?: boolean
}

export type LoginResponse = {
  ok: true
  me: Me
}

export type CallerIdItem = {
  number: string
  name?: string
}

export type MyCallerIds = {
  extension: string
  callerid_items: CallerIdItem[]
}

export type OriginateResponse = {
  success: true
  originate_id: string
}

export type CdrRecord = {
  src_num: string
  dst_num: string
  caller_id: string
  start: string
  answer: string
  duration: number
  billsec: number
  disposition: string
  recording: string
  linkedid: string
  trunk: string
  src_call_id: string
}

export type CdrResponse = {
  result: boolean
  data: CdrRecord[]
}

export type WebRtcIceServerDto = {
  urls: string
  username?: string
  credential?: string
}

/** WebRTC/SIP settings from BFF. `sipPassword` may be filled from MikoPBX m_Sip for the mapped extension (via CDR proxy). */
export type WebRtcConfig = {
  wsUrl: string
  sipHost: string
  extension: string
  iceServers: WebRtcIceServerDto[]
  sipPassword?: string
  /** Digest user for REGISTER (Miko: often `ext` or `ext-WS` — must match PJSIP auth). */
  sipAuthUser?: string
  /** SIP AoR user-part for WebRTC (REGISTER URI). When set, overrides `extension` + `-WS` heuristics. */
  sipContactUserPart?: string
}

