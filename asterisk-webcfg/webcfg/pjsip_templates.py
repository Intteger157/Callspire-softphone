from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class PjsipUser:
    extension: str
    password: str
    transport: str = "transport-tls"
    context: str = "from-internal"
    allow: str = "opus,alaw,ulaw,g722"
    max_contacts: int = 2
    remove_existing: bool = True
    direct_media: bool = False
    rewrite_contact: bool = True
    force_rport: bool = True
    rtp_symmetric: bool = True
    allow_subscribe: bool = False
    rtp_keepalive: int = 20
    media_encryption: str = "sdes"


def render_user_conf(user: PjsipUser) -> str:
    ext = user.extension.strip()
    endpoint = f"{ext}-endpoint"
    auth = f"{ext}-auth"
    aor = f"{ext}-aor"

    def yn(v: bool) -> str:
        return "yes" if v else "no"

    return (
        f"; Managed by asterisk-webcfg. Do not edit by hand.\n"
        f"\n"
        f"[{endpoint}]\n"
        f"type=endpoint\n"
        f"transport={user.transport}\n"
        f"context={user.context}\n"
        f"disallow=all\n"
        f"allow={user.allow}\n"
        f"auth={auth}\n"
        f"aors={aor}\n"
        f"direct_media={yn(user.direct_media)}\n"
        f"rewrite_contact={yn(user.rewrite_contact)}\n"
        f"force_rport={yn(user.force_rport)}\n"
        f"rtp_symmetric={yn(user.rtp_symmetric)}\n"
        f"allow_subscribe={yn(user.allow_subscribe)}\n"
        f"rtp_keepalive={user.rtp_keepalive}\n"
        f"media_encryption={user.media_encryption}\n"
        f"\n"
        f"[{auth}]\n"
        f"type=auth\n"
        f"auth_type=userpass\n"
        f"username={ext}\n"
        f"password={user.password}\n"
        f"\n"
        f"[{aor}]\n"
        f"type=aor\n"
        f"max_contacts={user.max_contacts}\n"
        f"remove_existing={yn(user.remove_existing)}\n"
    )

