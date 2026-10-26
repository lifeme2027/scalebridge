#!/usr/bin/env python3
"""Generates shared-vectors/vendor/xiaomi.json.

Frames are hand-built from the layout documented in NOTES.md (not captured from devices).
Expected values are computed independently of the C# parser using decimal.Decimal.
Run: python3 shared-vectors/vendor/generators/xiaomi.py > shared-vectors/vendor/xiaomi.json
"""
import json
from decimal import Decimal as D

LB_TO_KG = D("0.45359237")  # defined value
JIN_TO_KG = D("0.5")        # 1 jin (catty) = 500 g

def ts(y, mo, d, h, mi, s):
    return bytes([y & 0xFF, y >> 8, mo, d, h, mi, s])

def ts_str(y, mo, d, h, mi, s):
    return f"{y:04d}-{mo:02d}-{d:02d}T{h:02d}:{mi:02d}:{s:02d}"

def hexs(b):
    return " ".join(f"{x:02X}" for x in b)

def dec_str(x):
    s = format(x.normalize(), "f")
    return s

def weight(raw, unit):
    if unit == "Kg":
        orig = D(raw) / 200
        kg = orig
    elif unit == "Lb":
        orig = D(raw) / 100
        kg = orig * LB_TO_KG
    else:
        orig = D(raw) / 100
        kg = orig * JIN_TO_KG
    return dec_str(orig), dec_str(kg)

T = (2026, 10, 26, 8, 30, 15)
T_STR = ts_str(*T)

def v1(name, flags, raw, tsb=None, unit="Kg", source="advertisement-service-data", warnings=(), extra=b"", ts_ok=True, desc=""):
    tsb = tsb if tsb is not None else ts(*T)
    data = bytes([flags, raw & 0xFF, raw >> 8]) + tsb + extra
    stable = bool(flags & 0x20)
    removed = bool(flags & 0x80)
    orig, kg = weight(raw, unit)
    return {
        "name": name, "description": desc,
        "service": "181D", "characteristic": None if source == "advertisement-service-data" else "2A9D",
        "source": source, "frameType": "xiaomi-mi-scale-v1-10",
        "hex": hexs(data), "reject": False, "warnings": list(warnings),
        "expect": {
            "protocol": "XiaomiMiScale", "rawWeight": raw, "rawUnit": unit,
            "originalWeight": orig, "weightKg": kg,
            "timestamp": T_STR if ts_ok else None, "impedanceRaw": None,
            "stabilized": stable, "weightRemoved": removed, "isFinal": stable and not removed,
        },
    }

def v2(name, c0, c1, raw, imp=0, tsb=None, unit="Kg", warnings=(), extra=b"", ts_ok=True, desc=""):
    tsb = tsb if tsb is not None else ts(*T)
    data = bytes([c0, c1]) + tsb + bytes([imp & 0xFF, imp >> 8, raw & 0xFF, raw >> 8]) + extra
    stable = bool(c1 & 0x20)
    removed = bool(c1 & 0x80)
    has_imp = bool(c1 & 0x02)
    orig, kg = weight(raw, unit)
    return {
        "name": name, "description": desc,
        "service": "181B", "characteristic": None,
        "source": "advertisement-service-data", "frameType": "xiaomi-body-composition-scale-2-13",
        "hex": hexs(data), "reject": False, "warnings": list(warnings),
        "expect": {
            "protocol": "XiaomiBodyCompositionScale2", "rawWeight": raw, "rawUnit": unit,
            "originalWeight": orig, "weightKg": kg,
            "timestamp": T_STR if ts_ok else None, "impedanceRaw": imp if has_imp else None,
            "stabilized": stable, "weightRemoved": removed, "isFinal": stable and not removed,
        },
    }

def rej(name, service, frame, source, hexb, code, desc=""):
    return {"name": name, "description": desc, "service": service, "characteristic": None,
            "source": source, "frameType": frame, "hex": hexs(hexb), "reject": True, "errorCode": code}

BAD_TS = ts(2026, 13, 26, 8, 30, 15)  # month 13
AD = "advertisement-service-data"
V1F, V2F = "xiaomi-mi-scale-v1-10", "xiaomi-body-composition-scale-2-13"

vectors = [
    v1("xiaomi-v1-kg-stable", 0x22, 14000, desc="70.000 kg: raw 14000 / 200; stable, kg"),
    v1("xiaomi-v1-kg-stable-bit1-clear", 0x20, 14000, desc="same, flags 0x20 (bit1 clear): kg is 'neither unit bit set'"),
    v1("xiaomi-v1-kg-stable-notification", 0x22, 18700, source="notification", desc="93.500 kg via notification frame"),
    v1("xiaomi-v1-kg-minimum-step", 0x22, 1, desc="one LSB = 0.005 kg"),
    v1("xiaomi-v1-kg-zero", 0x22, 0, desc="zero weight is still a valid (stable) frame"),
    v1("xiaomi-v1-kg-max-raw", 0x22, 0xFFFF, desc="raw 65535 / 200 = 327.675 kg"),
    v1("xiaomi-v1-lb-stable", 0x21 | 0x20, 15432, unit="Lb", desc="154.32 lb * 0.45359237"),
    v1("xiaomi-v1-jin-stable", 0x30, 14000, unit="Jin", desc="140.00 jin = 70.000 kg"),
    v1("xiaomi-v1-jin-odd", 0x30, 12345, unit="Jin", desc="123.45 jin"),
    v1("xiaomi-v1-kg-unstable", 0x02, 14000, desc="not stabilized: parsed, isFinal=false"),
    v1("xiaomi-v1-kg-removed", 0xA2, 14000, desc="stabilized + weight removed: isFinal=false"),
    v1("xiaomi-v1-invalid-timestamp", 0x22, 14000, tsb=BAD_TS, ts_ok=False, warnings=["InvalidTimestamp"], desc="month 13 -> timestamp null"),
    v1("xiaomi-v1-zero-timestamp", 0x22, 14000, tsb=bytes(7), ts_ok=False, warnings=["InvalidTimestamp"], desc="all-zero timestamp is invalid"),
    v1("xiaomi-v1-trailing-bytes", 0x22, 14000, extra=b"\xAA", warnings=["TrailingBytes"], desc="11 bytes: extra byte ignored"),
    rej("xiaomi-v1-too-short-9", "181D", V1F, AD, bytes([0x22, 0x70, 0x36, 0xEA, 0x07, 0x0A, 0x1A, 0x08, 0x1E]), "TooShort"),
    rej("xiaomi-v1-empty", "181D", V1F, AD, b"", "TooShort"),
    rej("xiaomi-v1-ambiguous-unit", "181D", V1F, AD, bytes([0x31, 0xB0, 0x36]) + ts(*T), "AmbiguousUnit", "lb bit0 and jin bit4 both set"),
    v2("xiaomi-v2-kg-stable-impedance", 0x02, 0x22, 14000, imp=512, desc="70.000 kg, impedance 512, stable"),
    v2("xiaomi-v2-kg-stable-no-impedance", 0x02, 0x20, 14000, imp=0, desc="impedance bit clear -> impedanceRaw null even if field nonzero"),
    v2("xiaomi-v2-kg-stable-no-impedance-nonzero-field", 0x02, 0x20, 14000, imp=777, desc="impedance bit clear, bytes 9-10 = 777 ignored"),
    v2("xiaomi-v2-kg-removed", 0x02, 0xA2, 14000, imp=512, desc="removed: isFinal=false, impedance still reported"),
    v2("xiaomi-v2-kg-unstable", 0x02, 0x02, 14000, imp=300, desc="not stabilized"),
    v2("xiaomi-v2-kg-idle-zero", 0x02, 0x00, 0, imp=0, desc="idle frame"),
    v2("xiaomi-v2-lb-stable-impedance", 0x03, 0x22, 15432, imp=480, unit="Lb", desc="154.32 lb"),
    v2("xiaomi-v2-jin-stable-impedance", 0x02, 0x62, 14000, imp=480, unit="Jin", desc="140.00 jin = 70 kg"),
    v2("xiaomi-v2-kg-max-impedance", 0x02, 0x22, 14001, imp=0xFFFF, desc="impedance raw 65535 passes through"),
    v2("xiaomi-v2-invalid-timestamp", 0x02, 0x22, 14000, imp=512, tsb=BAD_TS, ts_ok=False, warnings=["InvalidTimestamp"]),
    v2("xiaomi-v2-trailing-bytes", 0x02, 0x22, 14000, imp=512, extra=b"\x00\x01", warnings=["TrailingBytes"]),
    rej("xiaomi-v2-too-short-12", "181B", V2F, AD, bytes([0x02, 0x22]) + ts(*T) + bytes([0x00, 0x02, 0xB0]), "TooShort"),
    rej("xiaomi-v2-empty", "181B", V2F, AD, b"", "TooShort"),
    rej("xiaomi-v2-ambiguous-unit", "181B", V2F, AD, bytes([0x03, 0x62]) + ts(*T) + bytes([0, 2, 0xB0, 0x36]), "AmbiguousUnit", "lb (ctrl0 bit0) and jin (ctrl1 bit6) both set"),
    rej("xiaomi-unsupported-service-180f", "180F", "xiaomi-service-data-dispatch", AD, bytes([1, 2, 3]), "UnsupportedService", "dispatcher: service other than 181D/181B"),
]

doc = {
    "format": "scalebridge-vectors/1",
    "suite": "vendor-xiaomi",
    "vendor": "xiaomi",
    "description": "Xiaomi Mi Scale (0x181D 10-byte frame) and Mi Body Composition Scale 2 (0x181B 13-byte frame). Hand-built from the layout in NOTES.md; EXPERIMENTAL, not verified on real hardware. See shared-vectors/vendor/VENDOR.md.",
    "experimental": True,
    "vectors": vectors,
}
print(json.dumps(doc, indent=2, ensure_ascii=False))
